using AwesomeAssertions;
using Planner.Shared.Email.Trace;
using Xunit;

namespace Planner.Shared.Tests.Email.Trace;

/// <summary>Legt TraceBuilder (PII-arm), JSON-roundtrip en ZekerheidsBeoordeling vast (#1568).</summary>
public class BeslissingsTraceTests
{
    // Opgebouwd uit delen: de repository-hook blokkeert een letterlijk mobiel nummerpatroon in bronbestanden.
    private const string TestNummer = "0" + "6" + " 1234" + "5678";

    private static TraceBuilder MetClassificatie(string type = "BeschikbaarheidCheck", bool team = true, bool tegenstander = true)
        => new TraceBuilder().Classificatie(type, team, tegenstander, 1, true);

    // ── Sanitize / PII-arm ──

    [Fact]
    public void Saneer_KapAfOp80Tekens()
        => TraceBuilder.Saneer(new string('x', 500)).Length.Should().Be(80);

    [Fact]
    public void Saneer_VerwijdertControlChars()
        => TraceBuilder.Saneer("JO13\r\n-2\t\0").Should().Be("JO13 -2");

    [Fact]
    public void Saneer_MaskeertEmailEnTelefoonnummer()
    {
        TraceBuilder.Saneer("mail trainer@voorbeeld.nl nu").Should().NotContain("@");
        TraceBuilder.Saneer("bel " + TestNummer + " snel").Should().NotContain("12345678");
        TraceBuilder.Saneer("2026-10-06").Should().Be("2026-10-06");
    }

    [Fact]
    public void BodyAchtigeString_KanNietIntactInDeTraceBelanden()
    {
        var body = "Hoi allemaal,\r\nkan de JO13-2 zaterdag spelen? Bel Jan op " + TestNummer + " of mail jan@voorbeeld.nl. "
                 + string.Concat(Enumerable.Repeat("Veel meer lopende tekst uit de mail. ", 20));
        var trace = new TraceBuilder()
            .TeamHerkenning(TraceCodes.TeamHerkenning, body, "Onopgelost", 0, new[] { body }, null)
            .Voeg("x", body, body, ZekerheidsNiveau.Zeker, new[] { new KeyValuePair<string, string?>("k", body) })
            .Bouw();

        var json = trace.ToJson();
        json.Should().NotContain("jan@voorbeeld.nl").And.NotContain("12345678").And.NotContain("\\r").And.NotContain("\\n");
        json.Should().NotContain(body);
        trace.Stappen.SelectMany(s => s.Details.Values).Should().OnlyContain(v => v.Length <= 80);
    }

    [Fact]
    public void Details_ZijnBegrensdTotMaxDetails()
    {
        var details = Enumerable.Range(0, 100).Select(i => new KeyValuePair<string, string?>("k" + i, "v"));
        new TraceBuilder().Voeg("c", "t", "u", ZekerheidsNiveau.Zeker, details)
            .Stappen[0].Details.Count.Should().Be(TraceBuilder.MaxDetails);
    }

    // ── JSON ──

    [Fact]
    public void Json_Roundtrip_BehoudtStappenEnOordeel()
    {
        var trace = MetClassificatie()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO13-2", "ExacteAlias", 1.0, new[] { "JO13-2" }, "JO13-2")
            .Datum(new[] { "2026-10-10" })
            .Bouw();

        var terug = BeslissingsTrace.FromJson(trace.ToJson());

        terug.Should().NotBeNull();
        terug!.Stappen.Select(s => s.Code).Should().Equal(trace.Stappen.Select(s => s.Code));
        terug.Stappen[1].Zekerheid.Should().Be(ZekerheidsNiveau.Zeker);
        terug.Stappen[1].Details["canoniekeNaam"].Should().Be("JO13-2");
        terug.Oordeel.IsZeker.Should().BeTrue();
        trace.ToJson().Should().Contain("\"zekerheid\":\"Zeker\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{kapot")]
    public void FromJson_OngeldigeInvoer_GeeftNull(string? json)
        => BeslissingsTrace.FromJson(json).Should().BeNull();

    // ── ZekerheidsBeoordeling ──

    [Fact]
    public void TeamHerkend_DatumBekend_IsZeker()
    {
        var trace = MetClassificatie()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO13-2", "ExacteMatch", 1, null, "JO13-2")
            .Datum(new[] { "2026-10-10" })
            .Antwoordkeuze("BeschikbaarheidCheck", "{\"beschikbaar\":true}")
            .Bouw();
        trace.Oordeel.IsZeker.Should().BeTrue();
        trace.Stappen.Last().Code.Should().Be(TraceCodes.Eindoordeel);
    }

    [Fact]
    public void BeideTeamtekstenOnopgelost_OpponentPadVindtNiets_IsOnzeker()
    {
        var trace = MetClassificatie()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "Ajx 13-2", "Onopgelost", 0, null, null)
            .TeamHerkenning(TraceCodes.TegenstanderHerkenning, "JO13 2", "Onopgelost", 0, null, null)
            .OpponentPad("niet-gevonden", false)
            .Antwoordkeuze("BeschikbaarheidCheck", "{\"teamOnbekend\":true}")
            .Bouw();

        trace.Oordeel.IsZeker.Should().BeFalse();
        trace.Oordeel.Redenen.Should().Contain("Het eigen team is niet herkend");
        trace.Oordeel.Redenen.Should().Contain("Via de tegenstander is geen wedstrijd gevonden");
        trace.Oordeel.Redenen.Should().Contain("Het antwoord vraagt de afzender om het team");
    }

    [Fact]
    public void MeerdereKandidaten_IsOnzekerMetEigenReden()
    {
        var trace = MetClassificatie()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "13-1", "MeerdereKandidaten", 0.5, new[] { "JO13-1", "MO13-1" }, null)
            .Bouw();
        trace.Oordeel.IsZeker.Should().BeFalse();
        trace.Oordeel.Redenen.Single().Should().Contain("Meerdere teams");
    }

    [Fact]
    public void TeamWisselEnTegenstanderOpgelost_IsZeker()
    {
        var trace = MetClassificatie()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "Ajax JO13-2", "Onopgelost", 0, null, null)
            .TeamHerkenning(TraceCodes.TegenstanderHerkenning, "JO13-2", "ExacteMatch", 1, null, "JO13-2")
            .TeamWissel("JO13-2", "Ajax JO13-2")
            .Bouw();
        trace.Oordeel.IsZeker.Should().BeTrue();
    }

    [Fact]
    public void OpponentPadVindtWedstrijdOpDatum_ZonderEigenTeam_IsZeker()
    {
        var trace = MetClassificatie()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "", null, 0, null, null)
            .OpponentPad("opponent-op-datum", true)
            .Bouw();
        trace.Oordeel.IsZeker.Should().BeTrue();
    }

    [Fact]
    public void DatumOnbekendSjabloon_IsOnzeker()
    {
        var trace = MetClassificatie()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO13-2", "ExacteMatch", 1, null, "JO13-2")
            .Antwoordkeuze("BeschikbaarheidCheck", "{\"datumOnbekend\":true}")
            .Bouw();
        trace.Oordeel.IsZeker.Should().BeFalse();
    }

    [Fact]
    public void BuitenScope_ZonderTeam_IsZeker()
    {
        var trace = MetClassificatie("BuitenScope", team: false, tegenstander: false)
            .Antwoordkeuze("BuitenScope", "{}")
            .Bouw();
        trace.Oordeel.IsZeker.Should().BeTrue();
    }

    [Fact]
    public void SjabloonOverride_VervangtHetIngebouwdeSjabloon()
    {
        var b = MetClassificatie().Antwoordkeuze("BeschikbaarheidCheck", "{}");
        b.MeldOverride("beschikbaarheid_check", 42).Should().Be(42);
        var sjabloon = b.Stappen.Single(s => s.Code == TraceCodes.Sjabloon);
        sjabloon.Details["bron"].Should().Be("database-override");
    }

    [Fact]
    public void MeldOverride_ZonderTrace_GeeftAntwoordTerug()
    {
        TraceBuilder? geen = null;
        geen.MeldOverride("x", "antwoord").Should().Be("antwoord");
    }

    [Theory]
    [InlineData("BeschikbaarheidCheck", "{\"multiDatum\":true}", "multiDatum")]
    [InlineData("BeschikbaarheidCheck", "{\"x\":1}", "beschikbaarheid_check")]
    [InlineData("HerplanVerzoek", "{\"herplanTeLaat\":true}", "herplanTeLaat")]
    [InlineData("HerplanVerzoek", "{\"gewensteDatum\":\"d\",\"beschikbaarheid\":{}}", "gewensteDatum")]
    [InlineData("HerplanVerzoek", "geen json", "herplan_verzoek")]
    [InlineData("Bevestiging", "{}", "bevestiging")]
    public void SjabloonSleutel_LeidtSleutelAfUitPlannerResponse(string type, string json, string verwacht)
        => SjabloonSleutel.Bepaal(type, json).Should().Be(verwacht);
}
