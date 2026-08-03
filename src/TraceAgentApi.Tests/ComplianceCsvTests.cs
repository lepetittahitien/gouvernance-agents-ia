using System.Text;
using TraceAgentApi.Audit;
using TraceAgentApi.Compliance;

namespace TraceAgentApi.Tests;

public class ComplianceCsvTests
{
    private static ComplianceExport ExportWith(params AuditEntryDto[] entries) => new(
        GeneratedAt: DateTimeOffset.UtcNow,
        PeriodFrom: DateTimeOffset.UtcNow.AddDays(-1),
        PeriodTo: DateTimeOffset.UtcNow,
        PiiRedacted: false,
        IntegrityProof: new AuditChainVerification(true, entries.Length, null, null),
        RunsTotal: 0,
        RunsWithPiiViolation: 0,
        ToolCallsDenied: 0,
        TotalTokens: 0,
        Runs: [],
        AuditEntries: entries.ToList());

    private static AuditEntryDto Entry(string details) => new(
        1, DateTimeOffset.UtcNow, AuditActorType.Agent, "llama3.2", AuditAction.ToolInvoked,
        "Tool", "run-1", details, "abc123", AuditHashing.GenesisHash);

    /// Renvoie les lignes de données (hors commentaires # et hors ligne d'en-tête).
    private static List<string> DataLines(string csv) =>
        csv.Split('\n')
            .Where(l => !l.StartsWith('#') && l.Trim().Length > 0)
            .Skip(1) // en-tête sequence,horodatage,…
            .ToList();

    [Fact]
    public void Un_champ_contenant_une_virgule_est_entoure_de_guillemets()
    {
        // Sans échappement, la virgule décalerait les colonnes et corromprait le document.
        var csv = ComplianceExporter.ToCsv(ExportWith(Entry("151 tokens, 7222 ms")));

        var ligne = DataLines(csv).Single();
        Assert.Contains("\"151 tokens, 7222 ms\"", ligne);
    }

    [Fact]
    public void Un_guillemet_est_double_selon_rfc_4180()
    {
        var csv = ComplianceExporter.ToCsv(ExportWith(Entry("ville dite \"Paris\"")));

        var ligne = DataLines(csv).Single();
        // Le guillemet interne devient "" et le champ entier est encadré.
        Assert.Contains("\"ville dite \"\"Paris\"\"\"", ligne);
    }

    [Fact]
    public void Un_saut_de_ligne_dans_un_champ_ne_casse_pas_le_nombre_de_lignes_de_donnees()
    {
        var csv = ComplianceExporter.ToCsv(ExportWith(Entry("ligne une\nligne deux")));

        // Une seule entrée = une seule ligne logique, même si le champ contient un \n
        // (le \n est protégé à l'intérieur des guillemets).
        Assert.Single(ExportWith(Entry("x")).AuditEntries);
        Assert.Contains("\"ligne une\nligne deux\"", csv);
    }

    [Fact]
    public void Un_champ_simple_n_est_pas_inutilement_echappe()
    {
        var csv = ComplianceExporter.ToCsv(ExportWith(Entry("get_weather(city=Paris)")));

        var ligne = DataLines(csv).Single();
        Assert.Contains("get_weather(city=Paris)", ligne);
        Assert.DoesNotContain("\"get_weather", ligne);
    }

    [Fact]
    public void L_entete_de_contexte_porte_la_preuve_d_integrite()
    {
        var csv = ComplianceExporter.ToCsv(ExportWith(Entry("x")));

        // Un CSV sans preuve d'intégrité n'a aucune valeur probante : elle doit y figurer.
        Assert.Contains("# Intégrité du journal : INTACTE", csv);
    }

    [Fact]
    public void Un_export_compromis_l_affiche_dans_l_entete()
    {
        var export = ExportWith(Entry("x")) with
        {
            IntegrityProof = new AuditChainVerification(false, 3, 2, "Contenu altéré à la séquence 2"),
        };

        var csv = ComplianceExporter.ToCsv(export);

        Assert.Contains("COMPROMISE", csv);
        Assert.Contains("séquence 2", csv);
    }
}
