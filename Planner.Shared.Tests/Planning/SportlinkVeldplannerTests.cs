using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;
using Xunit;

namespace Planner.Shared.Tests.Planning;

public class SportlinkVeldplannerTests
{
    // Fictieve data in de vorm van de echte respons (#1563): geen echte teams, ID's of locaties.
    private const string Json = """
    { "ScheduledMatches": [
      { "Duration": 60, "Interval": 15, "StartUpInterval": 0, "FollowUpInterval": 0, "StartTimeAsString": "09:00", "PublicMatchId": "M1",
        "Field": { "FieldName": "veld 2", "FieldSize": 0.5, "FieldOffsetDescription": "B" },
        "Teams": { "Home": { "TeamName": "Thuis O11-1" }, "Away": { "TeamName": "Uit O11-1" } } },
      { "Duration": 90, "Interval": 15, "StartUpInterval": 0, "FollowUpInterval": 5, "StartTimeAsString": "14:00", "PublicMatchId": "M2",
        "Field": { "FieldName": "veld 1", "FieldSize": 1, "FieldOffsetDescription": "A" },
        "Teams": { "Home": { "TeamName": "Thuis 1" }, "Away": { "TeamName": "Uit 1" } } },
      { "Duration": 20, "Interval": 10, "StartUpInterval": 5, "FollowUpInterval": 0, "StartTimeAsString": "19:35", "PublicMatchId": "M3",
        "Field": { "FieldName": "veld 3", "FieldSize": 0.25, "FieldOffsetDescription": "A1" },
        "Teams": { "Home": { "TeamName": "Thuis 35+1" }, "Away": { "TeamName": "Uit 35+1" } } },
      { "StartTimeAsString": "10:00", "Field": { "FieldName": "veld 4", "FieldSize": 1 },
        "Teams": { "Home": { "TeamName": "A" }, "Away": { "TeamName": "B" } } },
      { "StartTimeAsString": "11:00", "Teams": { "Home": { "TeamName": "Zonder" }, "Away": { "TeamName": "Veld" } } }
    ], "UnscheduledMatches": [], "OtherActivities": [] }
    """;

    [Fact]
    public void Parse_NeemtVeldStartEnVolledigeBezettingOverUitSportlink()
    {
        var blokken = SportlinkVeldplannerParser.Parse(Json);

        blokken.Should().HaveCount(4, "een item zonder veld wordt overgeslagen");
        blokken[0].Should().Be(new SportlinkVeldplannerBlok("Thuis O11-1 - Uit O11-1", "veld 2 B", "09:00", 0.5m, 75, "M1"));
    }

    [Fact]
    public void Parse_VolVeld_ZonderDeelaanduidingEnMetUitloop()
    {
        var blok = SportlinkVeldplannerParser.Parse(Json)[1];

        blok.Veld.Should().Be("veld 1", "bij een heel veld hoort geen A/B-aanduiding");
        blok.DuurMinuten.Should().Be(110, "90 speelduur + 15 pauze + 5 uitloop");
    }

    [Fact]
    public void Parse_Kwartveld_PauzeIsTienEnInloopVerschuiftDeStart()
    {
        var blok = SportlinkVeldplannerParser.Parse(Json)[2];

        blok.Veld.Should().Be("veld 3 A1");
        blok.Veldafmeting.Should().Be(0.25m);
        blok.StartTijd.Should().Be("19:30", "het inloopdeel hoort bij het blok");
        blok.DuurMinuten.Should().Be(35, "5 inloop + 20 speelduur + 10 pauze");
    }

    [Fact]
    public void Parse_OntbrekendeDuur_GebruiktSportlinksEigenStandaard()
        => SportlinkVeldplannerParser.Parse(Json)[3].DuurMinuten.Should().Be(SportlinkVeldplannerParser.StandaardDuur);

    [Fact]
    public void Parse_ZonderScheduledMatches_GeeftLegeLijst()
        => SportlinkVeldplannerParser.Parse("{}").Should().BeEmpty();

    [Fact]
    public void Parse_OngeldigeJson_GooitJsonException()
    {
        var act = () => SportlinkVeldplannerParser.Parse("geen json");
        act.Should().Throw<System.Text.Json.JsonException>();
    }

    private static SportlinkVeldplannerBlok Blok(string? publicId, long? externId = null, string label = "Thuis - Uit", string start = "09:00")
        => new(label, "veld 1", start, 1m, 75, publicId, externId);

    private static EigenSleutel Eigen(string? publicId = null, long? nummer = null) => new(publicId, nummer);

    [Fact]
    public void Koppel_OpPublicMatchId_OngeachtDeNaamEnDeStarttijd()
        => SportlinkVeldplannerKoppeling.Koppel(
                new[] { Eigen("M1") }, new[] { Blok("M1", label: "Heel Andere Naam - Nog Anders", start: "17:00") })
            .Should().ContainKey(0, "de koppeling kijkt nooit naar naam of tijd (#1915)");

    [Fact]
    public void Koppel_ZelfdeNaamMaarAnderPublicMatchId_KoppeltNiet()
        => SportlinkVeldplannerKoppeling.Koppel(
                new[] { Eigen("M1") }, new[] { Blok("M2", label: "Thuis - Uit") })
            .Should().BeEmpty("een naam is geen identiteit");

    [Fact]
    public void Koppel_ZonderEnigeSleutel_KoppeltNooit()
        => SportlinkVeldplannerKoppeling.Koppel(
                new[] { Eigen() }, new[] { Blok(null, label: "Thuis - Uit") })
            .Should().BeEmpty();

    [Fact]
    public void Koppel_ElkBlokHooguitEenmaal()
    {
        var res = SportlinkVeldplannerKoppeling.Koppel(new[] { Eigen("M1"), Eigen("M1") }, new[] { Blok("M1") });

        res.Should().ContainSingle().Which.Key.Should().Be(0);
    }

    [Fact]
    public void Koppel_RondeTwee_Wedstrijdnummer_AlsPublicMatchIdOntbreekt()
        => SportlinkVeldplannerKoppeling.Koppel(
                new[] { Eigen(null, 101066) }, new[] { Blok("M9", externId: 101066) })
            .Should().ContainKey(0, "wedstrijdnummer = ExternalMatchId");

    [Fact]
    public void Koppel_RondeTwee_NummerDubbelBijEigenRegels_KoppeltNiet()
        => SportlinkVeldplannerKoppeling.Koppel(
                new[] { Eigen(null, 1), Eigen(null, 1) }, new[] { Blok("M9", externId: 1) })
            .Should().BeEmpty("een clubwedstrijd heeft vaak nummer 1; dat is niet eenduidig");

    [Fact]
    public void Koppel_RondeTwee_NummerDubbelBijBlokken_KoppeltNiet()
        => SportlinkVeldplannerKoppeling.Koppel(
                new[] { Eigen(null, 7) }, new[] { Blok("M8", externId: 7), Blok("M9", externId: 7) })
            .Should().BeEmpty();

    [Fact]
    public void Koppel_RondeTwee_NeemtGeenBlokDatRondeEenAlHeeft()
    {
        var res = SportlinkVeldplannerKoppeling.Koppel(
            new[] { Eigen(null, 5), Eigen("M1", 5) }, new[] { Blok("M1", externId: 5) });

        res.Should().ContainSingle().Which.Key.Should().Be(1, "het PublicMatchId gaat voor");
    }

    [Fact]
    public void Koppel_OnbekendNummerBijBlok_KoppeltNiet()
        => SportlinkVeldplannerKoppeling.Koppel(new[] { Eigen(null, 5) }, new[] { Blok("M1", externId: null) }).Should().BeEmpty();

    [Fact]
    public void Koppel_TweeWedstrijdenMetEigenSleutel_KoppelenElkAanHunEigenBlok()
    {
        var res = SportlinkVeldplannerKoppeling.Koppel(
            new[] { Eigen("M2"), Eigen("M1") }, new[] { Blok("M1", start: "09:00"), Blok("M2", start: "13:00") });

        res[0].StartTijd.Should().Be("13:00");
        res[1].StartTijd.Should().Be("09:00");
    }
}
