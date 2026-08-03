using TraceAgentApi.Trace;

namespace TraceAgentApi.Overview;

/// Calculs purs de l'aperçu — testables sans base ni configuration.
public static class OverviewMath
{
    /// Pourcentage arrondi, protégé contre la division par zéro (0 run = 0 %, pas une erreur).
    public static double Rate(int part, int total) =>
        total == 0 ? 0 : Math.Round(part * 100.0 / total, 1);

    /// Coût projeté cumulé : somme des projections run par run. On groupe par modèle pour
    /// n'appeler le barème qu'une fois par modèle, mais la somme reste exacte car les tokens
    /// sont additionnés dans chaque groupe.
    public static decimal AggregateProjectedCost(
        PricingOptions pricing,
        IEnumerable<(string Model, int InputTokens, int OutputTokens)> runs)
    {
        return runs
            .GroupBy(r => r.Model)
            .Sum(g => CostEstimator.Estimate(
                pricing,
                g.Key,
                g.Sum(r => r.InputTokens),
                g.Sum(r => r.OutputTokens)).ProjectedEur ?? 0m);
    }
}
