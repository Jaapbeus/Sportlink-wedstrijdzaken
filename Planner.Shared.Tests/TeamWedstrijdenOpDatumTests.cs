using FluentAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// De invarianten van <see cref="TeamWedstrijdenOpDatum"/> (#945).
///
/// <para>
/// Dit type bestaat om één verwarring onmogelijk te maken: "de teamnaam was niet herleidbaar" en
/// "dit team heeft die dag geen wedstrijd" leverden allebei een lege lijst op, en lazen daarmee
/// allebei als "geen conflict". <c>check-availability</c> meldde daardoor "beschikbaar" terwijl er
/// niets was vergeleken.
/// </para>
///
/// <para>
/// De <b>Postgres</b>-tier heeft hier bovenop een integratietest die het volledige pad meet
/// (<c>CheckAvailabilityAsync_TeamNietInTeamlijst_WaarschuwtDatErNietsIsGecontroleerd</c>). Voor de
/// SQL Server-tier bestaat zo'n harnas niet — <c>FunctionApp.Tests</c> heeft geen enkele test tegen
/// een levende SQL Server — dus daar is dit de enige geautomatiseerde afgrendeling. Beide tiers
/// gebruiken exact dit type, en de servicewijziging is aan beide kanten dezelfde.
/// </para>
/// </summary>
public class TeamWedstrijdenOpDatumTests
{
    [Fact]
    public void NietHerkend_IsLeegEnMarkeertDatErNietsIsGecontroleerd()
    {
        var uitkomst = TeamWedstrijdenOpDatum.NietHerkend();

        uitkomst.TeamHerkend.Should().BeFalse();
        uitkomst.Wedstrijden.Should().BeEmpty();
    }

    /// <summary>
    /// Elke aanroep levert een eigen lijst. Met één gedeelde, statisch bewaarde lege lijst zou een
    /// aanroeper die eraan toevoegt de uitkomst van alle latere aanroepen vervuilen — en dat is
    /// precies de klasse stille fout die dit type juist moet uitsluiten.
    /// </summary>
    [Fact]
    public void NietHerkend_GeeftElkeKeerEenEigenLijst()
    {
        var eerste = TeamWedstrijdenOpDatum.NietHerkend();
        var tweede = TeamWedstrijdenOpDatum.NietHerkend();

        eerste.Wedstrijden.Should().NotBeSameAs(tweede.Wedstrijden);

        eerste.Wedstrijden.Add(new BestaandeWedstrijd());
        tweede.Wedstrijden.Should().BeEmpty("de tweede uitkomst mag niets merken van de eerste");
    }

    [Fact]
    public void Herkend_MetLegeLijst_BetekentEchtGeenWedstrijdDieDag()
    {
        var uitkomst = TeamWedstrijdenOpDatum.Herkend(new List<BestaandeWedstrijd>());

        uitkomst.TeamHerkend.Should().BeTrue(
            "een herkend team zonder wedstrijden is een geldig, gecontroleerd antwoord — niet hetzelfde "
            + "als een onherkenbare teamnaam");
        uitkomst.Wedstrijden.Should().BeEmpty();
    }

    [Fact]
    public void Herkend_GeeftDeAangeleverdeWedstrijdenOngewijzigdTerug()
    {
        var wedstrijden = new List<BestaandeWedstrijd>
        {
            new() { TeamNaam = "JO13-1", Wedstrijd = "JO13-1 - Tegenstander" }
        };

        var uitkomst = TeamWedstrijdenOpDatum.Herkend(wedstrijden);

        uitkomst.TeamHerkend.Should().BeTrue();
        uitkomst.Wedstrijden.Should().BeSameAs(wedstrijden);
    }
}
