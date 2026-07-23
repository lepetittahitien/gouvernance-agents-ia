using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Trace;

public record AgentRunSummary(
    Guid RunId,
    string Prompt,
    string ModelName,
    DateTimeOffset StartedAt,
    int TotalInputTokens,
    int TotalOutputTokens,
    long TotalDurationMs,
    decimal EstimatedCostEur,
    bool HasPiiViolation,
    InjectionRiskLevel InjectionRisk);

public class TraceQueryService(TraceDbContext dbContext)
{
    public async Task<List<AgentRunSummary>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.AgentRuns
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new AgentRunSummary(
                r.Id,
                r.Prompt,
                r.ModelName,
                r.StartedAt,
                r.TotalInputTokens,
                r.TotalOutputTokens,
                r.TotalDurationMs,
                r.EstimatedCostEur,
                r.HasPiiViolation,
                r.InjectionRisk))
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentRunTrace?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AgentRuns
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var piiFindingsByType = entity.PiiSummaryJson is null
            ? new Dictionary<PiiType, int>()
            : JsonSerializer.Deserialize<Dictionary<string, int>>(entity.PiiSummaryJson)!
                .ToDictionary(kv => Enum.Parse<PiiType>(kv.Key), kv => kv.Value);

        var injectionSignalsByKind = entity.InjectionSummaryJson is null
            ? new Dictionary<InjectionSignalKind, int>()
            : JsonSerializer.Deserialize<Dictionary<string, int>>(entity.InjectionSummaryJson)!
                .ToDictionary(kv => Enum.Parse<InjectionSignalKind>(kv.Key), kv => kv.Value);

        return new AgentRunTrace(
            entity.Id,
            entity.Prompt,
            entity.ModelName,
            entity.StartedAt,
            entity.Steps
                .OrderBy(s => s.Index)
                .Select(s => new TraceStep(s.Index, s.Kind, s.Label, s.Detail, s.InputTokens, s.OutputTokens, s.DurationMs))
                .ToList(),
            entity.FinalAnswer,
            entity.TotalInputTokens,
            entity.TotalOutputTokens,
            entity.TotalDurationMs,
            entity.EstimatedCostEur,
            entity.HasPiiViolation,
            piiFindingsByType,
            Budget: null,
            InjectionRisk: entity.InjectionRisk,
            InjectionSignalsByKind: injectionSignalsByKind);
    }
}
