using Planner.Shared;
using FluentAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// Tests voor de pure scheduling-helpers in PlannerShared (#578).
/// Deze logica bepaalt of een beschikbaarheidsantwoord "wel" of "niet mogelijk" wordt en
/// is daarmee direct bepalend voor het reply-beleid (#572) — regressies hier zijn duur.
/// Geen database nodig: alle helpers zijn puur.
/// </summary>
public class PlannerSharedTests
{
    private static VeldBeschikbaarheidInfo Veld(int nr, string vanaf = "09:00", string tot = "18:00") =>
        new()
        {
            VeldNummer = nr,
            BeschikbaarVanaf = TimeOnly.Parse(vanaf),
            BeschikbaarTot = TimeOnly.Parse(tot)
        };

    private static VeldInfo VeldInfoKunstgras(int nr) =>
        new() { VeldNummer = nr, VeldNaam = $"veld {nr}", VeldType = "kunstgras", HeeftKunstlicht = true };

    private static BestaandeWedstrijd Bezetting(
        int veld, string van, string tot, decimal deel = 1.00m, string? team = null, long? code = null) =>
        new()
        {
            Datum = new DateOnly(2026, 9, 12),
            VeldNummer = veld,
            AanvangsTijd = TimeOnly.Parse(van),
            EindTijd = TimeOnly.Parse(tot),
            VeldDeelGebruik = deel,
            TeamNaam = team,
            Wedstrijd = team == null ? null : $"{team} - Tegenstander",
            Wedstrijdcode = code,
            Bron = "Competitie"
        };

    private static readonly Dictionary<string, List<TeamRegel>> GeenTeamRegels = new();

    // ── CanFitMatch ──

    [Fact]
    public void CanFitMatch_LeegVeld_Past()
    {
        PlannerShared.CanFitMatch(
                TimeOnly.Parse("10:00"), TimeOnly.Parse("11:45"), 1.00m, 1,
                new List<BestaandeWedstrijd>(), GeenTeamRegels, new List<TeamRegel>())
            .Should().BeTrue();
    }

    [Fact]
    public void CanFitMatch_OverlappendeWedstrijdOpHeelVeld_PastNiet()
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:30", "12:15") };

        PlannerShared.CanFitMatch(
                TimeOnly.Parse("10:00"), TimeOnly.Parse("11:45"), 1.00m, 1,
                occs, GeenTeamRegels, new List<TeamRegel>())
            .Should().BeFalse();
    }

    [Fact]
    public void CanFitMatch_BinnenStandaardBuffer_PastNiet()
    {
        // Bezetting eindigt 11:45; standaardbuffer is 15 min → 11:50 start mag niet
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:45") };

        PlannerShared.CanFitMatch(
                TimeOnly.Parse("11:50"), TimeOnly.Parse("13:00"), 1.00m, 1,
                occs, GeenTeamRegels, new List<TeamRegel>())
            .Should().BeFalse();
    }

    [Fact]
    public void CanFitMatch_NaStandaardBuffer_Past()
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:45") };

        PlannerShared.CanFitMatch(
                TimeOnly.Parse("12:00"), TimeOnly.Parse("13:00"), 1.00m, 1,
                occs, GeenTeamRegels, new List<TeamRegel>())
            .Should().BeTrue();
    }

    [Fact]
    public void CanFitMatch_TweeHalveVelden_PassenNaastElkaar()
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:15", deel: 0.50m) };

        PlannerShared.CanFitMatch(
                TimeOnly.Parse("10:00"), TimeOnly.Parse("11:15"), 0.50m, 1,
                occs, GeenTeamRegels, new List<TeamRegel>())
            .Should().BeTrue();
    }

    [Fact]
    public void CanFitMatch_DrieHalveVelden_PassenNiet()
    {
        var occs = new List<BestaandeWedstrijd>
        {
            Bezetting(1, "10:00", "11:15", deel: 0.50m),
            Bezetting(1, "10:00", "11:15", deel: 0.50m)
        };

        PlannerShared.CanFitMatch(
                TimeOnly.Parse("10:00"), TimeOnly.Parse("11:15"), 0.50m, 1,
                occs, GeenTeamRegels, new List<TeamRegel>())
            .Should().BeFalse();
    }

    [Fact]
    public void CanFitMatch_TeamRegelBufferVoor_VerruimtDeBuffer()
    {
        // Het bezette team eist 45 min buffer na; 12:00 (15 min na 11:45) mag dan niet meer
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:45", team: "TEST JO14-1") };
        var allTeamRules = new Dictionary<string, List<TeamRegel>>
        {
            ["TEST JO14-1"] = new()
            {
                new TeamRegel { TeamNaam = "TEST JO14-1", RegelType = "BufferNa", WaardeMinuten = 45 }
            }
        };

        PlannerShared.CanFitMatch(
                TimeOnly.Parse("12:00"), TimeOnly.Parse("13:00"), 1.00m, 1,
                occs, allTeamRules, new List<TeamRegel>())
            .Should().BeFalse();
    }

    // ── TryExactTime ──

    [Fact]
    public void TryExactTime_VrijSlotOpGevraagdeTijd_GeeftSlot()
    {
        var slot = PlannerShared.TryExactTime(
            TimeOnly.Parse("10:00"),
            new List<VeldBeschikbaarheidInfo> { Veld(1) },
            new List<BestaandeWedstrijd>(),
            new List<VeldInfo> { VeldInfoKunstgras(1) },
            GeenTeamRegels, new List<TeamRegel>(), 1.00m, 105, sunset: null);

        slot.Should().NotBeNull();
        slot!.AanvangsTijd.Should().Be(TimeOnly.Parse("10:00"));
        slot.VeldNummer.Should().Be(1);
    }

    [Fact]
    public void TryExactTime_BuitenVeldbeschikbaarheid_GeeftNull()
    {
        // Veld is tot 18:00 beschikbaar; 17:30 + 105 min loopt daarbuiten
        PlannerShared.TryExactTime(
                TimeOnly.Parse("17:30"),
                new List<VeldBeschikbaarheidInfo> { Veld(1, tot: "18:00") },
                new List<BestaandeWedstrijd>(),
                new List<VeldInfo> { VeldInfoKunstgras(1) },
                GeenTeamRegels, new List<TeamRegel>(), 1.00m, 105, sunset: null)
            .Should().BeNull();
    }

    [Fact]
    public void TryExactTime_BezetVeld_ValtTerugOpAnderVeld()
    {
        var slot = PlannerShared.TryExactTime(
            TimeOnly.Parse("10:00"),
            new List<VeldBeschikbaarheidInfo> { Veld(1), Veld(2) },
            new List<BestaandeWedstrijd> { Bezetting(1, "09:30", "11:15") },
            new List<VeldInfo> { VeldInfoKunstgras(1), VeldInfoKunstgras(2) },
            GeenTeamRegels, new List<TeamRegel>(), 1.00m, 105, sunset: null);

        slot.Should().NotBeNull();
        slot!.VeldNummer.Should().Be(2);
    }

    // ── FindAllSlots ──

    [Fact]
    public void FindAllSlots_LeegVeld_GeeftMeerdereKandidaten()
    {
        var slots = PlannerShared.FindAllSlots(
            new List<VeldBeschikbaarheidInfo> { Veld(1, "09:00", "17:00") },
            new List<BestaandeWedstrijd>(),
            new List<VeldInfo> { VeldInfoKunstgras(1) },
            GeenTeamRegels, new List<TeamRegel>(),
            1.00m, 105, TimeOnly.Parse("08:30"), TimeOnly.Parse("22:00"), sunset: null);

        slots.Should().NotBeEmpty();
        slots.Should().OnlyContain(s => s.AanvangsTijd >= TimeOnly.Parse("09:00"));
        slots.Should().OnlyContain(s => s.EindTijd <= TimeOnly.Parse("17:00"));
    }

    [Fact]
    public void FindAllSlots_VolgeboektVeld_GeeftGeenKandidaten()
    {
        var slots = PlannerShared.FindAllSlots(
            new List<VeldBeschikbaarheidInfo> { Veld(1, "09:00", "12:00") },
            new List<BestaandeWedstrijd> { Bezetting(1, "09:00", "12:00") },
            new List<VeldInfo> { VeldInfoKunstgras(1) },
            GeenTeamRegels, new List<TeamRegel>(),
            1.00m, 105, TimeOnly.Parse("08:30"), TimeOnly.Parse("22:00"), sunset: null);

        slots.Should().BeEmpty();
    }

    [Fact]
    public void FindAllSlots_KunstgrasVoorNatuurgras()
    {
        var slots = PlannerShared.FindAllSlots(
            new List<VeldBeschikbaarheidInfo> { Veld(1), Veld(2) },
            new List<BestaandeWedstrijd>(),
            new List<VeldInfo>
            {
                new() { VeldNummer = 1, VeldNaam = "veld 1", VeldType = "natuurgras" },
                VeldInfoKunstgras(2)
            },
            GeenTeamRegels, new List<TeamRegel>(),
            1.00m, 105, TimeOnly.Parse("08:30"), TimeOnly.Parse("22:00"), sunset: null);

        slots.Should().NotBeEmpty();
        slots[0].VeldNummer.Should().Be(2, "kunstgras wordt eerst ingezet om grasvelden te ontlasten");
    }

    [Fact]
    public void FindAllSlots_DagdeelFilter_BeperktTotVenster()
    {
        var slots = PlannerShared.FindAllSlots(
            new List<VeldBeschikbaarheidInfo> { Veld(1, "09:00", "22:00") },
            new List<BestaandeWedstrijd>(),
            new List<VeldInfo> { VeldInfoKunstgras(1) },
            GeenTeamRegels, new List<TeamRegel>(),
            1.00m, 60, TimeOnly.Parse("17:00"), TimeOnly.Parse("22:00"), sunset: null);

        slots.Should().NotBeEmpty();
        slots.Should().OnlyContain(s => s.AanvangsTijd >= TimeOnly.Parse("17:00"));
    }

    // ── Doordeweekse waarschuwing (#576) ──

    [Theory]
    [InlineData("2026-09-07")] // maandag
    [InlineData("2026-09-08")] // dinsdag
    [InlineData("2026-09-09")] // woensdag
    [InlineData("2026-09-10")] // donderdag
    public void Waarschuwing_Doordeweeks_BevatGeenVasteVeldnummers(string datum)
    {
        var date = DateOnly.Parse(datum);

        var response = new CheckAvailabilityResponse();
        PlannerShared.AddWeekdayWarning(response.Waarschuwingen, date);

        var lijst = new List<string>();
        PlannerShared.AddWeekdayWarning(lijst, date);

        response.Waarschuwingen.Should().HaveCount(1);
        lijst.Should().HaveCount(1);
        // Beide overloads geven exact dezelfde, clubneutrale tekst (#576)
        lijst[0].Should().Be(response.Waarschuwingen[0]);

        foreach (var tekst in lijst)
        {
            tekst.Should().NotMatchRegex(@"veld\s*\d",
                "waarschuwing mag geen vaste veldnummers aannemen — velden komen uit dbo.VeldBeschikbaarheid");
            tekst.Should().Contain("veldbeschikbaarheid");
        }
    }

    [Theory]
    [InlineData("2026-09-11")] // vrijdag
    [InlineData("2026-09-12")] // zaterdag
    [InlineData("2026-09-13")] // zondag
    public void Waarschuwing_Weekend_GeenDoordeweekseWaarschuwing(string datum)
    {
        var date = DateOnly.Parse(datum);
        var lijst = new List<string>();

        PlannerShared.AddWeekdayWarning(lijst, date);

        lijst.Should().BeEmpty();
        PlannerShared.IsWeekday(date).Should().BeFalse();
    }

    // ── RondAfOp5Min ──

    [Theory]
    [InlineData("10:01", "10:05")]
    [InlineData("10:00", "10:00")]
    [InlineData("10:14", "10:15")]
    [InlineData("09:58", "10:00")]
    public void RondAfOp5Min_RondtNaarBovenAf(string invoer, string verwacht)
    {
        PlannerShared.RondAfOp5Min(TimeOnly.Parse(invoer))
            .Should().Be(TimeOnly.Parse(verwacht));
    }

    // ── ValidateBevestigInterval (#1134, Codex-review #1107 bevinding 9) ──

    [Fact]
    public void ValidateBevestigInterval_PositieveDuurBinnenGrenzen_GeeftNull()
    {
        PlannerShared.ValidateBevestigInterval(TimeOnly.Parse("10:00"), 105).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-15)]
    public void ValidateBevestigInterval_NulOfNegatieveDuur_GeeftFoutmelding(int duurMinuten)
    {
        PlannerShared.ValidateBevestigInterval(TimeOnly.Parse("10:00"), duurMinuten)
            .Should().Be("Wedstrijdduur moet groter zijn dan 0 minuten.");
    }

    [Fact]
    public void ValidateBevestigInterval_DuurBovenMaximum_GeeftFoutmelding()
    {
        PlannerShared.ValidateBevestigInterval(TimeOnly.Parse("10:00"), PlannerShared.MaxWedstrijdDuurMinuten + 1)
            .Should().Contain("onwaarschijnlijk groot");
    }

    [Fact]
    public void ValidateBevestigInterval_EindtijdOverschrijdtDeDag_GeeftFoutmelding()
    {
        // 23:00 + 90 min zou zonder deze check stilzwijgend naar 00:30 de volgende dag wrappen
        // (TimeOnly.AddMinutes kent geen dag-grens) — een eindtijd vóór de aanvangstijd.
        PlannerShared.ValidateBevestigInterval(TimeOnly.Parse("23:00"), 90)
            .Should().Be("Aanvangstijd plus wedstrijdduur overschrijdt het einde van de dag.");
    }

    [Fact]
    public void ValidateBevestigInterval_EindtijdPreciesMiddernacht_IsNogGeldig()
    {
        PlannerShared.ValidateBevestigInterval(TimeOnly.Parse("22:00"), 120).Should().BeNull();
    }

    // ── FindBezettingsConflict (#1134, Codex-review #1107 bevinding 9) ──

    [Fact]
    public void FindBezettingsConflict_LeegVeld_GeenConflict()
    {
        PlannerShared.FindBezettingsConflict(
                TimeOnly.Parse("10:00"), TimeOnly.Parse("11:00"), 1.00m, 1, new List<BestaandeWedstrijd>())
            .Should().BeNull();
    }

    [Fact]
    public void FindBezettingsConflict_OverlappendeVolledigeVeldreserveringen_GeeftConflict()
    {
        // Reproductie van Codex-review #1107 bevinding 9: 10:00–12:00 en 10:30–12:30 op hetzelfde
        // veld overlappen en mogen dus niet allebei mogen slagen.
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "12:00") };

        PlannerShared.FindBezettingsConflict(TimeOnly.Parse("10:30"), TimeOnly.Parse("12:30"), 1.00m, 1, occs)
            .Should().NotBeNull();
    }

    [Fact]
    public void FindBezettingsConflict_AansluitendeReserveringen_GeenConflict()
    {
        // Acceptatiecriterium #1134: 10:00–11:00 gevolgd door 11:00–12:00 raakt elkaar precies aan
        // — dat is GEEN conflict. Bewust een ander regime dan CanFitMatch, die hier wél een buffer
        // zou eisen omdat die methode nieuwe sloten voorstelt in plaats van een gekozen tijd bevestigt.
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:00") };

        PlannerShared.FindBezettingsConflict(TimeOnly.Parse("11:00"), TimeOnly.Parse("12:00"), 1.00m, 1, occs)
            .Should().BeNull();
    }

    [Fact]
    public void FindBezettingsConflict_AnderVeld_GeenConflict()
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(2, "10:00", "12:00") };

        PlannerShared.FindBezettingsConflict(TimeOnly.Parse("10:30"), TimeOnly.Parse("12:30"), 1.00m, 1, occs)
            .Should().BeNull();
    }

    [Fact]
    public void FindBezettingsConflict_TweeHalveVeldenBinnenCapaciteit_GeenConflict()
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:00", deel: 0.50m) };

        PlannerShared.FindBezettingsConflict(TimeOnly.Parse("10:00"), TimeOnly.Parse("11:00"), 0.50m, 1, occs)
            .Should().BeNull("twee halve velden mogen naast elkaar zolang de som binnen 1.00 blijft");
    }

    [Fact]
    public void FindBezettingsConflict_DrieHalveVeldenBovenCapaciteit_GeeftConflict()
    {
        var occs = new List<BestaandeWedstrijd>
        {
            Bezetting(1, "10:00", "11:00", deel: 0.50m),
            Bezetting(1, "10:00", "11:00", deel: 0.50m)
        };

        PlannerShared.FindBezettingsConflict(TimeOnly.Parse("10:00"), TimeOnly.Parse("11:00"), 0.50m, 1, occs)
            .Should().NotBeNull("het veld is al volledig gedeeld door twee bestaande halve-veldreserveringen");
    }

    [Fact]
    public void FindBezettingsConflict_GedeeldVeldOverlaptVolledigeVeldbezetting_GeeftConflict()
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:00", deel: 1.00m) };

        PlannerShared.FindBezettingsConflict(TimeOnly.Parse("10:00"), TimeOnly.Parse("11:00"), 0.50m, 1, occs)
            .Should().NotBeNull("een volledige-veldbezetting laat geen ruimte voor een gedeeld verzoek, ongeacht de eigen fractie");
    }
}
