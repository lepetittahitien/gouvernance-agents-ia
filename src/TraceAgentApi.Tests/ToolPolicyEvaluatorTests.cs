using TraceAgentApi.Policies;

namespace TraceAgentApi.Tests;

public class ToolPolicyEvaluatorTests
{
    /// Config type : l'agent météo n'a droit qu'à get_weather, et l'argument city
    /// ne doit contenir ni email ni URL (le scénario du bug observé en T2).
    private static ToolPolicyConfig WeatherConfig(bool defaultDeny = true) => new(
        Agents:
        [
            new AgentPolicy(
                AgentId: "llama3.2",
                AllowedTools:
                [
                    new ToolRule("get_weather",
                        ArgumentRules: [new ArgumentRule("city", DeniedPattern: @"@|\bhttps?://")]),
                ],
                DeniedTools: ["delete_database"]),
        ],
        DefaultDeny: defaultDeny);

    private static Dictionary<string, object?> Args(string key, object? value) => new() { [key] = value };

    [Fact]
    public void Autorise_un_outil_permis_avec_argument_sain()
    {
        var r = ToolPolicyEvaluator.Evaluate(WeatherConfig(), "llama3.2", "get_weather", Args("city", "Paris"));

        Assert.Equal(PolicyDecision.Allow, r.Decision);
    }

    [Fact]
    public void Refuse_un_outil_non_declare()
    {
        var r = ToolPolicyEvaluator.Evaluate(WeatherConfig(), "llama3.2", "send_email");

        Assert.Equal(PolicyDecision.Deny, r.Decision);
    }

    [Fact]
    public void Refuse_un_outil_explicitement_interdit()
    {
        var r = ToolPolicyEvaluator.Evaluate(WeatherConfig(), "llama3.2", "delete_database");

        Assert.Equal(PolicyDecision.Deny, r.Decision);
    }

    [Fact]
    public void Le_refus_explicite_l_emporte_meme_si_l_outil_est_aussi_autorise()
    {
        // delete_database dans allowed ET denied → denied gagne (posture sûre).
        var config = new ToolPolicyConfig(
            Agents:
            [
                new AgentPolicy("llama3.2",
                    AllowedTools: [new ToolRule("delete_database")],
                    DeniedTools: ["delete_database"]),
            ],
            DefaultDeny: true);

        var r = ToolPolicyEvaluator.Evaluate(config, "llama3.2", "delete_database");

        Assert.Equal(PolicyDecision.Deny, r.Decision);
    }

    [Theory]
    [InlineData("contact@client.fr")]
    [InlineData("https://evil.com/exfiltrate")]
    public void Refuse_un_argument_correspondant_a_un_motif_interdit(string valeurCity)
    {
        var r = ToolPolicyEvaluator.Evaluate(WeatherConfig(), "llama3.2", "get_weather", Args("city", valeurCity));

        Assert.Equal(PolicyDecision.Deny, r.Decision);
    }

    [Fact]
    public void Refuse_une_valeur_hors_liste_blanche()
    {
        var config = new ToolPolicyConfig(
            Agents:
            [
                new AgentPolicy("llama3.2",
                    AllowedTools:
                    [
                        new ToolRule("get_weather",
                            ArgumentRules: [new ArgumentRule("city", AllowedValues: ["Paris", "Lyon"])]),
                    ]),
            ],
            DefaultDeny: true);

        Assert.Equal(PolicyDecision.Allow,
            ToolPolicyEvaluator.Evaluate(config, "llama3.2", "get_weather", Args("city", "Lyon")).Decision);
        Assert.Equal(PolicyDecision.Deny,
            ToolPolicyEvaluator.Evaluate(config, "llama3.2", "get_weather", Args("city", "Marseille")).Decision);
    }

    [Fact]
    public void Un_agent_inconnu_est_refuse_par_defaut_deny()
    {
        var r = ToolPolicyEvaluator.Evaluate(WeatherConfig(defaultDeny: true), "agent-inconnu", "get_weather");

        Assert.Equal(PolicyDecision.Deny, r.Decision);
    }

    [Fact]
    public void Un_agent_inconnu_est_autorise_si_default_allow()
    {
        var r = ToolPolicyEvaluator.Evaluate(WeatherConfig(defaultDeny: false), "agent-inconnu", "get_weather");

        Assert.Equal(PolicyDecision.Allow, r.Decision);
    }

    [Fact]
    public void Un_wildcard_autorise_tous_les_outils()
    {
        var config = new ToolPolicyConfig(
            Agents: [new AgentPolicy("llama3.2", AllowedTools: [new ToolRule("*")])],
            DefaultDeny: true);

        Assert.Equal(PolicyDecision.Allow,
            ToolPolicyEvaluator.Evaluate(config, "llama3.2", "n_importe_quel_outil").Decision);
    }

    [Fact]
    public void Une_regle_d_argument_ne_s_applique_pas_si_l_argument_est_absent()
    {
        // La contrainte porte sur "city" ; un appel sans "city" ne doit pas être refusé pour ça.
        var r = ToolPolicyEvaluator.Evaluate(WeatherConfig(), "llama3.2", "get_weather", Args("autre", "valeur"));

        Assert.Equal(PolicyDecision.Allow, r.Decision);
    }

    [Fact]
    public void Config_vide_avec_default_deny_refuse_tout()
    {
        // Le cas « fichier de règles absent » : aucune autorisation accordée.
        var config = new ToolPolicyConfig(Agents: [], DefaultDeny: true);

        Assert.Equal(PolicyDecision.Deny,
            ToolPolicyEvaluator.Evaluate(config, "llama3.2", "get_weather", Args("city", "Paris")).Decision);
    }
}
