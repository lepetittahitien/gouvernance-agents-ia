namespace TraceAgentApi.Trace.Persistence;

public enum ExternalScanKind
{
    Pii,
    SchemaValidation,
    InjectionDetection,
}

/// Trace d'un appel aux endpoints découplés (/scan, /validate, /detect-injection).
///
/// Règle de confidentialité : le texte scanné n'est JAMAIS stocké — c'est la donnée du
/// système client. Seules les métadonnées du verdict sont conservées (types et comptes,
/// chemins de violation, familles de signaux). Même principe que la persistance PII de T2.
public class ExternalScanEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public ExternalScanKind Kind { get; set; }

    /// Identifiant libre fourni par l'appelant (ex: "support-bot-prod") —
    /// sans lui, impossible de savoir quel système tiers a déclenché la détection.
    public string? Source { get; set; }

    public bool HasViolation { get; set; }

    /// Résumé privacy-safe du verdict, sérialisé (contenu variable selon Kind).
    public required string SummaryJson { get; set; }
}
