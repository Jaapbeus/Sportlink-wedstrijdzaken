using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>
/// De voorwaarden vóór een beschikbaarheidscheck uit een bericht (#1861): eerst een herkend eigen team, dan een
/// bekende speelduur. Aanleiding: een vraag zonder team kreeg het team van een oude wedstrijd tegen dezelfde
/// tegenstander, en daarna "geen veld beschikbaar" met de beheerinstructie van de planner.
/// </summary>
public class BeschikbaarheidVoorwaardenTests
{
    private const string Datum = "2027-02-13";

    private sealed record Wedstrijd(string Naam);

    private static BerichtClassificatie Vraag(string? tegenstander, params string[] datums) => new()
    {
        Type = VerzoekType.BeschikbaarheidCheck,
        Tegenstander = tegenstander,
        Datums = datums.ToList(),
    };

    // ── Zonder herkend eigen team ──

    [Fact]
    public async Task ZonderEigenTeam_TegenstanderZonderWedstrijdOpDatum_VraagtOmHetTeam()
    {
        // Precies de aanleiding: alleen een wedstrijd op een andere datum zou bestaan; die wordt niet meer gezocht.
        var opzoekingen = new List<(string, DateOnly)>();
        var trace = new TraceBuilder();

        var json = await BeschikbaarheidVoorwaarden.ZonderEigenTeamAsync(Vraag("Tegenstander", Datum), [Datum],
            (t, d) => { opzoekingen.Add((t, d)); return Task.FromResult<Wedstrijd?>(null); }, trace);

        var r = JObject.Parse(json);
        r["teamOnbekend"]!.Value<bool>().Should().BeTrue();
        r["tegenstander"]!.ToString().Should().Be("Tegenstander");
        opzoekingen.Should().Equal(("Tegenstander", new DateOnly(2027, 2, 13)));
        var pad = trace.Stappen.Single(s => s.Code == TraceCodes.OpponentPad);
        pad.Details["tak"].Should().Be("niet-gevonden");
        trace.Stappen.Should().NotContain(s => s.Code == TraceCodes.OpponentTeamHerkenning);
    }

    [Fact]
    public async Task ZonderEigenTeam_WedstrijdTegenTegenstanderOpDeGevraagdeDatum_GeeftAlIngepland()
    {
        var trace = new TraceBuilder();

        var json = await BeschikbaarheidVoorwaarden.ZonderEigenTeamAsync(Vraag("Tegenstander", Datum), [Datum],
            (_, _) => Task.FromResult<Wedstrijd?>(new Wedstrijd("JO13-1 - Tegenstander")), trace);

        var r = JObject.Parse(json);
        r["wedstrijdAlIngepland"]!.Value<bool>().Should().BeTrue();
        r["wedstrijd"]!["Naam"]!.ToString().Should().Be("JO13-1 - Tegenstander");
        trace.Stappen.Single(s => s.Code == TraceCodes.OpponentPad).Details["tak"].Should().Be("opponent-op-datum");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task ZonderEigenTeam_GeenTegenstander_VraagtOmHetTeamZonderOpzoeking(string? tegenstander)
    {
        var opgezocht = false;
        var trace = new TraceBuilder();

        var json = await BeschikbaarheidVoorwaarden.ZonderEigenTeamAsync(Vraag(tegenstander, Datum), [Datum],
            (_, _) => { opgezocht = true; return Task.FromResult<Wedstrijd?>(null); }, trace);

        JObject.Parse(json)["teamOnbekend"]!.Value<bool>().Should().BeTrue();
        opgezocht.Should().BeFalse();
        trace.Stappen.Should().BeEmpty();
    }

    [Theory]
    [InlineData("2027-02-13|2027-02-20")]
    [InlineData("")]
    [InlineData("13 februari")]
    public async Task ZonderEigenTeam_GeenEnkeleStrikteDatum_VraagtOmHetTeamZonderOpzoeking(string datumLijst)
    {
        var datums = datumLijst.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var opgezocht = false;

        var json = await BeschikbaarheidVoorwaarden.ZonderEigenTeamAsync(Vraag("Tegenstander", datums), datums,
            (_, _) => { opgezocht = true; return Task.FromResult<Wedstrijd?>(null); }, null);

        JObject.Parse(json)["teamOnbekend"]!.Value<bool>().Should().BeTrue();
        opgezocht.Should().BeFalse();
    }

    // ── Speelduur ──

    private static Func<string, Task<Speeltijd?>> Speeltijden(params (string Sleutel, int Duur)[] rijen)
        => sleutel => Task.FromResult(rijen
            .Where(r => string.Equals(r.Sleutel, sleutel, StringComparison.OrdinalIgnoreCase))
            .Select(r => new Speeltijd { Leeftijd = r.Sleutel, WedstrijdTotaal = r.Duur, Veldafmeting = 1.00m })
            .FirstOrDefault());

    [Theory]
    [InlineData("JO23", "JO23")]
    [InlineData("jo23", "JO23")]
    [InlineData("O23", "JO23")]
    [InlineData("Onder 23", "JO23")]
    [InlineData("MO23", "MO23")]
    [InlineData("Senioren", "1-99")]
    public async Task Speelduur_BekendeCategorie_GaatDoorMetDeSleutelUitSpeeltijden(string categorie, string sleutel)
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, LeeftijdsCategorie = categorie };
        var trace = new TraceBuilder();

        var uitkomst = await BeschikbaarheidVoorwaarden.ControleerSpeelduurAsync(classificatie,
            Speeltijden(("JO23", 105), ("MO23", 105), ("1-99", 115)), trace);

        uitkomst.Should().BeNull();
        classificatie.LeeftijdsCategorie.Should().Be(sleutel);
        classificatie.TeamHandmatigBeoordelen.Should().BeFalse();
        trace.Stappen.Should().BeEmpty();
    }

    [Fact]
    public async Task Speelduur_ExacteSleutelWintVanNormalisatie()
    {
        // Een club met een eigen sleutel (bijv. "35+") mag niet via de normalisatie op een andere rij uitkomen.
        var classificatie = new BerichtClassificatie { LeeftijdsCategorie = "35+" };

        var uitkomst = await BeschikbaarheidVoorwaarden.ControleerSpeelduurAsync(classificatie,
            Speeltijden(("35+", 80), ("JO35", 999)), null);

        uitkomst.Should().BeNull();
        classificatie.LeeftijdsCategorie.Should().Be("35+");
    }

    [Theory]
    [InlineData(null, "nee")]
    [InlineData("", "nee")]
    [InlineData("JO99", "ja")]
    public async Task Speelduur_Onbekend_GaatNaarHandmatigePlanningZonderBeheerinstructie(string? categorie, string genoemd)
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, LeeftijdsCategorie = categorie };
        var trace = new TraceBuilder();

        var json = await BeschikbaarheidVoorwaarden.ControleerSpeelduurAsync(classificatie, Speeltijden(("JO23", 105)), trace);

        json.Should().NotBeNull();
        var r = JObject.Parse(json!);
        r[BeschikbaarheidVoorwaarden.HandmatigePlanningVlag]!.Value<bool>().Should().BeTrue();
        json.Should().NotContain("speeltijden").And.NotContain("/instellingen");
        classificatie.TeamHandmatigBeoordelen.Should().BeTrue("de bestaande afsluitende handmatige route (#1816) neemt het over");
        var stap = trace.Stappen.Should().ContainSingle().Subject;
        stap.Code.Should().Be(TraceCodes.Speelduur);
        stap.Zekerheid.Should().Be(ZekerheidsNiveau.Mislukt);
        stap.Details["categorieGenoemd"].Should().Be(genoemd);
    }

    [Fact]
    public async Task Speelduur_SpeeltijdZonderDuur_GaatNaarHandmatigePlanning()
    {
        var classificatie = new BerichtClassificatie { LeeftijdsCategorie = "JO23" };

        var json = await BeschikbaarheidVoorwaarden.ControleerSpeelduurAsync(classificatie, Speeltijden(("JO23", 0)), null);

        json.Should().NotBeNull();
        classificatie.TeamHandmatigBeoordelen.Should().BeTrue();
    }

    [Fact]
    public async Task Speelduur_Onbekend_MaaktHetZekerheidsoordeelOnzeker()
    {
        var trace = new TraceBuilder();
        await BeschikbaarheidVoorwaarden.ControleerSpeelduurAsync(new BerichtClassificatie(), Speeltijden(), trace);

        var oordeel = ZekerheidsBeoordeling.Beoordeel(trace.Stappen);

        oordeel.IsZeker.Should().BeFalse();
        oordeel.Redenen.Should().Contain("Speelduur bepalen: Niet bekend, handmatige planning");
    }

    [Fact]
    public void Speelduur_TraceStapOverleeftDeOpslagprojectie()
    {
        var trace = new TraceBuilder().SpeelduurOnbekend(categorieGenoemd: true);

        var geprojecteerd = TraceOpslagProjectie.Projecteer(trace.Bouw());

        var stap = geprojecteerd.Stappen.Single(s => s.Code == TraceCodes.Speelduur);
        stap.Details.Should().ContainKey("categorieGenoemd").WhoseValue.Should().Be("ja");
    }

    [Theory]
    [InlineData("Senioren")]
    [InlineData("1-99")]
    [InlineData(null)]
    public async Task Speelduur_O23Team_GebruiktJo23OokBijSeniorencategorie(string? categorie)
    {
        // #1863: het bericht of Sportlink noemt "Senioren", maar de duur is die van JO23.
        var classificatie = new BerichtClassificatie { LeeftijdsCategorie = categorie, TeamNaam = "ALLSTARS O23-1" };

        var uitkomst = await BeschikbaarheidVoorwaarden.ControleerSpeelduurAsync(classificatie,
            Speeltijden(("JO23", 105), ("1-99", 115)), null);

        uitkomst.Should().BeNull();
        classificatie.LeeftijdsCategorie.Should().Be("JO23");
    }

    [Fact]
    public async Task Speelduur_O23TeamZonderJo23Rij_ValtTerugOpCategorie()
    {
        var classificatie = new BerichtClassificatie { LeeftijdsCategorie = "Senioren", TeamNaam = "ALLSTARS O23-1" };

        var uitkomst = await BeschikbaarheidVoorwaarden.ControleerSpeelduurAsync(classificatie, Speeltijden(("1-99", 115)), null);

        uitkomst.Should().BeNull();
        classificatie.LeeftijdsCategorie.Should().Be("1-99");
    }

    [Fact]
    public async Task Speelduur_Seniorenteam_BlijftSeniorenduur()
    {
        var classificatie = new BerichtClassificatie { LeeftijdsCategorie = "Senioren", TeamNaam = "ALLSTARS 1" };

        var uitkomst = await BeschikbaarheidVoorwaarden.ControleerSpeelduurAsync(classificatie,
            Speeltijden(("JO23", 105), ("1-99", 115)), null);

        uitkomst.Should().BeNull();
        classificatie.LeeftijdsCategorie.Should().Be("1-99");
    }
}
