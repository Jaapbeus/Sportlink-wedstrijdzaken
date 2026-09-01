using FluentAssertions;
using Planner.Shared;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// #939: <see cref="FieldScheduler"/> hield bezetting alleen per veld bij, niet per team — een team
/// met twee wedstrijden op één dag kon daardoor twee keer op hetzelfde tijdstip ingepland worden, op
/// twee verschillende velden. Gedeeld gedrag (Planner.Shared, #888) — deze test dekt beide tiers.
/// </summary>
public class FieldSchedulerTeamConflictTests
{
    private static readonly List<VeldInfo> TweeKunstgrasVelden =
    [
        new() { VeldNummer = 101, VeldNaam = "Kunstgras 1", VeldType = "kunstgras" },
        new() { VeldNummer = 102, VeldNaam = "Kunstgras 2", VeldType = "kunstgras" },
    ];

    private static readonly List<VeldBeschikbaarheidInfo> RuimeBeschikbaarheid =
    [
        new() { VeldNummer = 101, BeschikbaarVanaf = new TimeOnly(8, 0), BeschikbaarTot = new TimeOnly(22, 0) },
        new() { VeldNummer = 102, BeschikbaarVanaf = new TimeOnly(8, 0), BeschikbaarTot = new TimeOnly(22, 0) },
    ];

    [Fact]
    public void FindAndOccupyNextSlot_ZelfdeTeamTweeKeer_KrijgtNooitHetzelfdeTijdstip()
    {
        var scheduler = new FieldScheduler(RuimeBeschikbaarheid, TweeKunstgrasVelden, buffer: 15);

        var eerste = scheduler.FindAndOccupyNextSlot(fractie: 1.00m, duurMinuten: 60, teamNaam: "AllStars JO13-1");
        var tweede = scheduler.FindAndOccupyNextSlot(fractie: 1.00m, duurMinuten: 60, teamNaam: "AllStars JO13-1");

        eerste.Should().NotBeNull("er zijn twee vrije velden, dus de eerste wedstrijd moet altijd inplanbaar zijn");
        tweede.Should().NotBeNull("de planner moet uitwijken naar een ander tijdstip, niet weigeren");

        tweede!.AanvangsTijd.Should().NotBe(eerste!.AanvangsTijd,
            "hetzelfde team kan niet op twee velden tegelijk spelen, ongeacht hoeveel velden vrij zijn");

        // Buffer-bewust: ook geen overlap inclusief de standaardbuffer (15 min) ertussen.
        var eersteBeschStart = eerste.AanvangsTijd.AddMinutes(-15);
        var eersteBeschEinde = eerste.EindTijd.AddMinutes(15);
        var overlaptMetBuffer = tweede.AanvangsTijd < eersteBeschEinde && tweede.EindTijd > eersteBeschStart;
        overlaptMetBuffer.Should().BeFalse("tussen twee wedstrijden van hetzelfde team geldt dezelfde buffer als voor veldbezetting");
    }

    [Fact]
    public void FindAndOccupyNextSlot_VerschillendeTeams_MogenWelTegelijkOpVerschillendeVelden()
    {
        // Regressiebewaking: de teamconflict-check mag géén ander team blokkeren — dat zou de
        // bestaande, gewenste "twee wedstrijden tegelijk op twee velden"-uitkomst breken.
        var scheduler = new FieldScheduler(RuimeBeschikbaarheid, TweeKunstgrasVelden, buffer: 15);

        var eerste = scheduler.FindAndOccupyNextSlot(fractie: 1.00m, duurMinuten: 60, teamNaam: "AllStars JO13-1");
        var tweede = scheduler.FindAndOccupyNextSlot(fractie: 1.00m, duurMinuten: 60, teamNaam: "AllStars JO14-1");

        eerste.Should().NotBeNull();
        tweede.Should().NotBeNull();
        tweede!.AanvangsTijd.Should().Be(eerste!.AanvangsTijd,
            "twee verschillende teams mogen wel gelijktijdig op twee verschillende velden staan");
        tweede.VeldNummer.Should().NotBe(eerste.VeldNummer);
    }

    [Fact]
    public void FindAndOccupyNearTime_ZelfdeTeamOpDezelfdeVoorkeurstijd_WijktUitNaarAnderTijdstip()
    {
        var scheduler = new FieldScheduler(RuimeBeschikbaarheid, TweeKunstgrasVelden, buffer: 15);
        var voorkeurTijd = new TimeOnly(10, 0);

        var eerste = scheduler.FindAndOccupyNearTime(voorkeurTijd, fractie: 1.00m, duurMinuten: 60, teamNaam: "AllStars JO13-1");
        var tweede = scheduler.FindAndOccupyNearTime(voorkeurTijd, fractie: 1.00m, duurMinuten: 60, teamNaam: "AllStars JO13-1");

        eerste.Should().NotBeNull();
        tweede.Should().NotBeNull();
        tweede!.AanvangsTijd.Should().NotBe(eerste!.AanvangsTijd,
            "ook op het handmatige/voorkeurstijd-pad mag hetzelfde team niet dubbel op hetzelfde tijdstip staan");
    }
}
