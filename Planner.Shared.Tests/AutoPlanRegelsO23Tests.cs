using AwesomeAssertions;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>#1863: een O23-team gebruikt overal de JO23-speeltijd, ook als Sportlink het als "Senioren" (1-99) levert.</summary>
public class AutoPlanRegelsO23Tests
{
    private const string Club = "TESTCLUB";

    private static Dictionary<string, Speeltijd> Speeltijden(int jo23 = 105) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["JO23"] = new Speeltijd { Leeftijd = "JO23", WedstrijdTotaal = jo23, Veldafmeting = 1.00m, StandaardVoorkeurTijd = new TimeOnly(12, 0) },
        ["1-99"] = new Speeltijd { Leeftijd = "1-99", WedstrijdTotaal = 115, Veldafmeting = 1.00m, StandaardVoorkeurTijd = new TimeOnly(14, 30) },
    };

    private static PlanDoel Doel(string team, string categorie, Dictionary<string, Speeltijd> speeltijden)
        => AutoPlanRegels.BepaalPlanDoel(team, categorie, false,
            new Dictionary<string, TeamVoorkeurVeld>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<(TimeOnly, int)>>(StringComparer.OrdinalIgnoreCase), speeltijden, Club);

    [Fact]
    public void O23MetSeniorencategorie_KrijgtJo23SleutelEnStreeftijd()
    {
        var doel = Doel("TESTCLUB O23-1", "1-99", Speeltijden());

        doel.Leeftijd.Should().Be("JO23");
        doel.DoelTijd.Should().Be(new TimeOnly(12, 0));
    }

    [Fact]
    public void EersteElftal_BlijftSeniorenSleutel()
    {
        var doel = Doel("TESTCLUB 1", "1-99", Speeltijden());

        doel.Leeftijd.Should().Be("1-99");
        doel.DoelTijd.Should().Be(new TimeOnly(14, 30));
    }

    [Fact]
    public void O23ZonderBruikbareJo23Rij_ValtTerugOpSeniorenSleutel()
    {
        Doel("TESTCLUB O23-1", "1-99", Speeltijden(jo23: 0)).Leeftijd.Should().Be("1-99");
    }

    /// <summary>
    /// De veldbezetting van beide tiers gebruikt dezelfde sleutelkeuze als de planning; zonder bron-categorie
    /// leidt de demomodus de leeftijd uit de teamnaam af, en een O23-team krijgt ook dan JO23.
    /// </summary>
    [Theory]
    [InlineData("TESTCLUB O23-1", "1-99", false, 105, "JO23")]
    [InlineData("TESTCLUB O23-1", "1-99", false, 0, "1-99")]
    [InlineData("TESTCLUB MO23-1", "1-99", false, 105, "1-99")]
    [InlineData("TESTCLUB 1", "1-99", false, 105, "1-99")]
    [InlineData("ALLSTARS JO13-1", null, true, 105, "JO13")]
    [InlineData("ALLSTARS JO13-1", null, false, 105, "")]
    [InlineData("ALLSTARS O23-1", null, true, 105, "JO23")]
    public void SpeeltijdSleutelVoorWedstrijd_KiestDezelfdeSleutelAlsDePlanning(
        string team, string? categorie, bool isAllstars, int jo23, string verwacht)
    {
        AutoPlanRegels.SpeeltijdSleutelVoorWedstrijd(team, categorie, isAllstars, Speeltijden(jo23)).Should().Be(verwacht);
    }

    /// <summary>
    /// Toepassen van een getoonde planning (<see cref="AutoPlanExactApply"/>) rekent met dezelfde O23-duur: het
    /// O23-team (Sportlink-categorie 1-99) bezet het veld 60 minuten (JO23), zodat een wedstrijd om 11:30 past.
    /// Met de seniorenduur (120) of zonder bruikbare JO23-rij botst die wedstrijd.
    /// </summary>
    [Theory]
    [InlineData(60, true)]
    [InlineData(0, false)]
    public void ExactApply_O23Team_BezetHetVeldMetJo23Duur(int jo23, bool past)
    {
        var velden = new List<VeldInfo> { new() { VeldNummer = 101, VeldNaam = "veld 101" } };
        var speeltijden = new Dictionary<string, Speeltijd>(StringComparer.OrdinalIgnoreCase)
        {
            ["JO23"] = new() { Leeftijd = "JO23", WedstrijdTotaal = jo23, Veldafmeting = 1.00m },
            ["1-99"] = new() { Leeftijd = "1-99", WedstrijdTotaal = 120, Veldafmeting = 1.00m },
            ["JO13"] = new() { Leeftijd = "JO13", WedstrijdTotaal = 60, Veldafmeting = .25m },
        };
        AutoPlanApplySource[] bronnen =
        [
            new(1, null, null, "ALLSTARS O23-1", "1-99", null),
            new(2, null, null, "ALLSTARS JO13-1", "JO13", null),
        ];
        AutoPlanApplyChoice[] keuzes =
        [
            new(1, null, null, 101, null, "10:00"),
            new(2, null, null, 101, "A2", "11:30"),
        ];

        var toepassen = () => AutoPlanExactApply.Validate(keuzes, bronnen, velden, speeltijden,
            new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase), 15);

        if (past) toepassen().Should().HaveCount(2);
        else toepassen.Should().Throw<AutoPlanApplyConflictException>();
    }
}
