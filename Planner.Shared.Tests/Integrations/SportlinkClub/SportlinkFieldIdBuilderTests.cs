using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using System.Text.Json;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

public class SportlinkFieldIdBuilderTests
{
    [Fact]
    public void BouwVoorstelFieldId_MetFacilityId_GeeftVerwachteVorm()
    {
        // Zelfde vorm als de vaste testwedstrijd (wedstrijdnummer 69, veld 6): "BBCF989-OUTDOOR_FIELD-6".
        var fieldId = SportlinkFieldIdBuilder.BouwVoorstelFieldId("BBCF989", 6);

        fieldId.Should().Be("BBCF989-OUTDOOR_FIELD-6");
    }

    [Fact]
    public void BouwVoorstelFieldId_ZonderFacilityId_GeeftNull()
    {
        var fieldId = SportlinkFieldIdBuilder.BouwVoorstelFieldId(null, 1);

        fieldId.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "1.0")]
    [InlineData("A", "0.5")]
    [InlineData("B", "0.5")]
    [InlineData("A1", "0.25")]
    [InlineData("B2", "0.25")]
    [InlineData("ONBEKEND", "1.0")]
    public void BouwVoorstelFieldSize_GeeftVerwachteFractieAlsString(string? subpositie, string verwacht)
    {
        var fieldSize = SportlinkFieldIdBuilder.BouwVoorstelFieldSize(subpositie);

        fieldSize.Should().Be(verwacht);
    }

    [Fact]
    public void BekendeSubposities_BevatHeelVeldEnAlleSubposities()
    {
        SportlinkFieldIdBuilder.BekendeSubposities.Should().Equal(
            (string?)null, "A", "B", "A1", "A2", "B1", "B2");
    }

    [Fact]
    public void BouwPaneelResponse_VoegtVeldEnSubpositieOptiesToeZonderRauweVeldenTeVerliezen()
    {
        // #1339: de node-merge mag geen bestaand SportlinkMatch-veld verliezen (bijv. bij een
        // toekomstige uitbreiding van SportlinkMatch), en moet de twee nieuwe lijsten toevoegen —
        // camelCase, want SportlinkMatch's [JsonPropertyName]-attributen zijn leidend, niet de
        // PascalCase C#-eigenschapsnamen.
        var match = new SportlinkMatch
        {
            PublicMatchId = "M000000001",
            MatchField = new SportlinkMatchField { FacilityId = "BBCF989", Name = "Sportpark" },
            Field = new SportlinkMatchFieldSnapshot { FieldId = "BBCF989-OUTDOOR_FIELD-6", FieldSize = "1.0" }
        };
        var velden = new List<(int VeldNummer, string VeldNaam)> { (6, "Veld 6"), (1, "Veld 1") };

        var payload = SportlinkFieldIdBuilder.BouwPaneelResponse(match, velden);

        payload["publicMatchId"]!.GetValue<string>().Should().Be("M000000001");
        payload["fieldId"]!.GetValue<string>().Should().Be("BBCF989-OUTDOOR_FIELD-6");
        var veldOpties = payload["veldOpties"].Deserialize<List<SportlinkVeldOptie>>()!;
        veldOpties.Should().ContainSingle(v => v.VeldNummer == 6 && v.VoorstelFieldId == "BBCF989-OUTDOOR_FIELD-6");
        veldOpties.Should().ContainSingle(v => v.VeldNummer == 1 && v.VoorstelFieldId == "BBCF989-OUTDOOR_FIELD-1");
        var subpositieOpties = payload["subpositieOpties"].Deserialize<List<SportlinkSubpositieOptie>>()!;
        subpositieOpties.Should().ContainSingle(s => s.Subpositie == "A1" && s.VoorstelFieldSize == "0.25");
    }
}
