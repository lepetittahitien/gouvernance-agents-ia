namespace TraceAgentApi.Trace;

public class ModelPricing
{
    /// Prix en euros pour 1 million de tokens d'entrée.
    public decimal InputPerMillionEur { get; set; }

    /// Prix en euros pour 1 million de tokens de sortie.
    public decimal OutputPerMillionEur { get; set; }
}

public class PricingOptions
{
    public const string SectionName = "Pricing";

    /// Barème par nom de modèle. Un modèle absent = coût 0 (cas d'un modèle local).
    public Dictionary<string, ModelPricing> Models { get; set; } = new();

    /// Modèle de référence pour la projection « combien ça coûterait sur un provider payant ».
    /// Répond à la vraie question d'un décideur quand l'agent tourne en local à coût nul.
    public string? ReferenceModel { get; set; }
}

public record CostEstimate(
    decimal ActualEur,
    string ActualModel,
    decimal? ProjectedEur,
    string? ProjectedModel);

/// Calcul déterministe du coût à partir des compteurs de tokens et d'un barème configurable.
///
/// En local (Ollama) le coût réel est nul, mais la *projection* sur un modèle de référence
/// montre ce que le même run coûterait sur un provider facturé — l'argument business concret.
public class CostEstimator(IConfiguration configuration)
{
    private PricingOptions LoadOptions()
    {
        var options = new PricingOptions();
        configuration.GetSection(PricingOptions.SectionName).Bind(options);
        return options;
    }

    public CostEstimate Estimate(string model, int inputTokens, int outputTokens) =>
        Estimate(LoadOptions(), model, inputTokens, outputTokens);

    /// Calcul pur — le barème est fourni, aucune I/O. Testable sans configuration ni DI.
    public static CostEstimate Estimate(PricingOptions options, string model, int inputTokens, int outputTokens)
    {
        var actual = ComputeFor(options, model, inputTokens, outputTokens) ?? 0m;

        decimal? projected = null;
        string? projectedModel = null;

        // On ne projette que si le modèle de référence est distinct de celui qui a tourné
        // (projeter un modèle sur lui-même n'apporterait rien).
        if (!string.IsNullOrWhiteSpace(options.ReferenceModel) &&
            !string.Equals(options.ReferenceModel, model, StringComparison.OrdinalIgnoreCase))
        {
            projected = ComputeFor(options, options.ReferenceModel, inputTokens, outputTokens);
            if (projected is not null)
            {
                projectedModel = options.ReferenceModel;
            }
        }

        return new CostEstimate(actual, model, projected, projectedModel);
    }

    private static decimal? ComputeFor(PricingOptions options, string model, int inputTokens, int outputTokens)
    {
        if (!options.Models.TryGetValue(model, out var pricing))
        {
            return null;
        }

        return inputTokens / 1_000_000m * pricing.InputPerMillionEur
             + outputTokens / 1_000_000m * pricing.OutputPerMillionEur;
    }
}
