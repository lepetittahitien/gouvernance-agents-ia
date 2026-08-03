using TraceAgentApi.Overview;
using TraceAgentApi.Trace;

namespace TraceAgentApi.Tests;

public class OverviewMathTests
{
    // --- Rate ---

    [Theory]
    [InlineData(0, 0, 0)]      // aucun run → 0 %, pas une division par zéro
    [InlineData(0, 10, 0)]
    [InlineData(10, 10, 100)]
    [InlineData(1, 4, 25)]
    [InlineData(1, 3, 33.3)]   // arrondi à une décimale
    public void Rate_calcule_un_pourcentage_protege_contre_zero(int part, int total, double attendu)
    {
        Assert.Equal(attendu, OverviewMath.Rate(part, total));
    }

    // --- AggregateProjectedCost ---

    private static PricingOptions Pricing() => new()
    {
        ReferenceModel = "claude-sonnet",
        Models = new()
        {
            ["llama3.2"] = new ModelPricing { InputPerMillionEur = 0, OutputPerMillionEur = 0 },
            ["claude-sonnet"] = new ModelPricing { InputPerMillionEur = 2.8m, OutputPerMillionEur = 14m },
        },
    };

    [Fact]
    public void AggregateProjectedCost_somme_les_projections_de_tous_les_runs()
    {
        // Trois runs locaux, chacun projeté sur claude-sonnet.
        // Total : 3M in × 2,8 + 3M out × 14 = 8,4 + 42 = 50,4 €
        var runs = new[]
        {
            ("llama3.2", 1_000_000, 1_000_000),
            ("llama3.2", 1_000_000, 1_000_000),
            ("llama3.2", 1_000_000, 1_000_000),
        };

        Assert.Equal(50.4m, OverviewMath.AggregateProjectedCost(Pricing(), runs));
    }

    [Fact]
    public void AggregateProjectedCost_est_zero_sans_run()
    {
        Assert.Equal(0m, OverviewMath.AggregateProjectedCost(Pricing(), []));
    }

    [Fact]
    public void AggregateProjectedCost_grouper_par_modele_donne_le_meme_total()
    {
        // Le groupement par modèle (optimisation) ne doit pas changer le résultat :
        // 500+500 tokens groupés = 1000 tokens, projetés au même tarif.
        var séparés = new[] { ("llama3.2", 500, 250), ("llama3.2", 500, 250) };
        var fusionnés = new[] { ("llama3.2", 1000, 500) };

        Assert.Equal(
            OverviewMath.AggregateProjectedCost(Pricing(), fusionnés),
            OverviewMath.AggregateProjectedCost(Pricing(), séparés));
    }
}
