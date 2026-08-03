using TraceAgentApi.Trace;

namespace TraceAgentApi.Tests;

public class CostEstimatorTests
{
    private static PricingOptions Options() => new()
    {
        ReferenceModel = "claude-sonnet",
        Models = new()
        {
            ["llama3.2"] = new ModelPricing { InputPerMillionEur = 0, OutputPerMillionEur = 0 },
            ["claude-sonnet"] = new ModelPricing { InputPerMillionEur = 2.8m, OutputPerMillionEur = 14m },
        },
    };

    [Fact]
    public void Modele_local_a_un_cout_reel_nul()
    {
        var e = CostEstimator.Estimate(Options(), "llama3.2", 1_000_000, 1_000_000);

        Assert.Equal(0m, e.ActualEur);
    }

    [Fact]
    public void Le_cout_reel_suit_le_bareme_du_modele_facture()
    {
        // 1M tokens in × 2,8 € + 1M out × 14 € = 16,8 €
        var e = CostEstimator.Estimate(Options(), "claude-sonnet", 1_000_000, 1_000_000);

        Assert.Equal(16.8m, e.ActualEur);
    }

    [Fact]
    public void Le_calcul_est_proportionnel_au_nombre_de_tokens()
    {
        // 500 in × 2,8/1M + 250 out × 14/1M = 0,0014 + 0,0035 = 0,0049 €
        var e = CostEstimator.Estimate(Options(), "claude-sonnet", 500, 250);

        Assert.Equal(0.0049m, e.ActualEur);
    }

    [Fact]
    public void Un_run_local_est_projete_sur_le_modele_de_reference()
    {
        // Coût réel nul (local), mais projection non nulle : l'argument business.
        var e = CostEstimator.Estimate(Options(), "llama3.2", 1_000_000, 1_000_000);

        Assert.Equal(0m, e.ActualEur);
        Assert.Equal("claude-sonnet", e.ProjectedModel);
        Assert.Equal(16.8m, e.ProjectedEur);
    }

    [Fact]
    public void Pas_de_projection_si_le_modele_est_deja_le_modele_de_reference()
    {
        // Projeter claude-sonnet sur lui-même n'apporterait rien.
        var e = CostEstimator.Estimate(Options(), "claude-sonnet", 1000, 1000);

        Assert.Null(e.ProjectedEur);
        Assert.Null(e.ProjectedModel);
    }

    [Fact]
    public void Un_modele_absent_du_bareme_coute_zero_sans_planter()
    {
        var e = CostEstimator.Estimate(Options(), "modele-inconnu", 1000, 1000);

        Assert.Equal(0m, e.ActualEur);
    }

    [Fact]
    public void Pas_de_projection_si_aucun_modele_de_reference_configure()
    {
        var options = Options();
        options.ReferenceModel = null;

        var e = CostEstimator.Estimate(options, "llama3.2", 1000, 1000);

        Assert.Null(e.ProjectedEur);
    }
}
