using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Trace;

public record ExternalScanSummary(
    Guid Id,
    DateTimeOffset Timestamp,
    ExternalScanKind Kind,
    string? Source,
    bool HasViolation,
    JsonElement Summary);

/// Persiste les verdicts des endpoints découplés pour qu'ils remontent dans le dashboard.
public class ExternalScanStore(TraceDbContext dbContext)
{
    public async Task RecordAsync(
        ExternalScanKind kind,
        string? source,
        bool hasViolation,
        object summary,
        CancellationToken cancellationToken = default)
    {
        dbContext.ExternalScans.Add(new ExternalScanEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Kind = kind,
            Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
            HasViolation = hasViolation,
            SummaryJson = JsonSerializer.Serialize(summary),
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ExternalScanSummary>> ListAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.ExternalScans
            .OrderByDescending(s => s.Timestamp)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return entities
            .Select(s => new ExternalScanSummary(
                s.Id, s.Timestamp, s.Kind, s.Source, s.HasViolation,
                JsonDocument.Parse(s.SummaryJson).RootElement.Clone()))
            .ToList();
    }
}
