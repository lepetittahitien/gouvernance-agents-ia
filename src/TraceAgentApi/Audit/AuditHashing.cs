using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Audit;

/// Logique pure de hachage et de vérification de la chaîne d'audit — aucune dépendance,
/// donc testable unitairement. AuditLogger s'occupe de la persistance et délègue ici.
public static class AuditHashing
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// Sérialisation canonique : tout changement d'ordre ou de format changerait le hash,
    /// donc ce format ne doit JAMAIS évoluer sans migration du journal existant.
    public static string ComputeHash(AuditEntryEntity e)
    {
        // Séparateur "unit separator" (U+001F) : absent des contenus textuels normaux,
        // il évite qu'une combinaison de champs différente produise la même chaîne canonique.
        var canonical = string.Join('\u001F',
            e.Sequence.ToString(CultureInfo.InvariantCulture),
            e.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            e.ActorType.ToString(),
            e.ActorId,
            e.Action.ToString(),
            e.ResourceType,
            e.ResourceId ?? "",
            e.Details ?? "",
            e.PreviousHash);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// Postgres stocke `timestamptz` à la microseconde ; .NET est plus fin. Sans troncature
    /// avant hachage, le hash recalculé après relecture ne correspondrait plus.
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var excess = value.Ticks % (TimeSpan.TicksPerMillisecond / 1000);
        return value.AddTicks(-excess);
    }

    /// Parcourt la chaîne (supposée ordonnée par séquence) et signale la première rupture.
    public static AuditChainVerification VerifyChain(IReadOnlyList<AuditEntryEntity> entries)
    {
        var expectedPreviousHash = GenesisHash;

        foreach (var entry in entries)
        {
            if (entry.PreviousHash != expectedPreviousHash)
            {
                return new AuditChainVerification(false, entries.Count, entry.Sequence,
                    $"Rupture de chaînage à la séquence {entry.Sequence} : " +
                    $"chaînage attendu {expectedPreviousHash[..12]}…, trouvé {entry.PreviousHash[..12]}…. " +
                    "Une entrée a été supprimée, insérée ou réordonnée.");
            }

            var recomputed = ComputeHash(entry);
            if (recomputed != entry.Hash)
            {
                return new AuditChainVerification(false, entries.Count, entry.Sequence,
                    $"Contenu altéré à la séquence {entry.Sequence} : " +
                    $"hash stocké {entry.Hash[..12]}…, hash recalculé {recomputed[..12]}…. " +
                    "Le contenu de cette entrée a été modifié après écriture.");
            }

            expectedPreviousHash = entry.Hash;
        }

        return new AuditChainVerification(true, entries.Count, null, null);
    }
}
