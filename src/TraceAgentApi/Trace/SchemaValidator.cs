using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

namespace TraceAgentApi.Trace;

public record SchemaViolation(string Path, string Message);

public record SchemaValidationResult(
    bool IsValid,
    string? ParseError,
    List<SchemaViolation> Violations);

/// Validation déterministe qu'une sortie de LLM respecte un schéma JSON attendu — garde-fou format de T2.
public static partial class SchemaValidator
{
    // Les LLM encadrent souvent leur JSON de ```json ... ``` — on extrait le contenu du bloc.
    [GeneratedRegex(@"```(?:json)?\s*(.*?)\s*```", RegexOptions.Singleline)]
    private static partial Regex MarkdownCodeFencePattern();

    public static SchemaValidationResult Validate(string output, JsonElement schemaDefinition)
    {
        var candidate = ExtractJsonCandidate(output);

        if (candidate is null)
        {
            return new SchemaValidationResult(false, "Aucun JSON trouvé dans la sortie.", []);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(candidate);
        }
        catch (JsonException ex)
        {
            return new SchemaValidationResult(false, $"JSON invalide : {ex.Message}", []);
        }

        using (document)
        {
            JsonSchema schema;
            try
            {
                schema = JsonSerializer.Deserialize<JsonSchema>(schemaDefinition)
                    ?? throw new JsonException("Schéma vide.");
            }
            catch (JsonException ex)
            {
                return new SchemaValidationResult(false, $"Schéma fourni invalide : {ex.Message}", []);
            }

            var evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
            });

            if (evaluation.IsValid)
            {
                return new SchemaValidationResult(true, null, []);
            }

            var violations = Flatten(evaluation)
                .Where(node => !node.IsValid && node.Errors is { Count: > 0 })
                .SelectMany(node => node.Errors!.Select(error =>
                    new SchemaViolation(
                        node.InstanceLocation.ToString() is { Length: > 0 } path ? path : "/",
                        error.Value)))
                .ToList();

            return new SchemaValidationResult(false, null, violations);
        }
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;
        foreach (var child in results.Details ?? [])
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    /// Isole le JSON dans une sortie qui peut contenir du texte autour (bloc markdown, phrase d'intro...).
    private static string? ExtractJsonCandidate(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var fenced = MarkdownCodeFencePattern().Match(output);
        if (fenced.Success)
        {
            return fenced.Groups[1].Value.Trim();
        }

        var trimmed = output.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return trimmed;
        }

        // Dernier recours : le premier objet/tableau JSON trouvé dans le texte.
        var firstBrace = trimmed.IndexOfAny(['{', '[']);
        if (firstBrace < 0)
        {
            return null;
        }

        var opening = trimmed[firstBrace];
        var closing = opening == '{' ? '}' : ']';
        var lastBrace = trimmed.LastIndexOf(closing);

        return lastBrace > firstBrace ? trimmed[firstBrace..(lastBrace + 1)] : null;
    }
}
