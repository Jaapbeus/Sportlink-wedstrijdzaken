using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// #1587 (review-ronde 1): een slot dat als "binnen het gecontroleerde dagdeel" wordt gepresenteerd, valt
/// volledig binnen dat dagdeel (start én einde), en een exacte aanvangstijd buiten het dagdeel laat het
/// dagdeel vervallen zodat het antwoord nooit een onware "alleen de ochtend gecontroleerd"-claim bevat.
/// </summary>
public class FieldSchedulerDagdeelTests
{
    private static readonly Dictionary<string, List<TeamRegel>> GeenTeamRegels = new();
    private static readonly TimeOnly OchtendVan = new(8, 30);
    private static readonly TimeOnly OchtendTot = new(12, 0);

    private static List<VeldBeschikbaarheidInfo> Velden() => new()
    {
        new() { VeldNummer = 1, BeschikbaarVanaf = new TimeOnly(8, 0), BeschikbaarTot = new TimeOnly(22, 0) }
    };

    private static List<VeldInfo> VeldInfos() => new() { new() { VeldNummer = 1, VeldNaam = "veld 1" } };

    // ── ToepasbaarDagdeel: de exacte tijd wint ──

    [Theory]
    [InlineData("ochtend", "10:00", "ochtend")]
    [InlineData("ochtend", "08:30", "ochtend")]   // ondergrens hoort bij het dagdeel
    [InlineData("ochtend", "11:59", "ochtend")]
    [InlineData("ochtend", "12:00", null)]        // 12:00 valt in de middag
    [InlineData("ochtend", "14:00", null)]
    [InlineData("ochtend", "08:00", null)]
    [InlineData("middag", "12:00", "middag")]
    [InlineData("middag", "16:59", "middag")]
    [InlineData("middag", "17:00", null)]
    [InlineData("avond", "17:00", "avond")]
    [InlineData("avond", "16:59", null)]
    [InlineData("nacht", "10:00", null)]
    public void ToepasbaarDagdeel_ExacteTijdBuitenHetDagdeel_LaatHetDagdeelVervallen(string dagdeel, string tijd, string? verwacht)
        => DagdeelVenster.ToepasbaarDagdeel(dagdeel, TimeOnly.Parse(tijd)).Should().Be(verwacht);

    [Fact]
    public void ToepasbaarDagdeel_ZonderExacteTijd_BlijftHetDagdeelGelden()
    {
        DagdeelVenster.ToepasbaarDagdeel("Ochtend", null).Should().Be("ochtend");
        DagdeelVenster.ToepasbaarDagdeel(null, null).Should().BeNull();
    }

    // ── Exacte tijd ──

    [Fact]
    public void TryExactTime_EindeBuitenHetDagdeel_WordtNietAlsExacteMatchGeaccepteerd()
    {
        // 11:30 + 75 min eindigt om 12:45: buiten de ochtend.
        var zonderGrens = PlannerShared.TryExactTime(new TimeOnly(11, 30), Velden(), new(), VeldInfos(),
            GeenTeamRegels, new(), 1.00m, 75, sunset: null);
        var metGrens = PlannerShared.TryExactTime(new TimeOnly(11, 30), Velden(), new(), VeldInfos(),
            GeenTeamRegels, new(), 1.00m, 75, sunset: null, uiterlijkEinde: OchtendTot);

        zonderGrens.Should().NotBeNull();
        metGrens.Should().BeNull();
    }

    [Fact]
    public void TryExactTime_EindeExactOpDeDagdeelgrens_IsToegestaan()
    {
        var slot = PlannerShared.TryExactTime(new TimeOnly(10, 45), Velden(), new(), VeldInfos(),
            GeenTeamRegels, new(), 1.00m, 75, sunset: null, uiterlijkEinde: OchtendTot);

        slot.Should().NotBeNull();
        slot!.EindTijd.Should().Be(OchtendTot);
    }

    // ── Kandidaten ──

    [Fact]
    public void FindAllSlots_MetEindBinnenDagdeel_LevertGeenSlotsDieNaHetDagdeelEindigen()
    {
        var slots = PlannerShared.FindAllSlots(Velden(), new(), VeldInfos(), GeenTeamRegels, new(),
            1.00m, 75, OchtendVan, OchtendTot, sunset: null, eindBinnenDagdeel: true);

        slots.Should().NotBeEmpty();
        slots.Should().OnlyContain(s => s.AanvangsTijd >= OchtendVan && s.EindTijd <= OchtendTot);
    }

    [Fact]
    public void FindAllSlots_ZonderEindBinnenDagdeel_BehoudtHetBestaandeGedrag()
    {
        // Het bestaande gedrag (herplan, dagbrede zoektocht): alleen de start ligt in het venster.
        var slots = PlannerShared.FindAllSlots(Velden(), new(), VeldInfos(), GeenTeamRegels, new(),
            1.00m, 75, OchtendVan, OchtendTot, sunset: null);

        slots.Should().Contain(s => s.EindTijd > OchtendTot);
    }

    [Fact]
    public void FindAllSlots_OnbeschikbareVoorkeurstijd_GeeftAlternatievenBinnenHetDagdeel()
    {
        // Het veld is van 09:00 tot 10:30 bezet; alternatieven blijven toch volledig binnen de ochtend.
        var bezet = new List<BestaandeWedstrijd>
        {
            new() { VeldNummer = 1, AanvangsTijd = new TimeOnly(9, 0), EindTijd = new TimeOnly(10, 30) }
        };

        var slots = PlannerShared.FindAllSlots(Velden(), bezet, VeldInfos(), GeenTeamRegels, new(),
            1.00m, 75, OchtendVan, OchtendTot, sunset: null, eindBinnenDagdeel: true);

        slots.Should().OnlyContain(s => s.AanvangsTijd >= OchtendVan && s.EindTijd <= OchtendTot);
        slots.Should().NotContain(s => s.AanvangsTijd < new TimeOnly(10, 30) && s.EindTijd > new TimeOnly(9, 0));
    }

    [Theory]
    [InlineData("middag", "12:00", "17:00")]
    [InlineData("avond", "17:00", "22:00")]
    public void FindAllSlots_GrensgevallenVanMiddagEnAvond_BlijvenBinnenHetVenster(string dagdeel, string van, string tot)
    {
        var (dagdeelVan, dagdeelTot) = DagdeelVenster.ZoekVenster(dagdeel)!.Value;

        var slots = PlannerShared.FindAllSlots(Velden(), new(), VeldInfos(), GeenTeamRegels, new(),
            1.00m, 105, dagdeelVan, dagdeelTot, sunset: null, eindBinnenDagdeel: true);

        slots.Should().NotBeEmpty();
        slots.Should().OnlyContain(s => s.AanvangsTijd >= TimeOnly.Parse(van) && s.EindTijd <= TimeOnly.Parse(tot));
    }
}
