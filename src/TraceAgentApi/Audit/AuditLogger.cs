using Microsoft.EntityFrameworkCore;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Audit;

/// Journal d'audit chaîné par hash.
///
/// Garantie réelle (à énoncer honnêtement à un client) : ce n'est pas de l'immuabilité au sens
/// physique — un administrateur de la base peut toujours modifier une ligne. Ce que la chaîne
/// apporte, c'est la **détectabilité** : toute modification, suppression ou insertion casse le
/// chaînage et devient prouvable via VerifyChainAsync. Pour une immuabilité plus forte, il
/// faudrait révoquer UPDATE/DELETE au niveau du rôle SQL et/ou ancrer périodiquement le dernier
/// hash sur un support externe.
public class AuditLogger(TraceDbContext dbContext, ILogger<AuditLogger> logger)
{
    public const string GenesisHash = AuditHashing.GenesisHash;

    /// Les appends doivent être sérialisés : deux écritures concurrentes liraient le même
    /// "dernier hash" et produiraient une chaîne cassée. Ce verrou couvre une instance ;
    /// en multi-instance il faudrait un verrou consultatif Postgres (pg_advisory_lock).
    private static readonly SemaphoreSlim AppendLock = new(1, 1);

    public async Task<AuditEntryDto> AppendAsync(
        AuditActorType actorType,
        string actorId,
        AuditAction action,
        string resourceType,
        string? resourceId = null,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        await AppendLock.WaitAsync(cancellationToken);
        try
        {
            var previousHash = await dbContext.AuditEntries
                .OrderByDescending(e => e.Sequence)
                .Select(e => e.Hash)
                .FirstOrDefaultAsync(cancellationToken) ?? GenesisHash;

            var entity = new AuditEntryEntity
            {
                Timestamp = AuditHashing.TruncateToMicroseconds(DateTimeOffset.UtcNow),
                ActorType = actorType,
                ActorId = actorId,
                Action = action,
                ResourceType = resourceType,
                ResourceId = resourceId,
                Details = details,
                PreviousHash = previousHash,
                Hash = string.Empty,
            };

            dbContext.AuditEntries.Add(entity);
            // Première sauvegarde : laisse Postgres attribuer le numéro de séquence,
            // qui fait partie du contenu haché.
            await dbContext.SaveChangesAsync(cancellationToken);

            entity.Hash = AuditHashing.ComputeHash(entity);
            await dbContext.SaveChangesAsync(cancellationToken);

            return ToDto(entity);
        }
        finally
        {
            AppendLock.Release();
        }
    }

    public async Task<List<AuditEntryDto>> ListAsync(
        Guid? resourceId = null,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.AuditEntries.AsQueryable();

        if (resourceId is not null)
        {
            var asString = resourceId.Value.ToString();
            query = query.Where(e => e.ResourceId == asString);
        }

        var entities = await query
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        return entities.Select(ToDto).ToList();
    }

    /// Recalcule toute la chaîne et signale la première rupture.
    /// La logique pure vit dans AuditHashing (testable sans base) ; ici on charge et on délègue.
    public async Task<AuditChainVerification> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        var entries = await dbContext.AuditEntries
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        var verification = AuditHashing.VerifyChain(entries);

        if (!verification.IsIntact)
        {
            logger.LogWarning("Intégrité du journal d'audit compromise. {Explanation}", verification.Explanation);
        }

        return verification;
    }

    private static AuditEntryDto ToDto(AuditEntryEntity e) => new(
        e.Sequence, e.Timestamp, e.ActorType, e.ActorId, e.Action,
        e.ResourceType, e.ResourceId, e.Details, e.Hash, e.PreviousHash);
}
