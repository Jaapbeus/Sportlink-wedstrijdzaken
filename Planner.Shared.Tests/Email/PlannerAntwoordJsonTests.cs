using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>Golden-tests (#1628): de JSON-contracten van <see cref="PlannerAntwoordJson"/>, veldnamen en volgorde.</summary>
public class PlannerAntwoordJsonTests
{
    private sealed record Wedstrijd(string Datum, long Wedstrijdcode);

    [Fact]
    public void DatumOnbekend_IsContract() => PlannerAntwoordJson.DatumOnbekend().Should().Be("{\"datumOnbekend\":true}");

    [Fact]
    public void WedstrijdAlIngepland_BevatWedstrijd()
        => PlannerAntwoordJson.WedstrijdAlIngepland(new Wedstrijd("2026-06-06", 1))
            .Should().Be("{\"wedstrijdAlIngepland\":true,\"wedstrijd\":{\"Datum\":\"2026-06-06\",\"Wedstrijdcode\":1}}");

    [Fact]
    public void TeamOnbekend_BevatTegenstander()
        => PlannerAntwoordJson.TeamOnbekend("FC Test").Should().Be("{\"teamOnbekend\":true,\"tegenstander\":\"FC Test\"}");

    [Fact]
    public void MultiDatum_GeeftEenResultaatPerDatumInVolgorde()
        => PlannerAntwoordJson.MultiDatum(new (string, object?)[] { ("2026-06-06", new { ok = true }), ("2026-06-07", null) })
            .Should().Be("{\"multiDatum\":true,\"resultaten\":[{\"datum\":\"2026-06-06\",\"response\":{\"ok\":true}},{\"datum\":\"2026-06-07\",\"response\":null}]}");

    [Fact]
    public void OnvoldoendeHerplanGegevens_IsContract()
        => PlannerAntwoordJson.OnvoldoendeHerplanGegevens()
            .Should().Be("{\"error\":\"Onvoldoende gegevens voor herplanverzoek (team en datum nodig)\"}");

    [Fact]
    public void HerplanWedstrijdNietGevonden_NoemtTeamEnDatum()
        => PlannerAntwoordJson.HerplanWedstrijdNietGevonden("JO13-1", "2026-06-06")
            .Should().Be("{\"gevonden\":false,\"reden\":\"Geen wedstrijd gevonden voor JO13-1 op 2026-06-06\"}");

    [Fact]
    public void HerplanTeLaat_BinnenDeadline_GeeftAntwoordMetDagen()
    {
        var datum = DateOnly.FromDateTime(DateTime.Today).AddDays(2).ToString("yyyy-MM-dd");

        var json = PlannerAntwoordJson.HerplanTeLaat(new Wedstrijd(datum, 7), datum, 8);

        json.Should().Be($"{{\"herplanTeLaat\":true,\"wedstrijd\":{{\"Datum\":\"{datum}\",\"Wedstrijdcode\":7}},\"deadlineDagen\":8,\"dagenTotWedstrijd\":2}}");
    }

    [Fact]
    public void HerplanTeLaat_BuitenDeadlineOfOnparseerbaar_GeeftNull()
    {
        var ver = DateOnly.FromDateTime(DateTime.Today).AddDays(30).ToString("yyyy-MM-dd");

        PlannerAntwoordJson.HerplanTeLaat(new Wedstrijd(ver, 1), ver, 8).Should().BeNull();
        PlannerAntwoordJson.HerplanTeLaat(new Wedstrijd("x", 1), "x", 8).Should().BeNull();
        PlannerAntwoordJson.HerplanTeLaat(new Wedstrijd("", 1), null, 8).Should().BeNull();
    }

    [Fact]
    public void HerplanMetGewensteDatumEnOpties_ZijnContract()
    {
        var w = new Wedstrijd("2026-06-06", 1);

        PlannerAntwoordJson.HerplanMetGewensteDatum(w, "2026-06-13", new { vrij = true })
            .Should().Be("{\"wedstrijd\":{\"Datum\":\"2026-06-06\",\"Wedstrijdcode\":1},\"gewensteDatum\":\"2026-06-13\",\"beschikbaarheid\":{\"vrij\":true}}");
        PlannerAntwoordJson.HerplanOpties(w, new { n = 2 })
            .Should().Be("{\"wedstrijd\":{\"Datum\":\"2026-06-06\",\"Wedstrijdcode\":1},\"herplanOpties\":{\"n\":2}}");
    }

    [Fact]
    public void TeamContact_MetEnZonderTeam()
    {
        PlannerAntwoordJson.TeamContact(null, false).Should().Be("{\"teamContactOpgevraagd\":true,\"teamNaam\":null,\"coachGevonden\":false}");
        PlannerAntwoordJson.TeamContact("JO13-1", true).Should().Be("{\"teamContactOpgevraagd\":true,\"teamNaam\":\"JO13-1\",\"coachGevonden\":true}");
    }
}
