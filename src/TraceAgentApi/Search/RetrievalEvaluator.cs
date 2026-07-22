using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TraceAgentApi.Trace;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Search;

/// Définit ce qui est *pertinent* pour une requête, par prédicat plutôt que par identifiants figés :
/// le jeu d'évals reste valide quand de nouveaux runs arrivent.
public record RelevancePredicate(
    bool? HasPiiViolation = null,
    bool? HasPolicyDenial = null,
    string? PromptContains = null);

public record RetrievalEvalCase(string Query, RelevancePredicate RelevantWhen);

public record RetrievalCaseResult(
    string Query,
    int RelevantTotal,
    int RelevantRetrieved,
    double RecallAtK,
    double PrecisionAtK,
    /// Rang du premier résultat pertinent (1 = premier). 0 si aucun dans le top-K.
    int FirstRelevantRank,
    double ReciprocalRank,
    List<string> TopResults);

public record RetrievalEvalReport(
    DateTimeOffset RunAt,
    int K,
    string EmbeddingModel,
    /// « naive » (un vecteur par run) ou « chunked » (un vecteur par aspect).
    string Strategy,
    double MeanRecallAtK,
    double MeanPrecisionAtK,
    /// MRR : mesure si le *premier* bon résultat arrive haut. C'est ce qui compte quand
    /// un opérateur regarde surtout les premiers résultats.
    double MeanReciprocalRank,
    List<RetrievalCaseResult> Cases);

public class RetrievalEvaluator(
    TraceDbContext dbContext,
    TraceSearchService searchService,
    ChunkedTraceSearchService chunkedSearchService,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions FileOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<RetrievalEvalReport> RunAsync(
        int k = 5,
        string strategy = "chunked",
        CancellationToken cancellationToken = default)
    {
        var path = configuration["Search:RetrievalEvalsPath"] ?? "Search/retrieval-evals.json";

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Jeu d'évals de retrieval introuvable : {path}");
        }

        var cases = JsonSerializer.Deserialize<List<RetrievalEvalCase>>(File.ReadAllText(path), FileOptions)
            ?? throw new InvalidOperationException("Jeu d'évals de retrieval vide ou invalide.");

        var runs = await dbContext.AgentRuns
            .Include(r => r.Steps)
            .ToListAsync(cancellationToken);

        List<RetrievalCaseResult> results = [];

        foreach (var evalCase in cases)
        {
            var relevantIds = runs
                .Where(r => IsRelevant(r, evalCase.RelevantWhen))
                .Select(r => r.Id)
                .ToHashSet();

            // Résultats ramenés à une forme commune pour comparer les deux stratégies
            // exactement sur les mêmes métriques.
            List<(Guid RunId, double Similarity, string Prompt)> hits =
                string.Equals(strategy, "naive", StringComparison.OrdinalIgnoreCase)
                    ? (await searchService.SearchAsync(evalCase.Query, k, cancellationToken))
                        .Select(h => (h.RunId, h.SimilarityPercent, h.Prompt)).ToList()
                    : (await chunkedSearchService.SearchAsync(evalCase.Query, k, cancellationToken))
                        .Select(h => (h.RunId, h.SimilarityPercent, h.Prompt)).ToList();

            var retrievedRelevant = hits.Count(h => relevantIds.Contains(h.RunId));
            var firstRank = hits.FindIndex(h => relevantIds.Contains(h.RunId)) + 1;

            results.Add(new RetrievalCaseResult(
                Query: evalCase.Query,
                RelevantTotal: relevantIds.Count,
                RelevantRetrieved: retrievedRelevant,
                // Recall plafonné à K : avec 12 runs pertinents et K=5, on ne peut pas en ramener plus de 5.
                RecallAtK: relevantIds.Count == 0
                    ? 0
                    : Math.Round(retrievedRelevant / (double)Math.Min(relevantIds.Count, k), 3),
                PrecisionAtK: hits.Count == 0 ? 0 : Math.Round(retrievedRelevant / (double)hits.Count, 3),
                FirstRelevantRank: firstRank,
                ReciprocalRank: firstRank == 0 ? 0 : Math.Round(1.0 / firstRank, 3),
                TopResults: hits.Select(h =>
                    $"{(relevantIds.Contains(h.RunId) ? "✓" : "✗")} {h.Similarity}% {Truncate(h.Prompt, 55)}").ToList()));
        }

        return new RetrievalEvalReport(
            RunAt: DateTimeOffset.UtcNow,
            K: k,
            EmbeddingModel: configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text",
            Strategy: strategy,
            MeanRecallAtK: Math.Round(results.Average(r => r.RecallAtK), 3),
            MeanPrecisionAtK: Math.Round(results.Average(r => r.PrecisionAtK), 3),
            MeanReciprocalRank: Math.Round(results.Average(r => r.ReciprocalRank), 3),
            Cases: results);
    }

    private static bool IsRelevant(AgentRunEntity run, RelevancePredicate predicate)
    {
        if (predicate.HasPiiViolation is { } pii && run.HasPiiViolation != pii)
        {
            return false;
        }

        if (predicate.HasPolicyDenial is { } denial)
        {
            var actual = run.Steps.Any(s => s.Kind == TraceStepKind.PolicyDenial);
            if (actual != denial)
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(predicate.PromptContains) &&
            !run.Prompt.Contains(predicate.PromptContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
