using System.Text.Json;
using TraceAgentApi.Trace;

namespace TraceAgentApi.Tests;

public class SchemaValidatorTests
{
    private static readonly JsonElement MeteoSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["ville", "temperature"],
          "properties": {
            "ville": { "type": "string" },
            "temperature": { "type": "number" },
            "vent": { "type": "number" }
          }
        }
        """).RootElement;

    [Fact]
    public void Valide_un_json_propre()
    {
        var result = SchemaValidator.Validate(
            """{"ville":"Paris","temperature":15.7,"vent":9.8}""", MeteoSchema);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Extrait_le_json_d_un_bloc_markdown()
    {
        // Cas LLM typique : le modèle encadre sa sortie de ```json ... ```
        var sortie = "Voici la réponse:\n```json\n{\"ville\":\"Lyon\",\"temperature\":22.1}\n```";

        var result = SchemaValidator.Validate(sortie, MeteoSchema);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Signale_un_champ_requis_manquant()
    {
        var result = SchemaValidator.Validate("""{"ville":"Nice"}""", MeteoSchema);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Message.Contains("temperature"));
    }

    [Fact]
    public void Signale_un_mauvais_type_avec_le_chemin_precis()
    {
        var result = SchemaValidator.Validate(
            """{"ville":"Nice","temperature":"vingt degrés"}""", MeteoSchema);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Path == "/temperature");
    }

    [Fact]
    public void Echoue_proprement_sans_json_dans_la_sortie()
    {
        var result = SchemaValidator.Validate("Il fait 15 degrés à Paris aujourd'hui.", MeteoSchema);

        Assert.False(result.IsValid);
        Assert.NotNull(result.ParseError);
    }

    [Fact]
    public void Echoue_proprement_sur_un_json_malforme()
    {
        var result = SchemaValidator.Validate("""{"ville":"Paris", "temperature":}""", MeteoSchema);

        Assert.False(result.IsValid);
        Assert.NotNull(result.ParseError);
    }
}
