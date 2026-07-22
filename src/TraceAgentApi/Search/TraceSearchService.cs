using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TraceAgentApi.Trace;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Search;

public record SearchHit(
    Guid RunId,
    DateTimeOffset StartedAt,
    string Prompt,
    string FinalAnswer,
    bool HasPiiViolation,
    /// Distance cosinus : 0 = identique, 2 = opposé. Plus c'est petit, plus c'est proche.
    double Distance,
    double SimilarityPercent);

public record IndexingReport(int RunsIndexed, int RunsSkipped, string EmbeddingModel);

/// Recherche sémantique sur les traces d'agents — « montre-moi les runs similaires à cet incident ».
public class TraceSearchService(
    TraceDbContext dbContext,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IConfiguration configuration,
    ILogger<TraceSearchService> logger)
{
    private string EmbeddingModel => configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

    /// Indexe les runs qui ne le sont pas encore. Réindexe aussi ceux dont le modèle
    /// d'embedding a changé : des vecteurs produits par deux modèles ne sont pas comparables.
    public async Task<IndexingReport> IndexPendingAsync(CancellationToken cancellationToken = default)
    {
        var model = EmbeddingModel;

        var runsToIndex = await dbContext.AgentRuns
            .Include(r => r.Steps)
            .Where(r => !dbContext.RunEmbeddings.Any(e => e.RunId == r.Id && e.EmbeddingModel == model))
            .ToListAsync(cancellationToken);

        if (runsToIndex.Count == 0)
        {
            return new IndexingReport(0, 0, model);
        }

        int indexed = 0, skipped = 0;

        foreach (var run in runsToIndex)
        {
            var text = BuildIndexedText(run);

            if (string.IsNullOrWhiteSpace(text))
            {
                skipped++;
                continue;
            }

            var embedding = await embeddingGenerator.GenerateVectorAsync(text, cancellationToken: cancellationToken);

            var existing = await dbContext.RunEmbeddings
                .FirstOrDefaultAsync(e => e.RunId == run.Id, cancellationToken);

            if (existing is null)
            {
                dbContext.RunEmbeddings.Add(new RunEmbeddingEntity
                {
                    RunId = run.Id,
                    IndexedText = text,
                    Embedding = new Vector(embedding),
                    EmbeddingModel = model,
                    IndexedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.IndexedText = text;
                existing.Embedding = new Vector(embedding);
                existing.EmbeddingModel = model;
                existing.IndexedAt = DateTimeOffset.UtcNow;
            }

            indexed++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Indexation terminée : {Indexed} run(s) indexé(s), {Skipped} ignoré(s).", indexed, skipped);

        return new IndexingReport(indexed, skipped, model);
    }

    /// Recherche en langage naturel : « des runs où l'agent a fuité un email ».
    public async Task<List<SearchHit>> SearchAsync(
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var queryVector = new Vector(
            await embeddingGenerator.GenerateVectorAsync(query, cancellationToken: cancellationToken));

        return await dbContext.RunEmbeddings
            .Where(e => e.Embedding != null && e.EmbeddingModel == EmbeddingModel)
            .Join(dbContext.AgentRuns, e => e.RunId, r => r.Id, (e, r) => new { e, r })
            .OrderBy(x => x.e.Embedding!.CosineDistance(queryVector))
            .Take(limit)
            .Select(x => new SearchHit(
                x.r.Id,
                x.r.StartedAt,
                x.r.Prompt,
                x.r.FinalAnswer,
                x.r.HasPiiViolation,
                x.e.Embedding!.CosineDistance(queryVector),
                // Conversion en score lisible : la distance cosinus va de 0 (identique) à 2.
                Math.Round((1 - x.e.Embedding!.CosineDistance(queryVector) / 2) * 100, 1)))
            .ToListAsync(cancellationToken);
    }

    /// « Montre-moi les runs similaires à celui-ci » — part du run existant, pas d'un texte libre.
    public async Task<List<SearchHit>> FindSimilarAsync(
        Guid runId,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var source = await dbContext.RunEmbeddings
            .FirstOrDefaultAsync(e => e.RunId == runId && e.EmbeddingModel == EmbeddingModel, cancellationToken);

        if (source?.Embedding is null)
        {
            return [];
        }

        var sourceVector = source.Embedding;

        return await dbContext.RunEmbeddings
            .Where(e => e.Embedding != null && e.EmbeddingModel == EmbeddingModel && e.RunId != runId)
            .Join(dbContext.AgentRuns, e => e.RunId, r => r.Id, (e, r) => new { e, r })
            .OrderBy(x => x.e.Embedding!.CosineDistance(sourceVector))
            .Take(limit)
            .Select(x => new SearchHit(
                x.r.Id,
                x.r.StartedAt,
                x.r.Prompt,
                x.r.FinalAnswer,
                x.r.HasPiiViolation,
                x.e.Embedding!.CosineDistance(sourceVector),
                Math.Round((1 - x.e.Embedding!.CosineDistance(sourceVector) / 2) * 100, 1)))
            .ToListAsync(cancellationToken);
    }

    /// Le « document » indexé pour un run : ce qui porte le sens pour une recherche d'incident.
    /// On inclut délibérément les appels d'outils et les violations — chercher « fuite de données »
    /// ou « appel refusé » doit ramener les runs concernés, pas seulement ceux dont le prompt
    /// contient ces mots.
    private static string BuildIndexedText(AgentRunEntity run)
    {
        var parts = new List<string>
        {
            $"Prompt : {run.Prompt}",
            $"Réponse : {run.FinalAnswer}",
        };

        foreach (var step in run.Steps.OrderBy(s => s.Index))
        {
            parts.Add(step.Kind switch
            {
                TraceStepKind.ToolCall => $"Outil appelé : {step.Label}. {step.Detail}",
                TraceStepKind.PolicyDenial => $"Appel d'outil refusé par la politique d'accès : {step.Label}. {step.Detail}",
                _ => null,
            } ?? "");
        }

        if (run.HasPiiViolation)
        {
            parts.Add($"Violation de confidentialité : fuite de données personnelles détectée. {run.PiiSummaryJson}");
        }

        return string.Join('\n', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
