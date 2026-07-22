namespace TraceAgentApi.Evals;

public enum EvalAssertionKind
{
    /// La réponse finale contient ce texte (insensible à la casse).
    AnswerContains,

    /// Cet outil a été appelé pendant le run.
    ToolCalled,

    /// Cet outil a été appelé avec cet argument (ex: "city=Paris").
    ToolCalledWithArg,

    /// Aucune fuite PII détectée (réutilise le garde-fou T2).
    NoPiiViolation,

    /// Le run a duré moins que ce seuil (ms) — détecte les régressions de latence.
    MaxDurationMs,

    /// Le run a consommé moins que ce seuil de tokens — détecte les régressions de coût.
    MaxTotalTokens,
}

public record EvalAssertion(EvalAssertionKind Kind, string? ExpectedValue = null, long? Threshold = null);

public record EvalCase(string Id, string Prompt, List<EvalAssertion> Assertions);

public record EvalAssertionResult(EvalAssertion Assertion, bool Passed, string Detail);

public record EvalCaseResult(
    string CaseId,
    string Prompt,
    Guid RunId,
    bool Passed,
    List<EvalAssertionResult> AssertionResults,
    long DurationMs,
    int TotalTokens);

public record EvalRunReport(
    Guid EvalRunId,
    DateTimeOffset StartedAt,
    string ModelName,
    int CasesTotal,
    int CasesPassed,
    double ScorePercent,
    long TotalDurationMs,
    int TotalTokens,
    List<EvalCaseResult> CaseResults,
    RegressionComparison? Regression);

/// Comparaison avec le run d'évals précédent — le cœur de la détection de régression.
public record RegressionComparison(
    Guid PreviousEvalRunId,
    DateTimeOffset PreviousStartedAt,
    double PreviousScorePercent,
    double ScoreDelta,
    bool IsRegression,
    List<string> NewlyFailingCaseIds,
    List<string> NewlyPassingCaseIds);
