namespace TraceAgentApi.Trace.Persistence;

public class EvalRunEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public required string ModelName { get; set; }
    public int CasesTotal { get; set; }
    public int CasesPassed { get; set; }
    public double ScorePercent { get; set; }
    public long TotalDurationMs { get; set; }
    public int TotalTokens { get; set; }

    /// Détail des cas et assertions, sérialisé — lu en bloc, jamais requêté champ par champ.
    public required string CaseResultsJson { get; set; }
}
