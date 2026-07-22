using System.Text.Json;
using System.Text.RegularExpressions;

namespace TraceAgentApi.Policies;

/// Évalue si un agent a le droit d'appeler un outil donné, avec les arguments donnés.
///
/// Ordre d'évaluation (volontairement strict) :
///   1. Refus explicite → Deny (le refus l'emporte toujours sur l'autorisation)
///   2. Outil autorisé + contraintes d'arguments respectées → Allow
///   3. Outil autorisé mais contrainte violée → Deny
///   4. Aucune règle correspondante → DefaultDeny (par défaut : Deny)
public class ToolPolicyEvaluator(IConfiguration configuration, ILogger<ToolPolicyEvaluator> logger)
{
    private static readonly JsonSerializerOptions FileOptions = new() { PropertyNameCaseInsensitive = true };

    private ToolPolicyConfig LoadConfig()
    {
        var path = configuration["Policies:ToolPoliciesPath"] ?? "Policies/tool-policies.json";

        if (!File.Exists(path))
        {
            // Pas de fichier de règles = aucune autorisation accordée. Fermé par défaut :
            // une erreur de déploiement ne doit pas ouvrir tous les outils.
            logger.LogWarning("Fichier de règles introuvable ({Path}) — tous les outils sont refusés.", path);
            return new ToolPolicyConfig([], DefaultDeny: true);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ToolPolicyConfig>(json, FileOptions)
            ?? new ToolPolicyConfig([], DefaultDeny: true);
    }

    public PolicyEvaluation Evaluate(string agentId, string tool, IDictionary<string, object?>? arguments = null)
    {
        var config = LoadConfig();
        var policy = config.Agents.FirstOrDefault(a =>
            string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

        if (policy is null)
        {
            return Decide(config.DefaultDeny, agentId, tool,
                denyReason: $"Aucune règle définie pour l'agent « {agentId} ».",
                allowReason: $"Aucune règle pour « {agentId} », autorisation par défaut.");
        }

        if (policy.DeniedTools?.Any(t => Matches(t, tool)) == true)
        {
            return new PolicyEvaluation(PolicyDecision.Deny,
                $"L'outil « {tool} » figure dans la liste de refus de « {agentId} ».", agentId, tool);
        }

        var toolRule = policy.AllowedTools?.FirstOrDefault(r => Matches(r.Tool, tool));

        if (toolRule is null)
        {
            return Decide(config.DefaultDeny, agentId, tool,
                denyReason: $"L'outil « {tool} » n'est pas dans les outils autorisés de « {agentId} ».",
                allowReason: $"Aucune règle spécifique pour « {tool} », autorisation par défaut.");
        }

        foreach (var rule in toolRule.ArgumentRules ?? [])
        {
            if (arguments is null || !arguments.TryGetValue(rule.Argument, out var raw))
            {
                continue;
            }

            var value = raw?.ToString() ?? "";

            if (rule.AllowedValues is { Count: > 0 } &&
                !rule.AllowedValues.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
            {
                return new PolicyEvaluation(PolicyDecision.Deny,
                    $"Valeur « {value} » interdite pour l'argument « {rule.Argument} » de « {tool} » " +
                    $"(valeurs autorisées : {string.Join(", ", rule.AllowedValues)}).", agentId, tool);
            }

            if (!string.IsNullOrEmpty(rule.DeniedPattern) &&
                Regex.IsMatch(value, rule.DeniedPattern, RegexOptions.IgnoreCase))
            {
                return new PolicyEvaluation(PolicyDecision.Deny,
                    $"Valeur « {value} » de l'argument « {rule.Argument} » correspond à un motif interdit.",
                    agentId, tool);
            }
        }

        return new PolicyEvaluation(PolicyDecision.Allow,
            $"L'agent « {agentId} » est autorisé à appeler « {tool} ».", agentId, tool);
    }

    private static PolicyEvaluation Decide(
        bool defaultDeny, string agentId, string tool, string denyReason, string allowReason) =>
        defaultDeny
            ? new PolicyEvaluation(PolicyDecision.Deny, denyReason, agentId, tool)
            : new PolicyEvaluation(PolicyDecision.Allow, allowReason, agentId, tool);

    private static bool Matches(string pattern, string tool) =>
        pattern == "*" || string.Equals(pattern, tool, StringComparison.OrdinalIgnoreCase);
}
