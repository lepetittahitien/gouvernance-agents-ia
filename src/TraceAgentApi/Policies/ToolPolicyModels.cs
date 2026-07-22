namespace TraceAgentApi.Policies;

public enum PolicyDecision
{
    Allow,
    Deny,
}

/// Contrainte sur les *données* passées à un outil, pas seulement sur l'outil lui-même.
public record ArgumentRule(
    string Argument,
    List<string>? AllowedValues = null,
    string? DeniedPattern = null);

public record ToolRule(
    string Tool,
    List<ArgumentRule>? ArgumentRules = null);

public record AgentPolicy(
    string AgentId,
    List<ToolRule>? AllowedTools = null,
    List<string>? DeniedTools = null);

public record ToolPolicyConfig(
    List<AgentPolicy> Agents,
    /// Si aucune règle ne correspond : refuser par défaut (posture sûre) ou autoriser.
    bool DefaultDeny = true);

public record PolicyEvaluation(
    PolicyDecision Decision,
    string Reason,
    string AgentId,
    string Tool);
