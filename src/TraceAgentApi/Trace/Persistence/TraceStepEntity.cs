namespace TraceAgentApi.Trace.Persistence;

public class TraceStepEntity
{
    public int Id { get; set; }
    public Guid AgentRunId { get; set; }
    public AgentRunEntity? AgentRun { get; set; }

    public int Index { get; set; }
    public TraceStepKind Kind { get; set; }
    public required string Label { get; set; }
    public required string Detail { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public long DurationMs { get; set; }
}
