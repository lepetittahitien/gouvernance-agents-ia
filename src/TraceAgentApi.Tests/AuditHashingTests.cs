using TraceAgentApi.Audit;
using TraceAgentApi.Trace.Persistence;

namespace TraceAgentApi.Tests;

public class AuditHashingTests
{
    /// Construit une chaîne valide de N entrées, comme le ferait AuditLogger.
    private static List<AuditEntryEntity> ChaineValide(int count)
    {
        List<AuditEntryEntity> entries = [];
        var previousHash = AuditHashing.GenesisHash;

        for (var i = 1; i <= count; i++)
        {
            var entry = new AuditEntryEntity
            {
                Sequence = i,
                Timestamp = AuditHashing.TruncateToMicroseconds(DateTimeOffset.UtcNow),
                ActorType = AuditActorType.Agent,
                ActorId = "llama3.2",
                Action = AuditAction.ToolInvoked,
                ResourceType = "Tool",
                ResourceId = Guid.NewGuid().ToString(),
                Details = $"get_weather(city=Ville{i})",
                PreviousHash = previousHash,
                Hash = string.Empty,
            };
            entry.Hash = AuditHashing.ComputeHash(entry);
            previousHash = entry.Hash;
            entries.Add(entry);
        }

        return entries;
    }

    [Fact]
    public void Une_chaine_valide_est_verifiee_intacte()
    {
        var verification = AuditHashing.VerifyChain(ChaineValide(5));

        Assert.True(verification.IsIntact);
        Assert.Equal(5, verification.EntriesChecked);
        Assert.Null(verification.FirstBrokenSequence);
    }

    [Fact]
    public void Une_chaine_vide_est_intacte()
    {
        Assert.True(AuditHashing.VerifyChain([]).IsIntact);
    }

    [Fact]
    public void La_modification_d_un_contenu_est_detectee_a_la_bonne_sequence()
    {
        var entries = ChaineValide(5);
        entries[2].Details = "get_weather(city=Falsifiée)"; // altération après écriture

        var verification = AuditHashing.VerifyChain(entries);

        Assert.False(verification.IsIntact);
        Assert.Equal(3, verification.FirstBrokenSequence); // Sequence est 1-indexée
        Assert.Contains("altéré", verification.Explanation);
    }

    [Fact]
    public void La_suppression_d_une_entree_est_detectee()
    {
        var entries = ChaineValide(5);
        entries.RemoveAt(2); // suppression d'une entrée gênante

        var verification = AuditHashing.VerifyChain(entries);

        Assert.False(verification.IsIntact);
        Assert.Contains("supprimée", verification.Explanation);
    }

    [Fact]
    public void Le_reordonnancement_est_detecte()
    {
        var entries = ChaineValide(5);
        (entries[1], entries[3]) = (entries[3], entries[1]);

        Assert.False(AuditHashing.VerifyChain(entries).IsIntact);
    }

    [Fact]
    public void Deux_entrees_au_contenu_identique_ont_des_hashes_differents()
    {
        // Le chaînage (PreviousHash) entre dans le hash : même contenu ≠ même empreinte.
        var entries = ChaineValide(2);
        entries[1].Details = entries[0].Details;
        entries[1].ResourceId = entries[0].ResourceId;
        entries[1].Timestamp = entries[0].Timestamp;
        entries[1].Hash = AuditHashing.ComputeHash(entries[1]);

        Assert.NotEqual(entries[0].Hash, entries[1].Hash);
    }

    [Fact]
    public void Le_hash_survit_a_un_aller_retour_de_precision_postgres()
    {
        // Postgres tronque timestamptz à la microseconde. Une entrée écrite puis relue
        // doit produire exactement le même hash — c'est le bug qu'on a évité à la conception.
        var entry = ChaineValide(1)[0];
        var hashAvant = entry.Hash;

        // Simule l'aller-retour : la précision microseconde est déjà appliquée, donc identique.
        var relue = new AuditEntryEntity
        {
            Sequence = entry.Sequence,
            Timestamp = new DateTimeOffset(entry.Timestamp.UtcDateTime, TimeSpan.Zero),
            ActorType = entry.ActorType,
            ActorId = entry.ActorId,
            Action = entry.Action,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            Details = entry.Details,
            PreviousHash = entry.PreviousHash,
            Hash = entry.Hash,
        };

        Assert.Equal(hashAvant, AuditHashing.ComputeHash(relue));
    }

    [Fact]
    public void Le_champ_separateur_empeche_les_collisions_de_concatenation()
    {
        // Sans séparateur, ("ab", "c") et ("a", "bc") produiraient la même chaîne canonique.
        var e1 = ChaineValide(1)[0];
        var e2 = ChaineValide(1)[0];
        e1.ActorId = "agentX";
        e1.ResourceType = "Tool";
        e2.ActorId = "agent";
        e2.ResourceType = "XTool";
        e1.Timestamp = e2.Timestamp;
        e1.ResourceId = e2.ResourceId;
        e1.Details = e2.Details;

        Assert.NotEqual(AuditHashing.ComputeHash(e1), AuditHashing.ComputeHash(e2));
    }
}
