using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Evals;

/// Persiste et relit les rapports d'évals. Le détail des cas est stocké en JSON :
/// c'est une donnée de rapport lue en bloc, pas une entité requêtée champ par champ.
public class EvalStore(TraceDbContext dbContext)
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task SaveAsync(EvalRunReport report, CancellationToken cancellationToken = default)
    {
        dbContext.EvalRuns.Add(new EvalRunEntity
        {
            Id = report.EvalRunId,
            StartedAt = report.StartedAt,
            ModelName = report.ModelName,
            CasesTotal = report.CasesTotal,
            CasesPassed = report.CasesPassed,
            ScorePercent = report.ScorePercent,
            TotalDurationMs = report.TotalDurationMs,
            TotalTokens = report.TotalTokens,
            CaseResultsJson = JsonSerializer.Serialize(report.CaseResults, PayloadOptions),
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<EvalRunReport?> GetLatestReportAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvalRuns
            .OrderByDescending(e => e.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : ToReport(entity);
    }

    public async Task<List<EvalRunSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.EvalRuns
            .OrderByDescending(e => e.StartedAt)
            .Select(e => new EvalRunSummary(
                e.Id, e.StartedAt, e.ModelName, e.CasesTotal, e.CasesPassed, e.ScorePercent))
            .ToListAsync(cancellationToken);
    }

    public async Task<EvalRunReport?> GetAsync(Guid evalRunId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvalRuns
            .FirstOrDefaultAsync(e => e.Id == evalRunId, cancellationToken);

        return entity is null ? null : ToReport(entity);
    }

    private static EvalRunReport ToReport(EvalRunEntity entity)
    {
        var caseResults = JsonSerializer.Deserialize<List<EvalCaseResult>>(entity.CaseResultsJson, PayloadOptions) ?? [];

        return new EvalRunReport(
            entity.Id,
            entity.StartedAt,
            entity.ModelName,
            entity.CasesTotal,
            entity.CasesPassed,
            entity.ScorePercent,
            entity.TotalDurationMs,
            entity.TotalTokens,
            caseResults,
            // La comparaison n'est calculée qu'au moment du run, pas rejouée à la relecture.
            Regression: null);
    }
}

public record EvalRunSummary(
    Guid EvalRunId,
    DateTimeOffset StartedAt,
    string ModelName,
    int CasesTotal,
    int CasesPassed,
    double ScorePercent);
