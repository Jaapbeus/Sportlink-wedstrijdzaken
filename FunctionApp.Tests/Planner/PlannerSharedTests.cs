using Planner.Shared;
using AwesomeAssertions;
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

    // Karakterisatie (#1625): leggen vast hoe CanFitMatch buffers van aanvrager en bezetter combineert.

    private static List<TeamRegel> Regels(string regelType, int? minuten) =>
        new() { new TeamRegel { TeamNaam = "TEST JO14-1", RegelType = regelType, WaardeMinuten = minuten } };

    private static bool Past(string start, string einde, decimal fractie,
        List<BestaandeWedstrijd> occs, Dictionary<string, List<TeamRegel>>? allRules = null,
        List<TeamRegel>? eigen = null) =>
        PlannerShared.CanFitMatch(
            TimeOnly.Parse(start), TimeOnly.Parse(einde), fractie, 1,
            occs, allRules ?? GeenTeamRegels, eigen ?? new List<TeamRegel>());

    [Theory]
    [InlineData(45, true)]   // 11:00 + 45 = 11:45, vóór de bezetter van 12:00
    [InlineData(60, true)]   // precies 12:00: grens, geen overlap
    [InlineData(61, false)]  // 12:01 raakt de bezetter
    public void CanFitMatch_BufferNaVanAanvrager_Verruimt(int minuten, bool verwacht)
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "12:00", "13:00") };

        Past("10:00", "11:00", 1.00m, occs, eigen: Regels("BufferNa", minuten)).Should().Be(verwacht);
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void CanFitMatch_BufferVoorVanAanvrager_Verruimt(int minuten, bool verwacht)
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:00") };

        Past("12:00", "13:00", 1.00m, occs, eigen: Regels("BufferVoor", minuten)).Should().Be(verwacht);
    }

    [Theory]
    [InlineData("BufferVoor", 45, "11:00", "12:30", false)] // beschermd gebied begint 12:15
    [InlineData("BufferVoor", 45, "11:00", "12:15", true)]
    [InlineData("BufferNa", 45, "14:30", "15:30", false)]   // beschermd gebied eindigt 14:45
    [InlineData("BufferNa", 45, "14:45", "15:45", true)]
    public void CanFitMatch_TeamRegelsVanBezetter_BepalenBeschermdGebied(
        string regelType, int minuten, string start, string einde, bool verwacht)
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "13:00", "14:00", team: "TEST JO14-1") };
        var allRules = new Dictionary<string, List<TeamRegel>> { ["TEST JO14-1"] = Regels(regelType, minuten) };

        Past(start, einde, 1.00m, occs, allRules).Should().Be(verwacht);
    }

    [Fact]
    public void CanFitMatch_RegelZonderWaarde_WordtGenegeerd()
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:00", team: "TEST JO14-1") };
        var allRules = new Dictionary<string, List<TeamRegel>> { ["TEST JO14-1"] = Regels("BufferNa", null) };

        // standaardbuffer 15: 11:15 mag precies, 11:14 niet; null verandert dat niet
        Past("11:15", "12:00", 1.00m, occs, allRules, Regels("BufferVoor", null)).Should().BeTrue();
        Past("11:14", "12:00", 1.00m, occs, allRules, Regels("BufferVoor", null)).Should().BeFalse();
    }

    [Fact]
    public void CanFitMatch_RegelKleinerDanStandaard_VerkleintBufferNiet()
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:00", team: "TEST JO14-1") };
        var allRules = new Dictionary<string, List<TeamRegel>> { ["TEST JO14-1"] = Regels("BufferNa", 5) };

        Past("11:14", "12:00", 1.00m, occs, allRules).Should().BeFalse();
    }

    [Fact]
    public void CanFitMatch_BezetterZonderTeamnaamOfRegels_GebruiktStandaardBuffer()
    {
        var zonderNaam = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:00") };
        var onbekend = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:00", team: "ONBEKEND") };
        var allRules = new Dictionary<string, List<TeamRegel>> { ["TEST JO14-1"] = Regels("BufferNa", 90) };

        Past("11:15", "12:00", 1.00m, zonderNaam, allRules).Should().BeTrue();
        Past("11:15", "12:00", 1.00m, onbekend, allRules).Should().BeTrue();
    }

    [Theory]
    [InlineData(1.00, 0.50)] // hele wedstrijd tegen deelveld-wedstrijd
    [InlineData(0.50, 1.00)] // deelveld-wedstrijd tegen hele wedstrijd
    public void CanFitMatch_GelijktijdigMetHeleOfDeelBezetting_PastNiet(double aanvraag, double bezetting)
    {
        var occs = new List<BestaandeWedstrijd> { Bezetting(1, "10:00", "11:15", deel: (decimal)bezetting) };

        Past("10:30", "11:45", (decimal)aanvraag, occs).Should().BeFalse();
    }

    [Fact]
    public void CanFitMatch_DeelveldCapaciteit_BepaaldPerTijdsblokHetMaximum()
    {
        // Twee halve bezettingen na elkaar (nooit tegelijk) laten ruimte voor een derde halve wedstrijd
        var sequentieel = new List<BestaandeWedstrijd>
        {
            Bezetting(1, "10:00", "10:30", deel: 0.50m),
            Bezetting(1, "10:30", "11:00", deel: 0.50m)
        };
        Past("10:00", "11:00", 0.50m, sequentieel).Should().BeTrue();

        // Overlappen er twee halve bezettingen binnen het slot, dan is het veld vol
        var overlap = new List<BestaandeWedstrijd>
        {
            Bezetting(1, "10:00", "10:30", deel: 0.50m),
            Bezetting(1, "10:20", "10:40", deel: 0.50m)
        };
        Past("10:15", "11:00", 0.50m, overlap).Should().BeFalse();
    }

    [Fact]
    public void CanFitMatch_Deelveld_BufferChecksVervallenBijGelijktijdigheid()
    {
        // Een gelijktijdige deelveld-bezetting slaat de bufferchecks over (continue); de tweede
        // bezetting direct erna (binnen standaardbuffer) blokkeert wel.
        var occs = new List<BestaandeWedstrijd>
        {
            Bezetting(1, "10:00", "11:00", deel: 0.50m),
            Bezetting(1, "11:05", "12:00", deel: 1.00m)
        };

        Past("10:00", "11:00", 0.50m, occs).Should().BeFalse();
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
