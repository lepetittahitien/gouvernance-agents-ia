namespace TraceAgentApi.Trace.Persistence;

public class AgentRunEntity
{
    public Guid Id { get; set; }
    public required string Prompt { get; set; }
    public required string ModelName { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public required string FinalAnswer { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public long TotalDurationMs { get; set; }
    public decimal EstimatedCostEur { get; set; }

    public bool HasPiiViolation { get; set; }

    // Type de PII + nombre d'occurrences uniquement (ex: {"Email":1,"PhoneFr":2})
    // — jamais la valeur détectée, pour ne pas transformer la base d'audit en dépôt de PII.
    public string? PiiSummaryJson { get; set; }

    /// Niveau de risque d'injection agrégé (prompt + résultats d'outils). Persisté en entier.
    public InjectionRiskLevel InjectionRisk { get; set; }

    // Familles de signaux + comptes (ex: {"InstructionOverride":1}) — pas les extraits.
    public string? InjectionSummaryJson { get; set; }

    public List<TraceStepEntity> Steps { get; set; } = [];
}
