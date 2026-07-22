using System.Text.RegularExpressions;

namespace TraceAgentApi.Trace;

public enum PiiType
{
    Email,
    PhoneFr,
    IbanFr,
    SocialSecurityFr,
    CreditCard,
}

public record PiiFinding(PiiType Type, string RedactedValue);

/// Détection déterministe (regex/validation) de fuite PII dans une sortie de LLM probabiliste — le garde-fou central de T2.
public static partial class PiiScanner
{
    [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<!\d)(?:\+33\s?|0)[1-9](?:[\s.-]?\d{2}){4}(?!\d)")]
    private static partial Regex PhoneFrPattern();

    [GeneratedRegex(@"\bFR\d{2}(?:[ ]?\d{4}){5}[ ]?\d{1,3}\b")]
    private static partial Regex IbanFrPattern();

    // Numéro de sécurité sociale français (NIR) — sexe(1) année(2) mois(2) département(2) commune(3) ordre(3) [clé(2)].
    // Limite connue : ne gère pas les départements corses (2A/2B), volontairement laissé de côté pour ce premier jalon.
    [GeneratedRegex(@"\b[12]\d{2}(0[1-9]|1[0-2])\d{2}\d{3}\d{3}(\d{2})?\b")]
    private static partial Regex SocialSecurityFrPattern();

    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b")]
    private static partial Regex CreditCardCandidatePattern();

    public static List<PiiFinding> Scan(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<PiiFinding> findings = [];

        findings.AddRange(EmailPattern().Matches(text).Select(m => new PiiFinding(PiiType.Email, Redact(m.Value))));
        findings.AddRange(PhoneFrPattern().Matches(text).Select(m => new PiiFinding(PiiType.PhoneFr, Redact(m.Value))));
        findings.AddRange(IbanFrPattern().Matches(text).Select(m => new PiiFinding(PiiType.IbanFr, Redact(m.Value))));
        findings.AddRange(SocialSecurityFrPattern().Matches(text).Select(m => new PiiFinding(PiiType.SocialSecurityFr, Redact(m.Value))));

        foreach (Match match in CreditCardCandidatePattern().Matches(text))
        {
            var digitsOnly = new string(match.Value.Where(char.IsDigit).ToArray());
            if (PassesLuhnCheck(digitsOnly))
            {
                findings.Add(new PiiFinding(PiiType.CreditCard, Redact(match.Value)));
            }
        }

        return findings;
    }

    /// Remplace toute PII trouvée par sa forme caviardée. Utilisé pour les exports de conformité :
    /// un document d'audit ne doit pas être lui-même un véhicule de fuite de données personnelles.
    /// S'appuie sur les mêmes motifs que Scan — une seule source de vérité.
    public static string RedactAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? "";
        }

        var result = text;
        result = EmailPattern().Replace(result, m => Redact(m.Value));
        result = PhoneFrPattern().Replace(result, m => Redact(m.Value));
        result = IbanFrPattern().Replace(result, m => Redact(m.Value));
        result = SocialSecurityFrPattern().Replace(result, m => Redact(m.Value));
        result = CreditCardCandidatePattern().Replace(result, m =>
        {
            var digitsOnly = new string(m.Value.Where(char.IsDigit).ToArray());
            return PassesLuhnCheck(digitsOnly) ? Redact(m.Value) : m.Value;
        });

        return result;
    }

    private static bool PassesLuhnCheck(string digits)
    {
        int sum = 0;
        bool alternate = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    private static string Redact(string value)
    {
        if (value.Length <= 4)
        {
            return "****";
        }
        return value[..2] + new string('*', value.Length - 4) + value[^2..];
    }
}
