using Pgvector;

namespace TraceAgentApi.Trace.Persistence;

/// Représentation vectorielle d'un run, pour la recherche sémantique (T5).
public class RunEmbeddingEntity
{
    public Guid RunId { get; set; }
    public AgentRunEntity? Run { get; set; }

    /// Texte réellement indexé (prompt + réponse + outils + violations).
    /// Conservé pour pouvoir ré-indexer sans rejouer le run, et pour l'inspection.
    public required string IndexedText { get; set; }

    /// 768 dimensions = sortie de `nomic-embed-text`. Changer de modèle d'embedding
    /// impose de ré-indexer : les vecteurs de modèles différents ne sont pas comparables.
    public Vector? Embedding { get; set; }

    public required string EmbeddingModel { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}
