using TraceAgentApi.Trace;

namespace TraceAgentApi.Tests;

public class PiiScannerTests
{
    [Theory]
    [InlineData("Contactez-moi à jean.dupont@example.com pour plus d'infos.", PiiType.Email)]
    [InlineData("Mon numéro est le 06 12 34 56 78, appelez-moi.", PiiType.PhoneFr)]
    [InlineData("Joignable au +33 6 12 34 56 78 en journée.", PiiType.PhoneFr)]
    [InlineData("Mon IBAN est FR76 3000 6000 0112 3456 7890 189.", PiiType.IbanFr)]
    [InlineData("Numéro de sécu : 185017612345678.", PiiType.SocialSecurityFr)]
    public void Scan_detecte_chaque_type_de_pii(string texte, PiiType typeAttendu)
    {
        var findings = PiiScanner.Scan(texte);

        Assert.Contains(findings, f => f.Type == typeAttendu);
    }

    [Fact]
    public void Scan_detecte_une_carte_bancaire_valide_luhn()
    {
        // 4539 1488 0343 6467 : numéro de test qui passe la validation de Luhn.
        var findings = PiiScanner.Scan("Voici ma carte : 4539 1488 0343 6467.");

        Assert.Contains(findings, f => f.Type == PiiType.CreditCard);
    }

    [Fact]
    public void Scan_ignore_une_suite_de_chiffres_qui_echoue_luhn()
    {
        // 16 chiffres mais Luhn invalide : un numéro de commande, pas une carte.
        var findings = PiiScanner.Scan("Ma commande fait 1234 5678 9012 3456 articles.");

        Assert.DoesNotContain(findings, f => f.Type == PiiType.CreditCard);
    }

    [Theory]
    [InlineData("Il fait beau à Paris aujourd'hui, 22°C.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Scan_ne_remonte_rien_sur_du_texte_propre(string texte)
    {
        Assert.Empty(PiiScanner.Scan(texte));
    }

    [Fact]
    public void Scan_ne_stocke_jamais_la_valeur_en_clair()
    {
        var findings = PiiScanner.Scan("Écrivez à jean.dupont@example.com.");

        var finding = Assert.Single(findings);
        Assert.DoesNotContain("jean.dupont@example.com", finding.RedactedValue);
        Assert.Contains("*", finding.RedactedValue);
    }

    [Fact]
    public void RedactAll_caviarde_toutes_les_pii_du_texte()
    {
        var texte = "Contact : jean@exemple.fr ou 06 12 34 56 78.";

        var caviardé = PiiScanner.RedactAll(texte);

        Assert.DoesNotContain("jean@exemple.fr", caviardé);
        Assert.DoesNotContain("06 12 34 56 78", caviardé);
        Assert.Contains("Contact :", caviardé); // le texte non-PII est préservé
    }

    [Fact]
    public void RedactAll_laisse_intact_un_texte_sans_pii()
    {
        var texte = "Il fait 22°C à Lyon.";

        Assert.Equal(texte, PiiScanner.RedactAll(texte));
    }

    [Fact]
    public void RedactAll_supporte_null_et_vide()
    {
        Assert.Equal("", PiiScanner.RedactAll(null));
        Assert.Equal("", PiiScanner.RedactAll(""));
    }
}
