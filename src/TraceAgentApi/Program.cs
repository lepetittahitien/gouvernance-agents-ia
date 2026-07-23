using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OllamaSharp;
using TraceAgentApi.Audit;
using TraceAgentApi.Compliance;
using TraceAgentApi.Evals;
using TraceAgentApi.Policies;
using TraceAgentApi.Search;
using TraceAgentApi.Trace;
using TraceAgentApi.Trace.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var ollamaUri = builder.Configuration["Ollama:Uri"] ?? "http://localhost:11434";
var ollamaModel = builder.Configuration["Ollama:Model"] ?? "llama3.2";

var ollamaEmbeddingModel = builder.Configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

builder.Services.AddSingleton<IChatClient>(_ => new OllamaApiClient(new Uri(ollamaUri), ollamaModel));
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
    new OllamaApiClient(new Uri(ollamaUri), ollamaEmbeddingModel));
builder.Services.AddDbContext<TraceDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("TraceDb"), o => o.UseVector()));
builder.Services.AddScoped<AgentRunner>();
builder.Services.AddScoped<TraceQueryService>();
builder.Services.AddScoped<EvalStore>();
builder.Services.AddScoped<EvalRunner>();
builder.Services.AddScoped<BudgetMonitor>();
builder.Services.AddScoped<AuditLogger>();
builder.Services.AddScoped<ToolPolicyEvaluator>();
builder.Services.AddScoped<ComplianceExporter>();
builder.Services.AddScoped<ExternalScanStore>();
builder.Services.AddScoped<TraceSearchService>();
builder.Services.AddScoped<ChunkedTraceSearchService>();
builder.Services.AddScoped<RetrievalEvaluator>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/agent/run", async (AgentRunRequest request, AgentRunner runner, CancellationToken cancellationToken) =>
{
    var trace = await runner.RunAsync(request.Prompt, cancellationToken);
    return Results.Ok(trace);
})
.WithName("RunAgent")
.WithOpenApi();

app.MapGet("/agent/runs", async (TraceQueryService queries, CancellationToken cancellationToken) =>
{
    var runs = await queries.ListRunsAsync(cancellationToken);
    return Results.Ok(runs);
})
.WithName("ListAgentRuns")
.WithOpenApi();

app.MapGet("/agent/runs/{runId:guid}", async (Guid runId, TraceQueryService queries, CancellationToken cancellationToken) =>
{
    var trace = await queries.GetRunAsync(runId, cancellationToken);
    return trace is null ? Results.NotFound() : Results.Ok(trace);
})
.WithName("GetAgentRun")
.WithOpenApi();

// Garde-fou PII découplé : n'importe quel système tiers (proxy, webhook, script d'ingestion de logs)
// peut appeler cet endpoint avec du texte brut, sans passer par notre boucle d'orchestration d'agent.
// Le verdict est persisté (jamais le texte) pour remonter dans le dashboard.
app.MapPost("/scan", async (ScanRequest request, ExternalScanStore store, CancellationToken cancellationToken) =>
{
    var findings = PiiScanner.Scan(request.Text);
    var findingsByType = findings
        .GroupBy(f => f.Type)
        .ToDictionary(g => g.Key, g => g.Count());

    await store.RecordAsync(
        ExternalScanKind.Pii, request.Source, findings.Count > 0,
        new { findingsByType = findingsByType.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) },
        cancellationToken);

    return Results.Ok(new ScanResponse(findings.Count > 0, findings, findingsByType));
})
.WithName("ScanText")
.WithOpenApi();

// Garde-fou format découplé : valide qu'une sortie (de n'importe quel agent) respecte un schéma JSON attendu.
app.MapPost("/validate", async (ValidateRequest request, ExternalScanStore store, CancellationToken cancellationToken) =>
{
    var result = SchemaValidator.Validate(request.Output, request.Schema);

    await store.RecordAsync(
        ExternalScanKind.SchemaValidation, request.Source, !result.IsValid,
        new
        {
            isValid = result.IsValid,
            parseError = result.ParseError,
            violationPaths = result.Violations.Select(v => v.Path).Distinct().ToList(),
        },
        cancellationToken);

    return Results.Ok(result);
})
.WithName("ValidateOutput")
.WithOpenApi();

// Garde-fou injection découplé — s'applique à une ENTRÉE (prompt utilisateur, résultat d'outil,
// document récupéré), contrairement à /scan et /validate qui portent sur une sortie.
app.MapPost("/detect-injection", async (DetectInjectionRequest request, ExternalScanStore store, CancellationToken cancellationToken) =>
{
    var result = InjectionDetector.Scan(request.Input);

    // Persistance sans les extraits : ils citent l'entrée scannée, donc la donnée du client.
    await store.RecordAsync(
        ExternalScanKind.InjectionDetection, request.Source, result.RiskLevel != InjectionRiskLevel.None,
        new
        {
            riskLevel = result.RiskLevel.ToString(),
            score = result.Score,
            signalKinds = result.Signals
                .GroupBy(s => s.Kind)
                .ToDictionary(g => g.Key.ToString(), g => g.Count()),
        },
        cancellationToken);

    return Results.Ok(result);
})
.WithName("DetectInjection")
.WithOpenApi();

// Historique des scans externes — ce que les systèmes tiers ont fait vérifier.
app.MapGet("/external-scans", async (int? limit, ExternalScanStore store, CancellationToken cancellationToken) =>
{
    var scans = await store.ListAsync(limit ?? 100, cancellationToken);
    return Results.Ok(scans);
})
.WithName("ListExternalScans")
.WithOpenApi();

// Evals (T3) : rejeu du jeu d'évals + score + comparaison au run précédent.
app.MapPost("/evals/run", async (EvalRunner runner, CancellationToken cancellationToken) =>
{
    var report = await runner.RunAsync(cancellationToken);
    return Results.Ok(report);
})
.WithName("RunEvals")
.WithOpenApi();

// Recherche sémantique sur les traces (T5).
app.MapPost("/search/index", async (
    string? strategy, TraceSearchService search, ChunkedTraceSearchService chunked, CancellationToken cancellationToken) =>
{
    var report = string.Equals(strategy, "naive", StringComparison.OrdinalIgnoreCase)
        ? await search.IndexPendingAsync(cancellationToken)
        : await chunked.IndexPendingAsync(cancellationToken);
    return Results.Ok(report);
})
.WithName("IndexRuns")
.WithOpenApi();

app.MapGet("/search", async (
    string q, int? limit, string? strategy,
    TraceSearchService search, ChunkedTraceSearchService chunked, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest(new { error = "Le paramètre « q » est requis." });
    }

    if (string.Equals(strategy, "naive", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(await search.SearchAsync(q, limit ?? 5, cancellationToken));
    }

    return Results.Ok(await chunked.SearchAsync(q, limit ?? 5, cancellationToken));
})
.WithName("SearchRuns")
.WithOpenApi();

app.MapPost("/search/evaluate", async (
    int? k, string? strategy, RetrievalEvaluator evaluator, CancellationToken cancellationToken) =>
{
    var report = await evaluator.RunAsync(k ?? 5, strategy ?? "chunked", cancellationToken);
    return Results.Ok(report);
})
.WithName("EvaluateRetrieval")
.WithOpenApi();

app.MapGet("/search/similar/{runId:guid}", async (
    Guid runId, int? limit, TraceSearchService search, CancellationToken cancellationToken) =>
{
    var hits = await search.FindSimilarAsync(runId, limit ?? 5, cancellationToken);
    return Results.Ok(hits);
})
.WithName("FindSimilarRuns")
.WithOpenApi();

// Export de conformité (T4) : historique complet d'une période, avec preuve d'intégrité jointe.
// `redactPii=true` caviarde les données personnelles — un document d'audit ne doit pas
// devenir lui-même un véhicule de fuite.
app.MapGet("/compliance/export", async (
    DateTimeOffset? from,
    DateTimeOffset? to,
    string? format,
    bool? redactPii,
    ComplianceExporter exporter,
    CancellationToken cancellationToken) =>
{
    var periodTo = to ?? DateTimeOffset.UtcNow;
    var periodFrom = from ?? periodTo.AddDays(-30);

    if (periodFrom > periodTo)
    {
        return Results.BadRequest(new { error = "La date de début doit précéder la date de fin." });
    }

    var export = await exporter.BuildAsync(periodFrom, periodTo, redactPii ?? false, cancellationToken);

    if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
    {
        var csv = ComplianceExporter.ToCsv(export);
        var fileName = $"audit-{periodFrom:yyyyMMdd}-{periodTo:yyyyMMdd}.csv";
        // BOM UTF-8 : sans lui, Excel affiche les accents en mojibake.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", fileName);
    }

    return Results.Ok(export);
})
.WithName("ExportCompliance")
.WithOpenApi();

// Évaluation d'une règle d'accès sans exécuter l'agent — permet de tester une politique
// (« est-ce que cet agent aurait le droit de faire ça ? ») depuis un système tiers.
app.MapPost("/policies/evaluate", (PolicyEvaluateRequest request, ToolPolicyEvaluator evaluator) =>
{
    var evaluation = evaluator.Evaluate(request.AgentId, request.Tool, request.Arguments);
    return Results.Ok(evaluation);
})
.WithName("EvaluatePolicy")
.WithOpenApi();

// Journal d'audit (T4) : consultation et vérification d'intégrité de la chaîne.
app.MapGet("/audit/entries", async (Guid? runId, AuditLogger audit, CancellationToken cancellationToken) =>
{
    var entries = await audit.ListAsync(runId, cancellationToken);
    return Results.Ok(entries);
})
.WithName("ListAuditEntries")
.WithOpenApi();

app.MapGet("/audit/verify", async (AuditLogger audit, CancellationToken cancellationToken) =>
{
    var verification = await audit.VerifyChainAsync(cancellationToken);
    return Results.Ok(verification);
})
.WithName("VerifyAuditChain")
.WithOpenApi();

// Consultation du budget tokens sur la période glissante — branchable sur un monitoring externe.
app.MapGet("/budget/status", async (BudgetMonitor monitor, CancellationToken cancellationToken) =>
{
    var status = await monitor.EvaluateAsync(cancellationToken: cancellationToken);
    return Results.Ok(status);
})
.WithName("GetBudgetStatus")
.WithOpenApi();

app.MapGet("/evals/runs", async (EvalStore store, CancellationToken cancellationToken) =>
{
    var runs = await store.ListAsync(cancellationToken);
    return Results.Ok(runs);
})
.WithName("ListEvalRuns")
.WithOpenApi();

app.MapGet("/evals/runs/{evalRunId:guid}", async (Guid evalRunId, EvalStore store, CancellationToken cancellationToken) =>
{
    var report = await store.GetAsync(evalRunId, cancellationToken);
    return report is null ? Results.NotFound() : Results.Ok(report);
})
.WithName("GetEvalRun")
.WithOpenApi();

app.Run();

record AgentRunRequest(string Prompt);
record ScanRequest(string Text, string? Source = null);
record ScanResponse(bool HasPiiViolation, List<PiiFinding> Findings, IReadOnlyDictionary<PiiType, int> FindingsByType);
record ValidateRequest(string Output, JsonElement Schema, string? Source = null);
record DetectInjectionRequest(string Input, string? Source = null);
record PolicyEvaluateRequest(string AgentId, string Tool, Dictionary<string, object?>? Arguments = null);
