using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// #1962 (WZ-ADR-014): het speeltijdtotaal is helften × helft + rust zonder buffer, en 35+/VR30+ krijgen in de
/// optimalisatie hun eigen KNVB-speeltijd.
/// </summary>
public class SpeeltijdHelftenTests
{
    [Theory]
    [InlineData(2, 45, 15, 105)] // senioren, zonder de 10 minuten buffer van #291
    [InlineData(2, 20, 10, 50)]  // JO8
    [InlineData(1, 20, 0, 20)]   // 35+ en VR30+
    public void Totaal_IsHelftenMaalHelftPlusRust(int helften, int helft, int rust, int verwacht) =>
        SpeeltijdBerekening.Totaal(helften, helft, rust).Should().Be(verwacht);

    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(3, 20, 0)]
    [InlineData(2, 0, 10)]
    [InlineData(2, 20, -1)]
    [InlineData(1, 20, 5)]
    public void Valideer_WeigertOngeldigeInvoer(int helften, int helft, int rust) =>
        SpeeltijdBerekening.Valideer(helften, helft, rust).Should().NotBeNull();

    [Theory]
    [InlineData(2, int.MaxValue, 0)]          // vermenigvuldiging zou overlopen
    [InlineData(2, 1_073_741_824, 0)]         // 2 × 2^30 = 2^31: precies één voorbij int.MaxValue
    [InlineData(1, 20, int.MaxValue)]         // optelling zou overlopen (en rust bij één helft)
    [InlineData(2, 20, int.MaxValue)]         // optelling zou overlopen
    [InlineData(2, 1440, 0)]                  // totaal 2880 > één dag
    [InlineData(2, 700, 41)]                  // totaal 1441
    public void Valideer_WeigertTotaalBovenEenDagEnOverflow(int helften, int helft, int rust)
    {
        SpeeltijdBerekening.Valideer(helften, helft, rust).Should().NotBeNull();
        var (_, totaal, fout) = SpeeltijdBerekening.Bereken(helften, helft, rust);
        fout.Should().NotBeNull();
        totaal.Should().Be(0, "bij een validatiefout wordt geen (overgelopen) totaal berekend");
    }

    [Fact]
    public void Valideer_AccepteertPreciesEenDag()
    {
        SpeeltijdBerekening.Valideer(2, 700, 40).Should().BeNull();
        SpeeltijdBerekening.Bereken(2, 700, 40).Totaal.Should().Be(1440);
    }

    [Fact]
    public void Bereken_ZonderHelften_GebruiktTweeHelften()
    {
        var (helften, totaal, fout) = SpeeltijdBerekening.Bereken(null, 45, 15);
        (helften, totaal, fout).Should().Be((2, 105, (string?)null));
    }

    [Fact]
    public void Totaal_LooptNooitStilOver() =>
        FluentActions.Invoking(() => SpeeltijdBerekening.Totaal(2, int.MaxValue, 0))
            .Should().Throw<OverflowException>();

    [Theory]
    [InlineData(2, 45, 15)]
    [InlineData(1, 20, 0)]
    public void Valideer_AccepteertGeldigeInvoer(int helften, int helft, int rust) =>
        SpeeltijdBerekening.Valideer(helften, helft, rust).Should().BeNull();

    [Theory]
    [InlineData("ALLSTARS 35+1", "35+")]
    [InlineData("ALLSTARS 35+4", "35+")]
    [InlineData("35+2", "35+")]
    [InlineData("ALLSTARS VR30+1", "VR30+")]
    [InlineData("allstars vr30+2", "VR30+")]
    [InlineData("ALLSTARS 1", null)]
    [InlineData("ALLSTARS VR1", null)]
    [InlineData("ALLSTARS 135+1", null)]
    [InlineData("ALLSTARS 35+", null)]
    [InlineData(null, null)]
    public void VeteranenSpeeltijdSleutel_HerkentAlleen35PlusEnVr30Plus(string? team, string? verwacht) =>
        TeamNaamNormalisatie.VeteranenSpeeltijdSleutel(team).Should().Be(verwacht);

    private static Dictionary<string, Speeltijd> Speeltijden(bool metVeteranen) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["1-99"] = new() { Leeftijd = "1-99", WedstrijdTotaal = 105, Veldafmeting = 1.00m },
            ["VR"] = new() { Leeftijd = "VR", WedstrijdTotaal = 105, Veldafmeting = 1.00m },
            ["35+"] = new() { Leeftijd = "35+", WedstrijdTotaal = metVeteranen ? 20 : 0, Veldafmeting = .50m },
            ["VR30+"] = new() { Leeftijd = "VR30+", WedstrijdTotaal = metVeteranen ? 20 : 0, Veldafmeting = .50m },
        };

    [Theory]
    [InlineData("ALLSTARS 35+1", "Senioren", "35+")]
    [InlineData("ALLSTARS VR30+1", "Senioren Vrouwen", "VR30+")]
    [InlineData("ALLSTARS 1", "1-99", "1-99")]
    public void OptimalisatieSleutel_VeteranenKrijgenHunEigenSpeeltijd(string team, string categorie, string verwacht) =>
        AutoPlanRegels.OptimalisatieSpeeltijdSleutel(team, LeeftijdNormalisatie.Normaliseer(categorie), false,
            Speeltijden(metVeteranen: true)).Should().Be(verwacht);

    [Fact]
    public void OptimalisatieSleutel_ZonderVeteranenrij_ValtTerugOpSenioren() =>
        AutoPlanRegels.OptimalisatieSpeeltijdSleutel("ALLSTARS 35+1", "1-99", false, Speeltijden(metVeteranen: false))
            .Should().Be("1-99");

    [Fact]
    public void BepaalPlanDoel_35PlusTeamGebruiktDeVeteranenSpeeltijd()
    {
        var speeltijden = Speeltijden(metVeteranen: true);
        speeltijden["35+"].StandaardVoorkeurTijd = new TimeOnly(20, 0);

        var doel = AutoPlanRegels.BepaalPlanDoel("ALLSTARS 35+1", "1-99", false,
            new Dictionary<string, TeamVoorkeurVeld>(), new Dictionary<string, List<(TimeOnly, int)>>(),
            speeltijden, "ALLSTARS");

        doel.Leeftijd.Should().Be("35+");
        doel.DoelTijd.Should().Be(new TimeOnly(20, 0));
    }
}
