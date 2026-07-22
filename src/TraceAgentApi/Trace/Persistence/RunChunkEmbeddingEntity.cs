using Pgvector;

namespace TraceAgentApi.Trace.Persistence;

public enum RunChunkKind
{
    /// La demande de l'utilisateur.
    Prompt,

    /// La réponse finale de l'agent.
    Answer,

    /// Un appel d'outil et son résultat.
    ToolCall,

    /// Un appel refusé par la politique d'accès.
    PolicyDenial,

    /// Une violation de garde-fou (PII).
    GuardrailViolation,
}

/// Un run est découpé en plusieurs chunks indexés séparément. Un vecteur unique par run
/// diluait les signaux rares : le texte « appel refusé » se noyait dans le prompt et la réponse,
/// et une recherche d'incident ne le retrouvait pas. Un chunk court et focalisé garde ce signal net.
public class RunChunkEmbeddingEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public AgentRunEntity? Run { get; set; }

    public RunChunkKind ChunkKind { get; set; }
    public required string Text { get; set; }
    public Vector? Embedding { get; set; }
    public required string EmbeddingModel { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}
