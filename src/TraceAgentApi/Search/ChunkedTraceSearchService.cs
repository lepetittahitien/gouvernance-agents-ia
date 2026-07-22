using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TraceAgentApi.Trace;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Search;

public record ChunkedSearchHit(
    Guid RunId,
    DateTimeOffset StartedAt,
    string Prompt,
    bool HasPiiViolation,
    /// Le chunk qui a effectivement déclenché la correspondance — dit *pourquoi* ce run remonte.
    RunChunkKind MatchedChunkKind,
    string MatchedText,
    double Distance,
    double SimilarityPercent);

/// Recherche sémantique par chunks. Chaque aspect d'un run (prompt, réponse, appel d'outil,
/// refus, violation) est indexé séparément, puis les résultats sont regroupés par run en
/// gardant le meilleur chunk. Corrige la dilution des signaux rares de l'indexation naïve.
public class ChunkedTraceSearchService(
    TraceDbContext dbContext,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IConfiguration configuration,
    ILogger<ChunkedTraceSearchService> logger)
{
    private string EmbeddingModel => configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

    public async Task<IndexingReport> IndexPendingAsync(CancellationToken cancellationToken = default)
    {
        var model = EmbeddingModel;

        var runsToIndex = await dbContext.AgentRuns
            .Include(r => r.Steps)
            .Where(r => !dbContext.RunChunkEmbeddings.Any(c => c.RunId == r.Id && c.EmbeddingModel == model))
            .ToListAsync(cancellationToken);

        if (runsToIndex.Count == 0)
        {
            return new IndexingReport(0, 0, model);
        }

        int indexed = 0, skipped = 0;

        foreach (var run in runsToIndex)
        {
            var chunks = BuildChunks(run).ToList();

            if (chunks.Count == 0)
            {
                skipped++;
                continue;
            }

            foreach (var (kind, text) in chunks)
            {
                var vector = await embeddingGenerator.GenerateVectorAsync(text, cancellationToken: cancellationToken);

                dbContext.RunChunkEmbeddings.Add(new RunChunkEmbeddingEntity
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    ChunkKind = kind,
                    Text = text,
                    Embedding = new Vector(vector),
                    EmbeddingModel = model,
                    IndexedAt = DateTimeOffset.UtcNow,
                });
            }

            indexed++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Indexation par chunks : {Indexed} run(s), {Skipped} ignoré(s).", indexed, skipped);

        return new IndexingReport(indexed, skipped, model);
    }

    public async Task<List<ChunkedSearchHit>> SearchAsync(
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var queryVector = new Vector(
            await embeddingGenerator.GenerateVectorAsync(query, cancellationToken: cancellationToken));

        // On récupère plus de chunks que de résultats voulus : plusieurs chunks d'un même run
        // peuvent remonter, et on ne garde ensuite qu'un résultat par run.
        var candidates = await dbContext.RunChunkEmbeddings
            .Where(c => c.Embedding != null && c.EmbeddingModel == EmbeddingModel)
            .Join(dbContext.AgentRuns, c => c.RunId, r => r.Id, (c, r) => new
            {
                c.RunId,
                r.StartedAt,
                r.Prompt,
                r.HasPiiViolation,
                c.ChunkKind,
                c.Text,
                Distance = c.Embedding!.CosineDistance(queryVector),
            })
            .OrderBy(x => x.Distance)
            .Take(limit * 5)
            .ToListAsync(cancellationToken);

        return candidates
            .GroupBy(x => x.RunId)
            .Select(g => g.OrderBy(x => x.Distance).First())
            .OrderBy(x => x.Distance)
            .Take(limit)
            .Select(x => new ChunkedSearchHit(
                x.RunId,
                x.StartedAt,
                x.Prompt,
                x.HasPiiViolation,
                x.ChunkKind,
                x.Text.Length > 160 ? x.Text[..160] + "…" : x.Text,
                Math.Round(x.Distance, 4),
                Math.Round((1 - x.Distance / 2) * 100, 1)))
            .ToList();
    }

    /// Découpe un run en chunks courts et focalisés. Un chunk = un fait, pas un run entier.
    private static IEnumerable<(RunChunkKind Kind, string Text)> BuildChunks(AgentRunEntity run)
    {
        if (!string.IsNullOrWhiteSpace(run.Prompt))
        {
            yield return (RunChunkKind.Prompt, $"Demande de l'utilisateur : {run.Prompt}");
        }

        if (!string.IsNullOrWhiteSpace(run.FinalAnswer))
        {
            yield return (RunChunkKind.Answer, $"Réponse de l'agent : {run.FinalAnswer}");
        }

        foreach (var step in run.Steps.OrderBy(s => s.Index))
        {
            switch (step.Kind)
            {
                case TraceStepKind.ToolCall:
                    yield return (RunChunkKind.ToolCall,
                        $"L'agent a appelé un outil externe. {step.Label}. {step.Detail}");
                    break;

                case TraceStepKind.PolicyDenial:
                    // Formulé avec les mots qu'un opérateur emploierait spontanément
                    // (« bloqué », « interdit », « empêché »), pas seulement le jargon interne.
                    yield return (RunChunkKind.PolicyDenial,
                        "Incident de sécurité : un appel d'outil a été bloqué, refusé et empêché " +
                        $"par la politique de contrôle d'accès. L'agent n'avait pas le droit d'exécuter cette action. " +
                        $"{step.Label}. {step.Detail}");
                    break;
            }
        }

        if (run.HasPiiViolation)
        {
            yield return (RunChunkKind.GuardrailViolation,
                "Incident de confidentialité : fuite de données personnelles détectée dans ce run. " +
                $"Des informations privées ont été divulguées. Types détectés : {run.PiiSummaryJson}");
        }
    }
}
