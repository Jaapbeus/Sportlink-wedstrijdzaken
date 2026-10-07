using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;
using Xunit;

namespace Planner.Shared.Tests.Planning;

/// <summary>#1582: Sportlink is leidend voor de Planning. Fictieve teamnamen, geen echte clubs.</summary>
public class SportlinkVeldbezettingSamenvoegingTests
{
    private sealed record Regel(string Wedstrijd, string? Tijd, string? Veld, int Duur, string Bron = "Eigen", bool NietInSportlink = false);

    private static SportlinkVeldplannerBlok Blok(string label, string start, string veld = "veld 1 A", int duur = 30)
        => new(label, veld, start, 0.5m, duur, null);

    private static IReadOnlyList<Regel> Voeg(IReadOnlyList<Regel> eigen, IReadOnlyList<SportlinkVeldplannerBlok>? blokken)
        => SportlinkVeldbezettingSamenvoeging.Voeg(eigen, blokken,
            r => (r.Wedstrijd, r.Tijd),
            (r, b) => r with { Tijd = b.StartTijd, Veld = b.Veld, Duur = b.DuurMinuten, Bron = "Sportlink" },
            b => new Regel(b.Label, b.StartTijd, b.Veld, b.DuurMinuten, "Sportlink"),
            r => r with { NietInSportlink = true },
            r => r.Tijd);

    [Fact]
    public void ZonderSportlink_BlijftDePlanningOngewijzigd()
    {
        var eigen = new[] { new Regel("Thuis 35+1 - Uit 35+1", "19:00", "veld 1", 50) };
        Voeg(eigen, null).Should().BeSameAs(eigen);
    }

    [Fact]
    public void EenGekoppeldeRegel_NeemtVeldTijdEnDuurOverUitSportlink()
    {
        var resultaat = Voeg(new[] { new Regel("Thuis 35+1 - Uit 35+1", "19:00", "veld 1", 50) },
            new[] { Blok("Thuis 35+1 - Uit 35+1", "19:30", "veld 2 B", 40) });

        resultaat.Should().ContainSingle().Which.Should().Be(new Regel("Thuis 35+1 - Uit 35+1", "19:30", "veld 2 B", 40, "Sportlink"));
    }

    [Fact]
    public void EenSportlinkBlokZonderEigenRegel_WordtZelfEenRegel()
    {
        // De bug van de releasetest: 8 van de 20 blokken stonden in de Planning, de rest werd weggegooid.
        var resultaat = Voeg(new[] { new Regel("Thuis 35+1 - Uit 35+1", "19:30", "veld 1 A", 30) },
            new[] { Blok("Thuis 35+1 - Uit 35+1", "19:30"), Blok("Ander 35+1 - Elders 35+2", "20:30", "veld 1 B") });

        resultaat.Select(r => (r.Wedstrijd, r.Tijd, r.Bron)).Should().Equal(
            ("Thuis 35+1 - Uit 35+1", "19:30", "Sportlink"), ("Ander 35+1 - Elders 35+2", "20:30", "Sportlink"));
    }

    [Fact]
    public void ZonderEigenRegels_ToontDePlanningToch_AlleSportlinkBlokken()
        => Voeg(Array.Empty<Regel>(), new[] { Blok("A - B", "10:00"), Blok("C - D", "10:30") }).Should().HaveCount(2);

    [Fact]
    public void EenEigenRegelDieSportlinkNietKent_BlijftStaan_MetEenZichtbareMarkering()
    {
        var resultaat = Voeg(new[] { new Regel("Verdwenen 35+1 - Weg 35+1", "21:00", "veld 1", 50) }, new[] { Blok("A - B", "20:00") });

        resultaat.Should().Contain(r => r.Wedstrijd == "Verdwenen 35+1 - Weg 35+1" && r.NietInSportlink);
        resultaat.Should().Contain(r => r.Wedstrijd == "A - B" && !r.NietInSportlink);
    }

    [Fact]
    public void SportlinkAntwoordtMetNulBlokken_DanIsElkeEigenRegelEenAfwijking()
        => Voeg(new[] { new Regel("A - B", "10:00", "veld 1", 50) }, Array.Empty<SportlinkVeldplannerBlok>())
            .Should().ContainSingle().Which.NietInSportlink.Should().BeTrue();

    [Fact]
    public void TweeBlokkenMetDezelfdeGegevens_ZijnTweeWedstrijden()
        => Voeg(Array.Empty<Regel>(), new[] { Blok("A - B", "10:00"), Blok("A - B", "10:00") }).Should().HaveCount(2);

    [Fact]
    public void Resultaat_IsGesorteerdOpTijd_ZonderTijdAchteraan()
    {
        var resultaat = Voeg(new[] { new Regel("Zonder - Tijd", null, null, 0), new Regel("Laat - Laat", "21:00", "veld 1", 30) },
            new[] { Blok("Vroeg - Vroeg", "09:00") });

        resultaat.Select(r => r.Wedstrijd).Should().Equal("Vroeg - Vroeg", "Laat - Laat", "Zonder - Tijd");
    }

    [Theory]
    [InlineData("Tegen 35+3 - Eigen 35+2", "Eigen 35+2", "Tegen 35+3")]          // eigen team is uit: de tegenstander is de thuisploeg
    [InlineData("Eigen 35+2 - Gast 35+1", "Eigen 35+2", "Gast 35+1")]      // eigen team is thuis
    [InlineData("Thuis 35+1 - v.v. Eigen 35+1", "Eigen 35+1", "Thuis 35+1")] // extra clubvoorvoegsel bij precies één kant
    public void Tegenstander_IsDeAndereKantVanHetLabel(string wedstrijd, string team, string verwacht)
        => SportlinkVeldbezettingSamenvoeging.Tegenstander(wedstrijd, team, uitteam: "Eigen 35+2").Should().Be(verwacht);

    [Fact]
    public void Tegenstander_BijUitwedstrijd_IsNietMeerDeEigenPloeg()
    {
        // Voorheen: kolom Tegenstander = uitteam = de eigen ploeg.
        SportlinkVeldbezettingSamenvoeging.Tegenstander("Thuis 35+2 - Eigen 35+1", "Eigen 35+1", uitteam: "Eigen 35+1")
            .Should().Be("Thuis 35+2");
    }

    [Theory]
    [InlineData("Onbekend - Anders", "Eigen 35+1")]   // geen van beide kanten is het eigen team: terugval op de uitploeg
    [InlineData("Alleen een naam", "Eigen 35+1")]     // geen scheidingsteken
    public void Tegenstander_ZonderEenduidigeKant_ValtTerugOpUitteam(string wedstrijd, string team)
        => SportlinkVeldbezettingSamenvoeging.Tegenstander(wedstrijd, team, "Uitteam").Should().Be("Uitteam");

    [Fact]
    public void Tegenstander_ZonderLabelOfTeam_ValtTerugOpUitteam()
    {
        SportlinkVeldbezettingSamenvoeging.Tegenstander(null, "Eigen 35+1", "X").Should().Be("X");
        SportlinkVeldbezettingSamenvoeging.Tegenstander("A - B", null, "X").Should().Be("X");
    }

    [Fact]
    public void SplitsWedstrijd_ZonderScheidingsteken_GeeftGeenUitploeg()
        => SportlinkVeldbezettingSamenvoeging.SplitsWedstrijd("Alleen").Should().Be(("Alleen", (string?)null));
}
