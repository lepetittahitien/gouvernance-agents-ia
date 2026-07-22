namespace TraceAgentApi.Audit;

public enum AuditActorType
{
    Agent,
    User,
    System,
}

/// ⚠️ Ces valeurs sont persistées en entier ET leur nom entre dans le calcul du hash d'audit.
/// Toute nouvelle valeur doit être ajoutée EN FIN d'énumération : insérer au milieu décalerait
/// les entiers déjà stockés, changerait le nom associé aux entrées existantes, et invaliderait
/// toute la chaîne de hachage.
public enum AuditAction
{
    RunStarted,
    ToolInvoked,
    GuardrailViolation,
    BudgetBreach,
    RunCompleted,
    ToolDenied,
}

public record AuditEntryDto(
    long Sequence,
    DateTimeOffset Timestamp,
    AuditActorType ActorType,
    string ActorId,
    AuditAction Action,
    string ResourceType,
    string? ResourceId,
    string? Details,
    string Hash,
    string PreviousHash);

/// Résultat de la vérification d'intégrité de la chaîne.
public record AuditChainVerification(
    bool IsIntact,
    long EntriesChecked,
    long? FirstBrokenSequence,
    string? Explanation);
