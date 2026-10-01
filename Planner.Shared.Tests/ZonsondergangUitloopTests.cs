using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// Legt de zonsondergang-uitloop vast (#1409): een veld zonder kunstlicht blijft na de
/// zonsondergang nog <see cref="PlannerShared.SunsetUitloopMinuten"/> minuten bruikbaar. De
/// oorspronkelijke grens op de minuut wees een JO13-wedstrijd (17:45–19:00) af terwijl het om
/// 19:00 nog ruim licht was.
/// </summary>
public class ZonsondergangUitloopTests
{
    private static readonly TimeOnly Zonsondergang = new(19, 4);

    private static List<VeldBeschikbaarheidInfo> VeldMetZonsondergang() =>
    [
        new() { VeldNummer = 5, BeschikbaarVanaf = new TimeOnly(17, 0), BeschikbaarTot = new TimeOnly(22, 0), GebruikZonsondergang = true }
    ];

    [Fact]
    public void Uitloop_IsVijftienMinuten()
        => PlannerShared.SunsetUitloopMinuten.Should().Be(15);

    [Fact]
    public void ZonsondergangEindtijd_TeltDeUitloopOpBijDeZonsondergang()
        => PlannerShared.ZonsondergangEindtijd(Zonsondergang).Should().Be(new TimeOnly(19, 19));

    [Fact]
    public void ZonsondergangEindtijd_WikkeltNooitOverMiddernacht()
        => PlannerShared.ZonsondergangEindtijd(new TimeOnly(23, 55)).Should().Be(new TimeOnly(23, 59));

    [Theory]
    [InlineData("17:45", 75, true)]   // eindigt 19:00, vóór zonsondergang
    [InlineData("17:55", 75, true)]   // eindigt 19:10, 6 min na zonsondergang: binnen de uitloop
    [InlineData("18:04", 75, true)]   // eindigt 19:19: precies op de grens
    [InlineData("18:05", 75, false)]  // eindigt 19:20: één minuut te laat
    public void TryExactTime_RespecteertZonsondergangPlusUitloop(string aanvang, int duur, bool verwachtPlek)
    {
        var velden = VeldMetZonsondergang();
        // Zelfde afkapping als ResolveEnPasZonsondergangToe*, zonder databasetoegang.
        velden[0].BeschikbaarTot = PlannerShared.ZonsondergangEindtijd(Zonsondergang);

        var resultaat = PlannerShared.TryExactTime(
            TimeOnly.Parse(aanvang), velden, [], [], new Dictionary<string, List<TeamRegel>>(), [],
            1.00m, duur, Zonsondergang);

        (resultaat != null).Should().Be(verwachtPlek);
    }

    [Fact]
    public void Waarschuwing_ZwijgtBijRuimeMarge()
        => PlannerShared.BouwZonsondergangWaarschuwing("veld 5", new TimeOnly(18, 30), Zonsondergang)
            .Should().BeNull();

    [Fact]
    public void Waarschuwing_NoemtMargeVoorZonsondergang()
        => PlannerShared.BouwZonsondergangWaarschuwing("veld 5", new TimeOnly(19, 0), Zonsondergang)
            .Should().Contain("4 min marge").And.Contain("veld 5");

    [Fact]
    public void Waarschuwing_NoemtMinutenNaZonsondergang_NietEenNegatieveMarge()
    {
        var tekst = PlannerShared.BouwZonsondergangWaarschuwing("veld 5", new TimeOnly(19, 10), Zonsondergang);

        tekst.Should().Contain("6 min ná zonsondergang");
        tekst.Should().NotContain("-");
    }
}
