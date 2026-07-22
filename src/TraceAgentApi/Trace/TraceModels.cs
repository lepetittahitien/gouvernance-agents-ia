namespace TraceAgentApi.Trace;

/// ⚠️ Persistée en entier : toute nouvelle valeur doit être ajoutée en fin d'énumération,
/// sinon les étapes déjà en base changeraient de type.
public enum TraceStepKind
{
    ModelCall,
    ToolCall,
    PolicyDenial,
}

public record TraceStep(
    int Index,
    TraceStepKind Kind,
    string Label,
    string Detail,
    int? InputTokens,
    int? OutputTokens,
    long DurationMs);

public record AgentRunTrace(
    Guid RunId,
    string Prompt,
    string ModelName,
    DateTimeOffset StartedAt,
    List<TraceStep> Steps,
    string FinalAnswer,
    int TotalInputTokens,
    int TotalOutputTokens,
    long TotalDurationMs,
    decimal EstimatedCostEur,
    bool HasPiiViolation,
    IReadOnlyDictionary<PiiType, int> PiiFindingsByType,
    BudgetStatus? Budget = null);
