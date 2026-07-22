using Microsoft.EntityFrameworkCore;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Trace;

public enum BudgetBreachKind
{
    /// Un run isolé a consommé plus que le seuil autorisé.
    PerRunTokens,

    /// Le cumul sur la période glissante dépasse le budget.
    PeriodTokens,
}

public record BudgetBreach(BudgetBreachKind Kind, long Observed, long Threshold, string Message);

public record BudgetStatus(
    bool HasBreach,
    List<BudgetBreach> Breaches,
    long PeriodTokensUsed,
    long PeriodTokenBudget,
    double PeriodUsagePercent,
    int PeriodHours,
    DateTimeOffset PeriodStart);

public class BudgetOptions
{
    public const string SectionName = "Budget";

    /// Seuil de tokens pour un run isolé. 0 = désactivé.
    public int MaxTokensPerRun { get; set; }

    /// Budget de tokens sur la période glissante. 0 = désactivé.
    public int MaxTokensPerPeriod { get; set; }

    /// Longueur de la fenêtre glissante, en heures.
    public int PeriodHours { get; set; } = 24;
}

/// Surveillance du budget tokens — alerte quand un run isolé ou une période dépasse un seuil.
///
/// Note : le coût € reste à 0 tant qu'on tourne sur Ollama local, donc le budget porte sur les
/// tokens (la métrique réellement mesurable ici). Brancher un provider payant suffira à dériver
/// un budget en euros à partir des mêmes compteurs.
public class BudgetMonitor(TraceDbContext dbContext, IConfiguration configuration, ILogger<BudgetMonitor> logger)
{
    private BudgetOptions LoadOptions()
    {
        var options = new BudgetOptions();
        configuration.GetSection(BudgetOptions.SectionName).Bind(options);
        return options;
    }

    /// Évalue le budget après un run. `justRanTrace` n'est pas encore forcément visible en base
    /// selon l'ordre d'appel : on l'ajoute explicitement au cumul de la période.
    public async Task<BudgetStatus> EvaluateAsync(
        AgentRunTrace? justRanTrace = null,
        CancellationToken cancellationToken = default)
    {
        var options = LoadOptions();
        var periodStart = DateTimeOffset.UtcNow.AddHours(-options.PeriodHours);

        var persistedTokens = await dbContext.AgentRuns
            .Where(r => r.StartedAt >= periodStart)
            .SumAsync(r => (long)(r.TotalInputTokens + r.TotalOutputTokens), cancellationToken);

        var alreadyPersisted = justRanTrace is not null
            && await dbContext.AgentRuns.AnyAsync(r => r.Id == justRanTrace.RunId, cancellationToken);

        var runTokens = justRanTrace is null
            ? 0
            : justRanTrace.TotalInputTokens + justRanTrace.TotalOutputTokens;

        var periodTokens = persistedTokens + (alreadyPersisted ? 0 : runTokens);

        List<BudgetBreach> breaches = [];

        if (options.MaxTokensPerRun > 0 && runTokens > options.MaxTokensPerRun)
        {
            breaches.Add(new BudgetBreach(
                BudgetBreachKind.PerRunTokens,
                runTokens,
                options.MaxTokensPerRun,
                $"Ce run a consommé {runTokens} tokens (seuil par run : {options.MaxTokensPerRun})."));
        }

        if (options.MaxTokensPerPeriod > 0 && periodTokens > options.MaxTokensPerPeriod)
        {
            breaches.Add(new BudgetBreach(
                BudgetBreachKind.PeriodTokens,
                periodTokens,
                options.MaxTokensPerPeriod,
                $"{periodTokens} tokens consommés sur {options.PeriodHours} h (budget : {options.MaxTokensPerPeriod})."));
        }

        foreach (var breach in breaches)
        {
            logger.LogWarning("BUDGET DÉPASSÉ ({Kind}) : {Message}", breach.Kind, breach.Message);
        }

        var usagePercent = options.MaxTokensPerPeriod > 0
            ? Math.Round(periodTokens * 100.0 / options.MaxTokensPerPeriod, 1)
            : 0;

        return new BudgetStatus(
            HasBreach: breaches.Count > 0,
            Breaches: breaches,
            PeriodTokensUsed: periodTokens,
            PeriodTokenBudget: options.MaxTokensPerPeriod,
            PeriodUsagePercent: usagePercent,
            PeriodHours: options.PeriodHours,
            PeriodStart: periodStart);
    }
}
