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

    private static SportlinkVeldplannerBlok Blok(string label, string start, int duur = 75)
        => new(label, "veld 1", start, 1m, duur, null);

    [Fact]
    public void Koppel_ZelfdeLabel_ZonderLeestekensEnHoofdletters()
    {
        var res = SportlinkVeldplannerKoppeling.Koppel(
            new[] { ("Thuis 5 - Voorbeeld '46 8", (string?)"14:30") }, new[] { Blok("Thuis 5 - Voorbeeld 46 8", "14:30") });

        res.Should().ContainKey(0);
    }

    [Fact]
    public void Koppel_VerouderdeStarttijd_KoppeltToch()
        => SportlinkVeldplannerKoppeling.Koppel(
                new[] { ("JO13-2 - JO13-2", (string?)"10:15") }, new[] { Blok("JO13-2 - JO13-2", "09:00") })
            .Should().ContainKey(0, "Sportlink is leidend, ook als onze starttijd achterloopt");

    [Fact]
    public void Koppel_TweeBlokkenZelfdeLabel_DichtstbijzijndeStarttijdEnElkBlokEenmaal()
    {
        var blokken = new[] { Blok("A - B", "09:00"), Blok("A - B", "13:00") };
        var res = SportlinkVeldplannerKoppeling.Koppel(
            new[] { ("A - B", (string?)"13:10"), ("A - B", (string?)"09:05") }, blokken);

        res[0].StartTijd.Should().Be("13:00");
        res[1].StartTijd.Should().Be("09:00");
    }

    [Fact]
    public void Koppel_PloegnaamMetVoorvoegsel_KoppeltInRondeTwee()
        => SportlinkVeldplannerKoppeling.Koppel(
                new[] { ("Thuis 35+2 - v.v. Uit 35+2", (string?)"21:00") },
                new[] { Blok("Thuis 35+2 - Uit 35+2", "21:00", 30) })
            .Should().ContainKey(0);

    [Fact]
    public void Koppel_AndereWedstrijd_KoppeltNiet()
        => SportlinkVeldplannerKoppeling.Koppel(
                new[] { ("Thuis 1 - Uit", (string?)"14:00") }, new[] { Blok("Thuis 2 - Uit", "14:00") })
            .Should().BeEmpty("een wedstrijd die Sportlink niet kent behoudt de eigen berekening");

    [Fact]
    public void Koppel_RondeTweeNeemtGeenBlokDatRondeEenAlHeeft()
    {
        var blokken = new[] { Blok("Thuis 1 - Uit", "14:00") };
        var res = SportlinkVeldplannerKoppeling.Koppel(
            new[] { ("Thuis 1 - v.v. Uit", (string?)"14:00"), ("Thuis 1 - Uit", (string?)"14:00") }, blokken);

        res.Should().ContainSingle().Which.Key.Should().Be(1, "het exacte label gaat voor");
    }
}
