using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TraceAgentApi.Trace;

namespace TraceAgentApi.Evals;

public class EvalRunner(
    AgentRunner agentRunner,
    EvalStore evalStore,
    ILogger<EvalRunner> logger,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions CaseFileOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<EvalRunReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var cases = await LoadCasesAsync(cancellationToken);
        var evalRunId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        List<EvalCaseResult> caseResults = [];

        foreach (var evalCase in cases)
        {
            logger.LogInformation("Éval {CaseId} : lancement…", evalCase.Id);

            var trace = await agentRunner.RunAsync(evalCase.Prompt, cancellationToken);
            var assertionResults = evalCase.Assertions
                .Select(assertion => Evaluate(assertion, trace))
                .ToList();

            caseResults.Add(new EvalCaseResult(
                CaseId: evalCase.Id,
                Prompt: evalCase.Prompt,
                RunId: trace.RunId,
                Passed: assertionResults.All(r => r.Passed),
                AssertionResults: assertionResults,
                DurationMs: trace.TotalDurationMs,
                TotalTokens: trace.TotalInputTokens + trace.TotalOutputTokens));
        }

        stopwatch.Stop();

        var casesPassed = caseResults.Count(r => r.Passed);
        var scorePercent = caseResults.Count == 0 ? 0 : Math.Round(casesPassed * 100.0 / caseResults.Count, 1);

        var previous = await evalStore.GetLatestReportAsync(cancellationToken);
        var regression = previous is null ? null : Compare(previous, scorePercent, caseResults);

        var report = new EvalRunReport(
            EvalRunId: evalRunId,
            StartedAt: startedAt,
            ModelName: configuration["Ollama:Model"] ?? "inconnu",
            CasesTotal: caseResults.Count,
            CasesPassed: casesPassed,
            ScorePercent: scorePercent,
            TotalDurationMs: stopwatch.ElapsedMilliseconds,
            TotalTokens: caseResults.Sum(r => r.TotalTokens),
            CaseResults: caseResults,
            Regression: regression);

        if (regression?.IsRegression == true)
        {
            logger.LogWarning(
                "RÉGRESSION détectée : score {Current}% contre {Previous}% précédemment ({Delta:+0.0;-0.0}). Cas nouvellement en échec : {Cases}",
                scorePercent, regression.PreviousScorePercent, regression.ScoreDelta,
                string.Join(", ", regression.NewlyFailingCaseIds));
        }

        await evalStore.SaveAsync(report, cancellationToken);
        return report;
    }

    private static RegressionComparison Compare(
        EvalRunReport previous,
        double currentScore,
        List<EvalCaseResult> currentResults)
    {
        var previousByCase = previous.CaseResults.ToDictionary(r => r.CaseId, r => r.Passed);

        var newlyFailing = currentResults
            .Where(r => !r.Passed && previousByCase.TryGetValue(r.CaseId, out var wasPassing) && wasPassing)
            .Select(r => r.CaseId)
            .ToList();

        var newlyPassing = currentResults
            .Where(r => r.Passed && previousByCase.TryGetValue(r.CaseId, out var wasPassing) && !wasPassing)
            .Select(r => r.CaseId)
            .ToList();

        return new RegressionComparison(
            PreviousEvalRunId: previous.EvalRunId,
            PreviousStartedAt: previous.StartedAt,
            PreviousScorePercent: previous.ScorePercent,
            ScoreDelta: Math.Round(currentScore - previous.ScorePercent, 1),
            // Une régression, c'est un cas qui passait et qui casse — pas juste un score en baisse
            // (le score peut bouger si on ajoute des cas au jeu d'évals).
            IsRegression: newlyFailing.Count > 0,
            NewlyFailingCaseIds: newlyFailing,
            NewlyPassingCaseIds: newlyPassing);
    }

    private static EvalAssertionResult Evaluate(EvalAssertion assertion, AgentRunTrace trace)
    {
        switch (assertion.Kind)
        {
            case EvalAssertionKind.AnswerContains:
            {
                var expected = assertion.ExpectedValue ?? "";
                var passed = trace.FinalAnswer.Contains(expected, StringComparison.OrdinalIgnoreCase);
                return new EvalAssertionResult(assertion, passed,
                    passed ? $"réponse contient « {expected} »" : $"réponse ne contient pas « {expected} »");
            }

            case EvalAssertionKind.ToolCalled:
            {
                var expected = assertion.ExpectedValue ?? "";
                var passed = trace.Steps.Any(s =>
                    s.Kind == TraceStepKind.ToolCall &&
                    s.Label.Contains(expected, StringComparison.OrdinalIgnoreCase));
                return new EvalAssertionResult(assertion, passed,
                    passed ? $"outil « {expected} » appelé" : $"outil « {expected} » jamais appelé");
            }

            case EvalAssertionKind.ToolCalledWithArg:
            {
                var expected = assertion.ExpectedValue ?? "";
                var passed = trace.Steps.Any(s =>
                    s.Kind == TraceStepKind.ToolCall &&
                    s.Label.Contains(expected, StringComparison.OrdinalIgnoreCase));
                return new EvalAssertionResult(assertion, passed,
                    passed ? $"appelé avec « {expected} »" : $"jamais appelé avec « {expected} »");
            }

            case EvalAssertionKind.NoPiiViolation:
            {
                var passed = !trace.HasPiiViolation;
                return new EvalAssertionResult(assertion, passed,
                    passed ? "aucune PII détectée" : "PII détectée dans le run");
            }

            case EvalAssertionKind.MaxDurationMs:
            {
                var threshold = assertion.Threshold ?? long.MaxValue;
                var passed = trace.TotalDurationMs <= threshold;
                return new EvalAssertionResult(assertion, passed,
                    $"{trace.TotalDurationMs} ms (seuil {threshold} ms)");
            }

            case EvalAssertionKind.MaxTotalTokens:
            {
                var threshold = assertion.Threshold ?? long.MaxValue;
                var total = trace.TotalInputTokens + trace.TotalOutputTokens;
                var passed = total <= threshold;
                return new EvalAssertionResult(assertion, passed,
                    $"{total} tokens (seuil {threshold})");
            }

            default:
                return new EvalAssertionResult(assertion, false, $"type d'assertion inconnu : {assertion.Kind}");
        }
    }

    private async Task<List<EvalCase>> LoadCasesAsync(CancellationToken cancellationToken)
    {
        var path = configuration["Evals:CasesPath"] ?? "Evals/default-evals.json";

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Jeu d'évals introuvable : {path}");
        }

        await using var stream = File.OpenRead(path);
        var cases = await JsonSerializer.DeserializeAsync<List<EvalCase>>(stream, CaseFileOptions, cancellationToken);

        return cases ?? throw new InvalidOperationException($"Jeu d'évals vide ou invalide : {path}");
    }
}
