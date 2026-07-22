using TraceAgentApi.Audit;

namespace TraceAgentApi.Trace.Persistence;

/// Entrée du journal d'audit. Écrite une fois, jamais modifiée : le chaînage par hash
/// rend toute altération postérieure détectable (cf. AuditLogger.VerifyChainAsync).
public class AuditEntryEntity
{
    public long Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public AuditActorType ActorType { get; set; }
    public required string ActorId { get; set; }
    public AuditAction Action { get; set; }
    public required string ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? Details { get; set; }

    /// Hash de l'entrée précédente — c'est ce qui constitue la chaîne.
    public required string PreviousHash { get; set; }

    /// SHA-256 du contenu canonique de cette entrée, chaînage inclus.
    public required string Hash { get; set; }
}
