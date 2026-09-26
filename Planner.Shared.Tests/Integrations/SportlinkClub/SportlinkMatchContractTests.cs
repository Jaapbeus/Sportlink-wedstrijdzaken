using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using System.Text.Json;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

public class SportlinkMatchContractTests
{
    private const string GeldigeMatchJson = """
        {
            "publicMatchId": "M000000001",
            "externalMatchId": 123456,
            "matchDate": { "Date": "2026-09-15", "StartTime": "19:30" },
            "matchStatus": "SCHEDULED",
            "isHomeMatch": true,
            "isCanceledMatch": false,
            "isConceptMatch": false,
            "isEditFieldAllowed": true,
            "isAssignDressingRoomsAllowed": true,
            "isAssignOfficialsAllowed": true,
            "isEditFieldSidePanelAllowed": true,
            "isAddScoreAllowed": true,
            "matchField": { "facilityId": "BBCF989", "name": "Sportpark" },
            "field": { "fieldId": "BBCF989-OUTDOOR_FIELD-6", "fieldSize": 1.0, "fieldOffset": 0 },
            "matchOfficials": [ { "name": "geheim" } ]
        }
        """;

    [Fact]
    public void ControleerVorm_GeldigeRespons_GeeftLegeLijst()
    {
        var afwijkend = SportlinkMatchContract.ControleerVorm(GeldigeMatchJson);

        afwijkend.Should().BeEmpty();
    }

    [Fact]
    public void ControleerVorm_ExternalMatchIdAlsString_WordtOokGeaccepteerd()
    {
        // Sportlink heeft live wisselvallig gedrag laten zien (string vs. getal) — de contractcheck
        // moet beide toestaan, net als FlexibleStringJsonConverter dat voor SportlinkMatch doet.
        var json = GeldigeMatchJson.Replace("\"externalMatchId\": 123456", "\"externalMatchId\": \"123456\"");

        var afwijkend = SportlinkMatchContract.ControleerVorm(json);

        afwijkend.Should().BeEmpty();
    }

    [Fact]
    public void ControleerVorm_OntbrekendVeld_WordtGerapporteerdOpNaam()
    {
        var zonderMatchStatus = System.Text.RegularExpressions.Regex.Replace(
            GeldigeMatchJson, ",?\\s*\"matchStatus\"\\s*:\\s*\"SCHEDULED\"", "");

        var afwijkend = SportlinkMatchContract.ControleerVorm(zonderMatchStatus);

        afwijkend.Should().Contain("matchStatus");
    }

    [Fact]
    public void ControleerVorm_VeldMetOnverwachtType_WordtGerapporteerdOpNaam()
    {
        // isHomeMatch als string in plaats van boolean — een contractbreuk die System.Text.Json bij
        // een gewone deserialisatie soms stilzwijgend zou kunnen negeren/falen zonder duidelijke naam.
        var json = GeldigeMatchJson.Replace("\"isHomeMatch\": true", "\"isHomeMatch\": \"true\"");

        var afwijkend = SportlinkMatchContract.ControleerVorm(json);

        afwijkend.Should().Contain("isHomeMatch");
    }

    [Fact]
    public void ControleerVorm_NooitAfhankelijkVanMatchOfficialsInhoud()
    {
        // matchOfficials bevat persoonsgegevens — de contractcheck mag dit veld nooit inspecteren,
        // dus een ontbrekend/afwijkend matchOfficials-veld mag nooit in het resultaat verschijnen.
        var zonderOfficials = GeldigeMatchJson.Replace(
            ", \"matchOfficials\": [ { \"name\": \"geheim\" } ]", "");

        var afwijkend = SportlinkMatchContract.ControleerVorm(zonderOfficials);

        afwijkend.Should().BeEmpty();
        afwijkend.Should().NotContain(v => v.Contains("matchOfficials", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ControleerVorm_FieldOntbreekt_WordtGerapporteerdOpNaam()
    {
        // #1339: "field" (huidige veld-prefill) is een verplicht rootveld, net als matchField.
        var zonderField = System.Text.RegularExpressions.Regex.Replace(
            GeldigeMatchJson,
            ",?\\s*\"field\"\\s*:\\s*\\{[^}]*\\}", "");

        var afwijkend = SportlinkMatchContract.ControleerVorm(zonderField);

        afwijkend.Should().Contain("field");
    }

    [Fact]
    public void ControleerVorm_FieldMagNullZijn()
    {
        // Een wedstrijd zonder toegewezen veld heeft vermoedelijk field: null — geen contractbreuk.
        var metNullField = System.Text.RegularExpressions.Regex.Replace(
            GeldigeMatchJson,
            "\"field\"\\s*:\\s*\\{[^}]*\\}", "\"field\": null");

        var afwijkend = SportlinkMatchContract.ControleerVorm(metNullField);

        afwijkend.Should().BeEmpty();
    }

    [Fact]
    public void ControleerVorm_OngeldigeJson_GooitJsonException()
    {
        var act = () => SportlinkMatchContract.ControleerVorm("dit is geen json");

        act.Should().Throw<JsonException>();
    }
}
