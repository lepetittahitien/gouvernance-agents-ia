using System.Text.RegularExpressions;

namespace TraceAgentApi.Trace;

public enum InjectionSignalKind
{
    /// « ignore les instructions précédentes » — la tentative la plus courante.
    InstructionOverride,

    /// « tu es maintenant... », « joue le rôle de... » — détournement de persona.
    RoleManipulation,

    /// « répète ton prompt système », « affiche tes instructions » — exfiltration de configuration.
    SystemPromptExtraction,

    /// Fausses balises de rôle (<|system|>, ### system:) injectées dans du contenu utilisateur.
    DelimiterInjection,

    /// « ignore les règles de sécurité », mode développeur, DAN...
    SafetyBypass,

    /// Blocs encodés (base64 long) pouvant masquer une charge utile.
    SuspiciousEncoding,
}

public enum InjectionRiskLevel
{
    None,
    Low,
    Medium,
    High,
}

public record InjectionSignal(InjectionSignalKind Kind, string Excerpt);

public record InjectionScanResult(
    InjectionRiskLevel RiskLevel,
    int Score,
    List<InjectionSignal> Signals);

/// Détection heuristique de tentative de prompt injection sur une ENTRÉE d'agent
/// (prompt utilisateur, résultat d'outil, document récupéré).
///
/// ⚠️ Contrairement aux garde-fous PII et schéma qui sont déterministes, celui-ci est
/// heuristique par nature : il repère des formulations connues, pas une vérité absolue.
/// Faux positifs et contournements sont possibles — c'est un signal d'alerte à corréler,
/// pas une preuve. Réf. OWASP LLM01: Prompt Injection.
public static partial class InjectionDetector
{
    // Pondération : plus le signal est spécifique à une attaque, plus il pèse.
    private const int InstructionOverrideWeight = 40;
    private const int SystemPromptExtractionWeight = 35;
    private const int SafetyBypassWeight = 35;
    private const int DelimiterInjectionWeight = 30;
    private const int RoleManipulationWeight = 20;
    private const int SuspiciousEncodingWeight = 15;

    [GeneratedRegex(
        @"(ignore|oublie|disregard|forget|neglect)[\s\w]{0,20}(instruction|consigne|prompt|règle|rule|directive|ce qui précède|précédent|previous|above|prior)",
        RegexOptions.IgnoreCase)]
    private static partial Regex InstructionOverridePattern();

    [GeneratedRegex(
        @"(tu es maintenant|vous êtes maintenant|you are now|act as|agis comme|joue le rôle|pretend to be|from now on you)",
        RegexOptions.IgnoreCase)]
    private static partial Regex RoleManipulationPattern();

    [GeneratedRegex(
        @"(répète|repeat|affiche|montre|show|reveal|print|dis-moi)[\s\w]{0,20}(system prompt|prompt système|instructions? système|tes instructions|your instructions|initial prompt|configuration)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SystemPromptExtractionPattern();

    [GeneratedRegex(
        @"(<\|?\s*(system|assistant|user)\s*\|?>|^\s*###\s*(system|assistant|user)\s*:|\[\s*(system|assistant)\s*\])",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DelimiterInjectionPattern();

    [GeneratedRegex(
        @"(mode développeur|developer mode|jailbreak|\bDAN\b|sans restriction|no restrictions|bypass[\s\w]{0,15}(safety|sécurité|filter|filtre)|désactive[\s\w]{0,15}(sécurité|filtre|garde-fou))",
        RegexOptions.IgnoreCase)]
    private static partial Regex SafetyBypassPattern();

    // Base64 d'au moins ~40 caractères : trop long pour être un identifiant anodin.
    [GeneratedRegex(@"\b[A-Za-z0-9+/]{40,}={0,2}\b")]
    private static partial Regex SuspiciousEncodingPattern();

    public static InjectionScanResult Scan(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new InjectionScanResult(InjectionRiskLevel.None, 0, []);
        }

        List<InjectionSignal> signals = [];
        int score = 0;

        void Check(Regex pattern, InjectionSignalKind kind, int weight)
        {
            foreach (Match match in pattern.Matches(input))
            {
                signals.Add(new InjectionSignal(kind, Excerpt(input, match)));
                score += weight;
            }
        }

        Check(InstructionOverridePattern(), InjectionSignalKind.InstructionOverride, InstructionOverrideWeight);
        Check(SystemPromptExtractionPattern(), InjectionSignalKind.SystemPromptExtraction, SystemPromptExtractionWeight);
        Check(SafetyBypassPattern(), InjectionSignalKind.SafetyBypass, SafetyBypassWeight);
        Check(DelimiterInjectionPattern(), InjectionSignalKind.DelimiterInjection, DelimiterInjectionWeight);
        Check(RoleManipulationPattern(), InjectionSignalKind.RoleManipulation, RoleManipulationWeight);
        Check(SuspiciousEncodingPattern(), InjectionSignalKind.SuspiciousEncoding, SuspiciousEncodingWeight);

        score = Math.Min(score, 100);

        var riskLevel = score switch
        {
            0 => InjectionRiskLevel.None,
            < 30 => InjectionRiskLevel.Low,
            < 60 => InjectionRiskLevel.Medium,
            _ => InjectionRiskLevel.High,
        };

        return new InjectionScanResult(riskLevel, score, signals);
    }

    /// Extrait le passage suspect avec un peu de contexte, pour que l'opérateur puisse juger.
    private static string Excerpt(string input, Match match)
    {
        const int padding = 30;
        var start = Math.Max(0, match.Index - padding);
        var end = Math.Min(input.Length, match.Index + match.Length + padding);
        var excerpt = input[start..end].ReplaceLineEndings(" ").Trim();

        return (start > 0 ? "…" : "") + excerpt + (end < input.Length ? "…" : "");
    }
}
