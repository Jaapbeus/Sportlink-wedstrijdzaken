using AwesomeAssertions;
using Planner.Shared;
using Planner.Shared.Planning;
using Xunit;

namespace Planner.Shared.Tests.Planning;

/// <summary>
/// #1430: de conflictdetectie die na een handmatige zet op "Veld optimalisatie" draait, en die
/// dezelfde buffer- en baanregels gebruikt als de automatische planner (<see cref="FieldScheduler"/>).
/// De nummers in de testnamen verwijzen naar de verplichte gevallen uit het issue.
/// </summary>
public class PlanningConflictDetectieTests
{
    private const int Algemeen = 15;

    private static PlanningSlot Slot(string team, string? veld, string? sub, string start, int duur = 60,
        int? voor = null, int? na = null) =>
        new(team, veld, sub, TimeOnly.Parse(start), duur, voor, na);

    private static IReadOnlyList<PlanningConflict> Detecteer(params PlanningSlot[] slots) =>
        PlanningConflictDetectie.Detecteer(slots, Algemeen);

    // ── Veld: overlap op banen ──

    [Fact]
    public void Geval1_ZelfdeKwartbaanOverlappend_GeeftVeldOverlap()
    {
        var c = Detecteer(Slot("A", "Veld 1", "A1", "10:00"), Slot("B", "Veld 1", "A1", "10:30"));

        c.Should().ContainSingle().Which.Soort.Should().Be(PlanningConflictSoort.VeldOverlap);
    }

    [Fact]
    public void Geval2_HalfVeldAEnHalfVeldBOverlappend_GeenConflict()
    {
        Detecteer(Slot("A", "Veld 1", "A", "10:00"), Slot("B", "Veld 1", "B", "10:00")).Should().BeEmpty();
    }

    [Fact]
    public void Geval3_HalfVeldAPlusKwartA1Overlappend_GeeftVeldOverlap()
    {
        Detecteer(Slot("A", "Veld 1", "A", "10:00"), Slot("B", "Veld 1", "A1", "10:15"))
            .Should().ContainSingle().Which.Soort.Should().Be(PlanningConflictSoort.VeldOverlap);
    }

    [Fact]
    public void Geval4_HalfVeldAPlusKwartB1Overlappend_GeenConflict()
    {
        Detecteer(Slot("A", "Veld 1", "A", "10:00"), Slot("B", "Veld 1", "B1", "10:15")).Should().BeEmpty();
    }

    [Fact]
    public void HeelVeldZonderSubpositie_BotstMetElkVelddeel()
    {
        Detecteer(Slot("A", "Veld 1", null, "10:00"), Slot("B", "Veld 1", "B2", "10:15"))
            .Should().ContainSingle().Which.Soort.Should().Be(PlanningConflictSoort.VeldOverlap);
    }

    [Fact]
    public void OverlapVanEenMinuut_OpZelfdeBaan_IsConflict()
    {
        var c = Detecteer(Slot("A", "Veld 1", "A", "10:00"), Slot("B", "Veld 1", "A", "10:59"));

        c.Should().ContainSingle().Which.Soort.Should().Be(PlanningConflictSoort.VeldOverlap);
    }

    [Fact]
    public void OverlapVanEenMinuut_OpAndereHelft_IsGeenConflictEnOokGeenBufferconflict()
    {
        // Gelijktijdig naast elkaar: daar hoort géén buffer tussen (zelfde regel als PastOpVeld).
        Detecteer(Slot("A", "Veld 1", "A", "10:00"), Slot("B", "Veld 1", "B", "10:59")).Should().BeEmpty();
    }

    // ── Veld: buffer ──

    [Fact]
    public void Geval5_GatKleinerDanAlgemeneBuffer_GeeftVeldBuffer()
    {
        var c = Detecteer(Slot("A", "Veld 1", "A", "10:00"), Slot("B", "Veld 1", "B", "11:10"));

        var conflict = c.Should().ContainSingle().Which;
        conflict.Soort.Should().Be(PlanningConflictSoort.VeldBuffer);
        conflict.GatMinuten.Should().Be(10);
        conflict.VereisteBufferMinuten.Should().Be(Algemeen);
        conflict.DoorTeamregel(Algemeen).Should().BeFalse();
    }

    [Fact]
    public void Geval6_GatExactGelijkAanAlgemeneBuffer_GeenConflict()
    {
        Detecteer(Slot("A", "Veld 1", "A", "10:00"), Slot("B", "Veld 1", "A", "11:15")).Should().BeEmpty();
    }

    [Fact]
    public void Geval6_GatExactGelijkAanEffectieveTeambuffer_GeenConflict()
    {
        Detecteer(Slot("A", "Veld 1", "A", "10:00", na: 60), Slot("B", "Veld 1", "A", "12:00")).Should().BeEmpty();
    }

    [Fact]
    public void Geval6_GatEenMinuutKleinerDanTeambuffer_IsConflict()
    {
        Detecteer(Slot("A", "Veld 1", "A", "10:00", na: 60), Slot("B", "Veld 1", "A", "11:59"))
            .Should().ContainSingle().Which.VereisteBufferMinuten.Should().Be(60);
    }

    [Fact]
    public void Geval7_VoorgangerBufferNa60_Gat30_IsConflict()
    {
        // Het concrete scenario uit #1430: algemene buffer 15, team A BufferNa 60, A 10:00–11:00,
        // de volgende wedstrijd gesleept naar 11:30. Vóór #1430 zag de GUI hier niets.
        var c = Detecteer(Slot("A", "Veld 1", null, "10:00", na: 60), Slot("B", "Veld 1", null, "11:30"));

        var conflict = c.Should().ContainSingle().Which;
        conflict.Soort.Should().Be(PlanningConflictSoort.VeldBuffer);
        conflict.GatMinuten.Should().Be(30);
        conflict.VereisteBufferMinuten.Should().Be(60);
        conflict.DoorTeamregel(Algemeen).Should().BeTrue();
    }

    [Fact]
    public void Geval8_OpvolgerBufferVoor45_Gat30_IsConflict()
    {
        Detecteer(Slot("A", "Veld 1", null, "10:00"), Slot("B", "Veld 1", null, "11:30", voor: 45))
            .Should().ContainSingle().Which.VereisteBufferMinuten.Should().Be(45);
    }

    [Fact]
    public void Geval9_RichtingOmgekeerd_BufferVoorVanDeVoorgangerTeltNiet()
    {
        // A heeft alleen een BufferVoor; als voorganger is die niet van toepassing.
        Detecteer(Slot("A", "Veld 1", null, "10:00", voor: 60), Slot("B", "Veld 1", null, "11:30")).Should().BeEmpty();
    }

    [Fact]
    public void Geval9_RichtingOmgekeerd_BufferNaVanDeOpvolgerTeltNiet()
    {
        Detecteer(Slot("A", "Veld 1", null, "10:00"), Slot("B", "Veld 1", null, "11:30", na: 60)).Should().BeEmpty();
    }

    [Fact]
    public void Geval9_InvoervolgordeMaaktNietUit_SorteertOpStarttijd()
    {
        // B staat eerst in de invoer maar speelt later: de BufferNa van A blijft de maatgevende.
        var c = Detecteer(Slot("B", "Veld 1", null, "11:30"), Slot("A", "Veld 1", null, "10:00", na: 60));

        var conflict = c.Should().ContainSingle().Which;
        conflict.Eerste.TeamNaam.Should().Be("A");
        conflict.Tweede.TeamNaam.Should().Be("B");
    }

    [Fact]
    public void BeideKanten_GrootsteVanBufferNaEnBufferVoorTelt()
    {
        Detecteer(Slot("A", "Veld 1", null, "10:00", na: 30), Slot("B", "Veld 1", null, "11:40", voor: 45))
            .Should().ContainSingle().Which.VereisteBufferMinuten.Should().Be(45);
    }

    [Fact]
    public void TeamregelKleinerDanAlgemeneBuffer_AlgemeneBufferBlijftGelden()
    {
        Detecteer(Slot("A", "Veld 1", null, "10:00", na: 5), Slot("B", "Veld 1", null, "11:10"))
            .Should().ContainSingle().Which.VereisteBufferMinuten.Should().Be(Algemeen);
    }

    [Fact]
    public void TeamsZonderSpecifiekeBuffer_GebruikenDeAlgemeneBuffer()
    {
        var c = PlanningConflictDetectie.Detecteer(
            [Slot("A", "Veld 1", null, "10:00"), Slot("B", "Veld 1", null, "11:20")], algemeneBuffer: 30);

        c.Should().ContainSingle().Which.VereisteBufferMinuten.Should().Be(30);
    }

    [Fact]
    public void BufferVlakVoorMiddernacht_WordtNietWeggewikkeld()
    {
        // 23:00 + 50 = 23:50; opvolger 23:55 → gat 5 < 15. TimeOnly.AddMinutes zou de buffergrens
        // naar 00:05 wikkelen en dit conflict missen.
        Detecteer(Slot("A", "Veld 1", null, "23:00", duur: 50), Slot("B", "Veld 1", null, "23:55", duur: 4))
            .Should().ContainSingle().Which.GatMinuten.Should().Be(5);
    }

    // ── Team over velden heen (#939) ──

    [Fact]
    public void Geval10_ZelfdeTeamGelijktijdigOpTweeVelden_GeeftTeamOverlap()
    {
        Detecteer(Slot("A", "Veld 1", null, "10:00"), Slot("A", "Veld 2", null, "10:30"))
            .Should().ContainSingle().Which.Soort.Should().Be(PlanningConflictSoort.TeamOverlap);
    }

    [Fact]
    public void Geval11_ZelfdeTeamNietOverlappendBinnenTeambuffer_GeeftTeamBuffer()
    {
        var c = Detecteer(Slot("A", "Veld 1", null, "10:00", na: 60), Slot("A", "Veld 2", null, "11:30"));

        var conflict = c.Should().ContainSingle().Which;
        conflict.Soort.Should().Be(PlanningConflictSoort.TeamBuffer);
        conflict.VereisteBufferMinuten.Should().Be(60);
    }

    [Fact]
    public void Geval12_VerschillendeTeamsOpVerschillendeVelden_GeenConflict()
    {
        Detecteer(Slot("A", "Veld 1", null, "10:00"), Slot("B", "Veld 2", null, "10:00")).Should().BeEmpty();
    }

    [Fact]
    public void TeamnaamVergelijking_IsHoofdletterongevoelig_ZoalsDeServer()
    {
        Detecteer(Slot("JO13-1", "Veld 1", null, "10:00"), Slot("jo13-1", "Veld 2", null, "10:00"))
            .Should().ContainSingle().Which.Soort.Should().Be(PlanningConflictSoort.TeamOverlap);
    }

    [Fact]
    public void ZelfdeTeamOpZelfdeVeld_GeeftZowelVeldAlsTeamconflict()
    {
        // Ongewijzigd t.o.v. vóór #1430: beide controles zijn onafhankelijk en melden elk.
        var c = Detecteer(Slot("A", "Veld 1", null, "10:00"), Slot("A", "Veld 1", null, "10:30"));

        c.Select(x => x.Soort).Should().Equal(PlanningConflictSoort.VeldOverlap, PlanningConflictSoort.TeamOverlap);
    }

    // ── Robuustheid en volgorde ──

    [Fact]
    public void Geval13_ZonderDuurOfVeldnaam_VeiligGenegeerd()
    {
        var c = Detecteer(
            Slot("A", "Veld 1", null, "10:00", duur: 0),
            Slot("B", "Veld 1", null, "10:00"),
            Slot("C", null, null, "10:00"),
            Slot("", "Veld 2", null, "10:00"),
            Slot("", "Veld 2", "B", "12:00"));

        // C heeft geen veld en een uniek team; A heeft geen duur; de twee naamloze slots op Veld 2
        // liggen ver uit elkaar. Niets botst.
        c.Should().BeEmpty();
    }

    [Fact]
    public void Geval14_MeerdereWedstrijden_StabieleVolgordeZonderDubbelen()
    {
        PlanningSlot[] slots =
        [
            Slot("C", "Veld 2", null, "10:00"),
            Slot("A", "Veld 1", "A", "10:00"),
            Slot("B", "Veld 1", "A", "10:30"),
            Slot("D", "Veld 1", "A", "11:40"),
            Slot("C", "Veld 1", "B", "10:30"),
        ];

        var eerste = Detecteer(slots);
        var tweede = Detecteer(slots);

        eerste.Select(Omschrijf).Should().Equal(
            // Veld 2 komt eerst voor in de invoer; daar staat één wedstrijd, dus niets.
            // Veld 1, op starttijd: A 10:00, B 10:30, C 10:30, D 11:40.
            "VeldOverlap Veld 1 A-B",
            "VeldBuffer Veld 1 B-D",     // B eindigt 11:30, D start 11:40: gat 10
            "VeldBuffer Veld 1 C-D",     // C eindigt 11:30 op B, D op A — veldbuffer geldt voor het hele veld
            "TeamOverlap C C-C");        // C staat om 10:00 op Veld 2 en om 10:30 op Veld 1
        tweede.Select(Omschrijf).Should().Equal(eerste.Select(Omschrijf));
        eerste.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Geval14_GelijkeStarttijden_BehoudenInvoervolgorde()
    {
        var c = Detecteer(Slot("X", "Veld 1", "A1", "10:00"), Slot("Y", "Veld 1", "A1", "10:00"));

        var conflict = c.Should().ContainSingle().Which;
        conflict.Eerste.TeamNaam.Should().Be("X");
        conflict.Tweede.TeamNaam.Should().Be("Y");
    }

    private static string Omschrijf(PlanningConflict c) =>
        $"{c.Soort} {c.Groep} {c.Eerste.TeamNaam}-{c.Tweede.TeamNaam}";
}
