using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using TraceAgentApi.Audit;
using TraceAgentApi.Policies;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Trace;

public class AgentRunner
{
    private const string ModelName = "llama3.2";
    private const int MaxToolIterations = 5;

    private readonly IChatClient _chatClient;
    private readonly TraceDbContext _dbContext;
    private readonly BudgetMonitor _budgetMonitor;
    private readonly AuditLogger _auditLogger;
    private readonly ToolPolicyEvaluator _policyEvaluator;
    private readonly ILogger<AgentRunner> _logger;
    private readonly string _weatherServerProjectPath;

    public AgentRunner(
        IChatClient chatClient,
        TraceDbContext dbContext,
        BudgetMonitor budgetMonitor,
        AuditLogger auditLogger,
        ToolPolicyEvaluator policyEvaluator,
        ILogger<AgentRunner> logger,
        IConfiguration configuration)
    {
        _chatClient = chatClient;
        _dbContext = dbContext;
        _budgetMonitor = budgetMonitor;
        _auditLogger = auditLogger;
        _policyEvaluator = policyEvaluator;
        _logger = logger;
        _weatherServerProjectPath = configuration["McpWeatherServer:ProjectPath"]
            ?? throw new InvalidOperationException("Configuration manquante: McpWeatherServer:ProjectPath");
    }

    public async Task<AgentRunTrace> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var steps = new List<TraceStep>();
        var totalStopwatch = Stopwatch.StartNew();

        await _auditLogger.AppendAsync(
            AuditActorType.Agent, ModelName, AuditAction.RunStarted,
            resourceType: "AgentRun", resourceId: runId.ToString(),
            details: $"prompt: {prompt}", cancellationToken: cancellationToken);

        // 1. Démarrage du serveur MCP météo comme sous-processus (transport stdio).
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "weather",
            Command = "dotnet",
            Arguments = ["run", "--project", _weatherServerProjectPath, "--no-build"],
            StandardErrorLines = line => _logger.LogWarning("[mcp-weather stderr] {Line}", line),
        });

        await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        var tools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

        _logger.LogInformation("Outils MCP disponibles: {Tools}", string.Join(", ", tools.Select(t => t.Name)));

        List<ChatMessage> messages = [new(ChatRole.User, prompt)];
        ChatOptions options = new() { Tools = [.. tools] };

        int totalInputTokens = 0;
        int totalOutputTokens = 0;
        string finalAnswer = string.Empty;

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var modelStopwatch = Stopwatch.StartNew();
            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
            modelStopwatch.Stop();

            var inputTokens = (int?)(response.Usage?.InputTokenCount ?? 0);
            var outputTokens = (int?)(response.Usage?.OutputTokenCount ?? 0);
            totalInputTokens += inputTokens ?? 0;
            totalOutputTokens += outputTokens ?? 0;

            var functionCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            var stepDetail = functionCalls.Count > 0
                ? $"le modèle demande {functionCalls.Count} appel(s) d'outil: {string.Join(", ", functionCalls.Select(f => f.Name))}"
                : $"réponse finale: {response.Text}";

            steps.Add(new TraceStep(
                Index: steps.Count + 1,
                Kind: TraceStepKind.ModelCall,
                Label: $"appel modèle ({ModelName})",
                Detail: stepDetail,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                DurationMs: modelStopwatch.ElapsedMilliseconds));

            messages.AddMessages(response);

            if (functionCalls.Count == 0)
            {
                finalAnswer = response.Text;
                break;
            }

            foreach (var call in functionCalls)
            {
                var tool = tools.First(t => t.Name == call.Name);
                var argsLabel = string.Join(", ", call.Arguments?.Select(kv => $"{kv.Key}={kv.Value}") ?? []);

                // Contrôle d'accès (T4) : premier garde-fou qui *bloque* au lieu d'observer.
                // Évalué AVANT l'invocation — un appel refusé ne part jamais.
                var policy = _policyEvaluator.Evaluate(ModelName, call.Name, call.Arguments);

                if (policy.Decision == PolicyDecision.Deny)
                {
                    _logger.LogWarning("Appel d'outil refusé — {Reason}", policy.Reason);

                    steps.Add(new TraceStep(
                        Index: steps.Count + 1,
                        Kind: TraceStepKind.PolicyDenial,
                        Label: $"REFUSÉ {call.Name}({argsLabel})",
                        Detail: policy.Reason,
                        InputTokens: null,
                        OutputTokens: null,
                        DurationMs: 0));

                    await _auditLogger.AppendAsync(
                        AuditActorType.System, "ToolPolicy", AuditAction.ToolDenied,
                        resourceType: "Tool", resourceId: runId.ToString(),
                        details: $"{call.Name}({argsLabel}) — {policy.Reason}",
                        cancellationToken: cancellationToken);

                    // On renvoie le refus au modèle plutôt que d'interrompre : il peut alors
                    // l'expliquer à l'utilisateur au lieu de rester bloqué à réessayer.
                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(call.CallId, $"Appel refusé par la politique d'accès : {policy.Reason}")]));

                    continue;
                }

                var toolStopwatch = Stopwatch.StartNew();

                object? result;
                try
                {
                    result = await tool.InvokeAsync(new AIFunctionArguments(call.Arguments), cancellationToken);
                }
                catch (Exception ex)
                {
                    result = $"Erreur lors de l'appel de l'outil: {ex.Message}";
                }

                toolStopwatch.Stop();

                steps.Add(new TraceStep(
                    Index: steps.Count + 1,
                    Kind: TraceStepKind.ToolCall,
                    Label: $"outil {call.Name}({argsLabel})",
                    Detail: $"résultat: {result}",
                    InputTokens: null,
                    OutputTokens: null,
                    DurationMs: toolStopwatch.ElapsedMilliseconds));

                await _auditLogger.AppendAsync(
                    AuditActorType.Agent, ModelName, AuditAction.ToolInvoked,
                    resourceType: "Tool", resourceId: runId.ToString(),
                    details: $"{call.Name}({argsLabel})",
                    cancellationToken: cancellationToken);

                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
            }
        }

        totalStopwatch.Stop();

        // Garde-fou PII (T2) : on scanne la réponse finale ET chaque étape intermédiaire,
        // une fuite peut se produire dans un résultat d'outil avant même la réponse finale.
        var piiFindings = PiiScanner.Scan(finalAnswer);
        foreach (var step in steps)
        {
            piiFindings.AddRange(PiiScanner.Scan(step.Detail));
        }
        var piiFindingsByType = piiFindings
            .GroupBy(f => f.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        if (piiFindings.Count > 0)
        {
            var breakdown = string.Join(", ", piiFindingsByType.Select(kv => $"{kv.Key}={kv.Value}"));
            _logger.LogWarning(
                "Run {RunId} : {Count} fuite(s) PII détectée(s) — {Breakdown}",
                runId, piiFindings.Count, breakdown);

            // Le journal d'audit ne consigne que le type et le nombre — jamais la valeur détectée
            // (même règle de confidentialité que la persistance des runs, cf. T2).
            await _auditLogger.AppendAsync(
                AuditActorType.System, "PiiScanner", AuditAction.GuardrailViolation,
                resourceType: "AgentRun", resourceId: runId.ToString(),
                details: $"PII détectée — {breakdown}", cancellationToken: cancellationToken);
        }

        var trace = new AgentRunTrace(
            RunId: runId,
            Prompt: prompt,
            ModelName: ModelName,
            StartedAt: startedAt,
            Steps: steps,
            FinalAnswer: finalAnswer,
            TotalInputTokens: totalInputTokens,
            TotalOutputTokens: totalOutputTokens,
            TotalDurationMs: totalStopwatch.ElapsedMilliseconds,
            // Ollama tourne en local : pas de facturation réelle. Le champ reste à 0€
            // mais existe déjà pour brancher un vrai barème (Anthropic, OpenAI...) plus tard.
            EstimatedCostEur: 0m,
            HasPiiViolation: piiFindings.Count > 0,
            PiiFindingsByType: piiFindingsByType);

        await PersistAsync(trace, cancellationToken);

        // Après persistance : le run compte alors dans le cumul de la période.
        var budget = await _budgetMonitor.EvaluateAsync(trace, cancellationToken);
        trace = trace with { Budget = budget };

        foreach (var breach in budget.Breaches)
        {
            await _auditLogger.AppendAsync(
                AuditActorType.System, "BudgetMonitor", AuditAction.BudgetBreach,
                resourceType: "AgentRun", resourceId: runId.ToString(),
                details: breach.Message, cancellationToken: cancellationToken);
        }

        await _auditLogger.AppendAsync(
            AuditActorType.Agent, ModelName, AuditAction.RunCompleted,
            resourceType: "AgentRun", resourceId: runId.ToString(),
            details: $"{totalInputTokens + totalOutputTokens} tokens, {totalStopwatch.ElapsedMilliseconds} ms",
            cancellationToken: cancellationToken);

        TraceWriter.Print(trace);

        return trace;
    }

    private async Task PersistAsync(AgentRunTrace trace, CancellationToken cancellationToken)
    {
        var entity = new AgentRunEntity
        {
            Id = trace.RunId,
            Prompt = trace.Prompt,
            ModelName = trace.ModelName,
            StartedAt = trace.StartedAt,
            FinalAnswer = trace.FinalAnswer,
            TotalInputTokens = trace.TotalInputTokens,
            TotalOutputTokens = trace.TotalOutputTokens,
            TotalDurationMs = trace.TotalDurationMs,
            EstimatedCostEur = trace.EstimatedCostEur,
            HasPiiViolation = trace.HasPiiViolation,
            PiiSummaryJson = trace.PiiFindingsByType.Count > 0
                ? JsonSerializer.Serialize(trace.PiiFindingsByType.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value))
                : null,
            Steps = trace.Steps.Select(s => new TraceStepEntity
            {
                Index = s.Index,
                Kind = s.Kind,
                Label = s.Label,
                Detail = s.Detail,
                InputTokens = s.InputTokens,
                OutputTokens = s.OutputTokens,
                DurationMs = s.DurationMs,
            }).ToList(),
        };

        _dbContext.AgentRuns.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
