using Microsoft.EntityFrameworkCore;
using TraceAgentApi.Audit;
using TraceAgentApi.Evals;
using TraceAgentApi.Trace;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Overview;

public record OverviewStats(
    DateTimeOffset GeneratedAt,
    int PeriodHours,

    // Activité
    int RunsInPeriod,
    long TotalTokens,
    decimal ProjectedCostEur,
    string? ProjectedCostModel,

    // Gouvernance — les signaux qui comptent pour un décideur
    int PiiViolations,
    double PiiViolationRate,
    int InjectionSuspicions,
    double InjectionSuspicionRate,
    int ToolDenials,

    // Santé de la plateforme
    bool AuditChainIntact,
    long AuditEntriesChecked,
    BudgetStatus Budget,
    int ExternalScansInPeriod,
    int ExternalScanViolations,
    double? LatestEvalScore);

/// Vue d'ensemble « santé des agents » — agrège les briques en un seul écran.
public class OverviewService(
    TraceDbContext dbContext,
    BudgetMonitor budgetMonitor,
    AuditLogger auditLogger,
    EvalStore evalStore,
    ExternalScanStore externalScanStore,
    IConfiguration configuration)
{
    public async Task<OverviewStats> BuildAsync(int periodHours = 24, CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-periodHours);

        var runs = await dbContext.AgentRuns
            .Where(r => r.StartedAt >= since)
            .Select(r => new
            {
                r.ModelName,
                r.TotalInputTokens,
                r.TotalOutputTokens,
                r.HasPiiViolation,
                r.InjectionRisk,
            })
            .ToListAsync(cancellationToken);

        var runsTotal = runs.Count;
        var piiViolations = runs.Count(r => r.HasPiiViolation);
        var injectionSuspicions = runs.Count(r => r.InjectionRisk != InjectionRiskLevel.None);
        var totalTokens = runs.Sum(r => (long)(r.TotalInputTokens + r.TotalOutputTokens));

        var toolDenials = await dbContext.TraceSteps
            .CountAsync(s => s.Kind == TraceStepKind.PolicyDenial && s.AgentRun!.StartedAt >= since, cancellationToken);

        var pricing = new PricingOptions();
        configuration.GetSection(PricingOptions.SectionName).Bind(pricing);
        var projectedCost = OverviewMath.AggregateProjectedCost(
            pricing, runs.Select(r => (r.ModelName, r.TotalInputTokens, r.TotalOutputTokens)));

        var integrity = await auditLogger.VerifyChainAsync(cancellationToken);
        var budget = await budgetMonitor.EvaluateAsync(cancellationToken: cancellationToken);

        var externalScans = await externalScanStore.ListAsync(1000, cancellationToken);
        var externalInPeriod = externalScans.Where(s => s.Timestamp >= since).ToList();

        var latestEval = await evalStore.GetLatestReportAsync(cancellationToken);

        return new OverviewStats(
            GeneratedAt: DateTimeOffset.UtcNow,
            PeriodHours: periodHours,
            RunsInPeriod: runsTotal,
            TotalTokens: totalTokens,
            ProjectedCostEur: projectedCost,
            ProjectedCostModel: pricing.ReferenceModel,
            PiiViolations: piiViolations,
            PiiViolationRate: OverviewMath.Rate(piiViolations, runsTotal),
            InjectionSuspicions: injectionSuspicions,
            InjectionSuspicionRate: OverviewMath.Rate(injectionSuspicions, runsTotal),
            ToolDenials: toolDenials,
            AuditChainIntact: integrity.IsIntact,
            AuditEntriesChecked: integrity.EntriesChecked,
            Budget: budget,
            ExternalScansInPeriod: externalInPeriod.Count,
            ExternalScanViolations: externalInPeriod.Count(s => s.HasViolation),
            LatestEvalScore: latestEval?.ScorePercent);
    }
}
