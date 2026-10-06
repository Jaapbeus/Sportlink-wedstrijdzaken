using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// #1545: teamaliassen ("JO23-4", "O23-4", "[club] O23-4", "23-4") moeten dezelfde teaminstelling
/// vinden, en een team zonder eigen instelling valt terug op de standaardtijd van zijn
/// leeftijdscategorie. Clubcode is een neutrale placeholder — nooit een echte clubnaam.
/// </summary>
public class TeamSleutelLookupTests
{
    private const string Club = "TESTCLUB";

    private static Dictionary<string, List<(TimeOnly Tijd, int Prioriteit)>> Voorkeuren(params (string Team, string Tijd)[] regels)
    {
        var d = new Dictionary<string, List<(TimeOnly, int)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (team, tijd) in regels)
            d[team] = [(TimeOnly.Parse(tijd), 5)];
        return d;
    }

    private static Dictionary<string, Speeltijd> Speeltijden() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["JO23"] = new Speeltijd { Leeftijd = "JO23", WedstrijdTotaal = 105, Veldafmeting = 1.00m, StandaardVoorkeurTijd = new TimeOnly(12, 0) },
        ["JO14"] = new Speeltijd { Leeftijd = "JO14", WedstrijdTotaal = 85, Veldafmeting = 1.00m, StandaardVoorkeurTijd = new TimeOnly(11, 0) },
    };

    private static PlanDoel Doel(string team, string categorie, Dictionary<string, List<(TimeOnly, int)>> voorkeuren)
        => AutoPlanRegels.BepaalPlanDoel(team, categorie, isAllstars: false,
            new Dictionary<string, TeamVoorkeurVeld>(StringComparer.OrdinalIgnoreCase), voorkeuren, Speeltijden(), Club);

    // ── Aliassen ──

    [Theory]
    [InlineData("JO23-4")]
    [InlineData("O23-4")]
    [InlineData("TESTCLUB O23-4")]
    [InlineData("TESTCLUB JO23-4")]
    [InlineData("jo23 4")]
    [InlineData("23-4")]   // prefixloos: één kandidaat in de tabel
    public void Alias_VindtTeamVoorkeur(string bronNaam)
    {
        var voorkeuren = Voorkeuren(("JO23-4", "15:30"));

        TeamSleutelLookup.TryGetValue(voorkeuren, bronNaam, Club, out var gevonden).Should().BeTrue();
        gevonden[0].Tijd.Should().Be(new TimeOnly(15, 30));
    }

    [Fact]
    public void Alias_OokAlsBeheertabelDeKnvbNotatieBevat()
    {
        var voorkeuren = Voorkeuren(("TESTCLUB O23-4", "15:30"));

        TeamSleutelLookup.TryGetValue(voorkeuren, "JO23-4", Club, out var gevonden).Should().BeTrue();
        gevonden[0].Tijd.Should().Be(new TimeOnly(15, 30));
    }

    [Fact]
    public void Prefixloos_MetJongensEnMeisjesKandidaat_KiestNiets()
    {
        var voorkeuren = Voorkeuren(("JO14-2", "10:00"), ("MO14-2", "11:00"));

        TeamSleutelLookup.TryGetValue(voorkeuren, "14-2", Club, out _).Should().BeFalse();
    }

    [Fact]
    public void AnderTeamnummer_WordtNietGevonden()
    {
        var voorkeuren = Voorkeuren(("JO23-4", "15:30"));

        TeamSleutelLookup.TryGetValue(voorkeuren, "TESTCLUB O23-3", Club, out _).Should().BeFalse();
    }

    [Fact]
    public void Tegenstander_MetZelfdeNummer_IsGeenEigenTeam()
    {
        // Zonder strip van de eigen clubprefix blijft het clubdeel van een ander onderscheidend.
        var voorkeuren = Voorkeuren(("JO23-4", "15:30"));

        TeamSleutelLookup.TryGetValue(voorkeuren, "ANDERECLUB O23-4", Club, out _).Should().BeFalse();
    }

    // ── Rangorde: specifiek team → leeftijdscategorie ──

    [Fact]
    public void TeamMetEigenVoorkeur_KrijgtDieTijd()
    {
        var doel = Doel("TESTCLUB O23-4", "JO23", Voorkeuren(("JO23-4", "15:30")));

        doel.DoelTijd.Should().Be(new TimeOnly(15, 30));
        doel.Bron.Should().Be("team");
    }

    [Fact]
    public void TeamZonderEigenVoorkeur_ValtTerugOpCategorie()
    {
        var doel = Doel("TESTCLUB O23-3", "JO23", Voorkeuren(("JO23-4", "15:30")));

        doel.DoelTijd.Should().Be(new TimeOnly(12, 0));
        doel.Bron.Should().Be("leeftijd");
    }

    [Fact]
    public void VoorkeursveldRegel_WordtOokViaAliasGevonden()
    {
        var velden = new Dictionary<string, TeamVoorkeurVeld>(StringComparer.OrdinalIgnoreCase)
        {
            ["JO23-4"] = new TeamVoorkeurVeld { TeamNaam = "JO23-4", VeldNummer = 2, Prioriteit = 1 },
        };

        var doel = AutoPlanRegels.BepaalPlanDoel("TESTCLUB O23-4", "JO23", false, velden,
            Voorkeuren(("JO23-4", "15:30")), Speeltijden(), Club);

        doel.Laag.Should().Be(0);
        doel.VoorkeurVeldNummer.Should().Be(2);
        doel.DoelTijd.Should().Be(new TimeOnly(15, 30));
    }

    // ── Leeftijdscategorie afleiden (beschikbaarheidscheck) ──

    [Theory]
    [InlineData("JO14-2", "JO14")]
    [InlineData("TESTCLUB O14-2", "JO14")]
    [InlineData("MO13-1", "MO13")]
    [InlineData("TESTCLUB O23-4", "JO23")]
    public void LeidLeeftijdsCategorieAf_UitCanoniekeTeamnaam(string team, string verwacht)
        => TeamNaamNormalisatie.LeidLeeftijdsCategorieAf(team, Club).Should().Be(verwacht);

    [Theory]
    [InlineData("14-2")]      // geen prefix: nooit JO/MO raden
    [InlineData("TESTCLUB 1")] // senioren hebben geen leeftijd+teamnummer-vorm
    [InlineData("")]
    [InlineData(null)]
    public void LeidLeeftijdsCategorieAf_ZonderZekerheid_GeeftNull(string? team)
        => TeamNaamNormalisatie.LeidLeeftijdsCategorieAf(team, Club).Should().BeNull();

    [Theory]
    [InlineData(null, "TESTCLUB O14-2", "JO14")]  // leeg → afgeleid uit het herkende team
    [InlineData("", "JO14-2", "JO14")]
    [InlineData("MO14", "JO14-2", "MO14")]        // al ingevuld → ongemoeid
    [InlineData(null, "14-2", null)]              // geen prefix → niets raden
    public void VulLeeftijdsCategorieAan(string? huidige, string team, string? verwacht)
        => TeamNaamNormalisatie.VulLeeftijdsCategorieAan(huidige, team, Club).Should().Be(verwacht);
}
