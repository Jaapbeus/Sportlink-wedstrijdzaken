using FluentAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// Regressietests voor het faalscenario dat een dubbele boeking op hetzelfde veld opleverde (#707).
///
/// <para><b>Het scenario:</b> een club met twaalf velden ("veld 1" … "veld 12") en een wedstrijd op
/// "veld 10" om 12:00 die herpland wordt.</para>
///
/// <para>De matcher in het herplanpad loste "veld 10" correct op naar veldnummer 10, maar de
/// bezettingsopbouw kapte de veldnaam af op zes tekens ("veld 10" → "veld 1"). Twee gevolgen:</para>
/// <list type="bullet">
///   <item><b>A — spookbezetting.</b> De uitsluiting van de eigen wedstrijd matchte op
///   (veldnummer, aanvangstijd, wedstrijdnaam) en vond niets meer, want de bezetting stond op
///   veld 1 en de matcher zocht op veld 10. De eigen wedstrijd bleef dus als bezetting op veld 1
///   staan en blokkeerde daar een veld waar ze niet eens speelde.</item>
///   <item><b>B — dubbele boeking.</b> Veld 10 kwam in de bezetting helemaal niet voor en leek
///   daardoor de hele dag vrij. Een tweede wedstrijd kon vlak naast de bestaande op hetzelfde
///   veld worden aangeboden.</item>
/// </list>
///
/// <para>De tests hieronder combineren de drie échte productie-eenheden van dat pad:
/// <see cref="PlannerShared.ResolveVeld(string?, IReadOnlyDictionary{string, int})"/> (de
/// veld-mapping die <c>SportlinkApiClient</c> gebruikt),
/// <see cref="PlannerAvailabilityRepository.FilterExcludingWedstrijdcode"/> (de uitsluiting) en
/// <see cref="PlannerShared.CanFitMatch"/> / <see cref="PlannerShared.FindAllSlots"/> (de
/// slotberekening). Alleen HTTP en SQL zijn weggelaten.</para>
/// </summary>
public class VeldBezettingHerplanTests
{
    private const int AantalVelden = 12;
    private const long EigenWedstrijdcode = 19816434;
    private const long AndereWedstrijdcode = 19816435;
    private const int DuurMinuten = 105;

    private static readonly DateOnly Datum = new(2026, 9, 12);

    /// <summary>Veldnaam → veldnummer, zoals <c>PlannerSettingsRepository.GetVeldenLookupAsync</c> levert.</summary>
    private static Dictionary<string, int> VeldenLookup(int aantal = AantalVelden) =>
        Enumerable.Range(1, aantal).ToDictionary(n => $"veld {n}", n => n, StringComparer.OrdinalIgnoreCase);

    private static List<VeldInfo> Velden(int aantal = AantalVelden) =>
        Enumerable.Range(1, aantal)
            .Select(n => new VeldInfo { VeldNummer = n, VeldNaam = $"veld {n}", VeldType = "kunstgras" })
            .ToList();

    private static List<VeldBeschikbaarheidInfo> Beschikbaarheid(int aantal = AantalVelden) =>
        Enumerable.Range(1, aantal)
            .Select(n => new VeldBeschikbaarheidInfo
            {
                VeldNummer = n,
                BeschikbaarVanaf = TimeOnly.Parse("09:00"),
                BeschikbaarTot = TimeOnly.Parse("18:00")
            })
            .ToList();

    /// <summary>
    /// Bouwt één bezettingsregel exact zoals <c>SportlinkApiClient</c> dat doet: de Sportlink-veldstring
    /// wordt via de gedeelde matcher naar veldnummer + subpositie omgezet.
    /// </summary>
    private static BestaandeWedstrijd Bezetting(
        string sportlinkVeld, string aanvang, long? wedstrijdcode, string wedstrijd,
        Dictionary<string, int>? lookup = null)
    {
        var (veldNummer, subpositie) = PlannerShared.ResolveVeld(sportlinkVeld, lookup ?? VeldenLookup());
        var start = TimeOnly.Parse(aanvang);
        return new BestaandeWedstrijd
        {
            Datum = Datum,
            AanvangsTijd = start,
            EindTijd = start.AddMinutes(DuurMinuten),
            VeldNummer = veldNummer,
            VeldDeelGebruik = 1.00m,
            VeldSubpositie = subpositie,
            Wedstrijd = wedstrijd,
            Wedstrijdcode = wedstrijdcode,
            Bron = "API"
        };
    }

    private static readonly Dictionary<string, List<TeamRegel>> GeenTeamRegels = new();

    private static bool PastOpVeld(int veldNummer, string van, List<BestaandeWedstrijd> occupations) =>
        PlannerShared.CanFitMatch(
            TimeOnly.Parse(van), TimeOnly.Parse(van).AddMinutes(DuurMinuten), 1.00m, veldNummer,
            occupations.Where(o => o.VeldNummer == veldNummer).ToList(),
            GeenTeamRegels, new List<TeamRegel>());

    // ── De veld-mapping zelf ──

    [Fact]
    public void Bezetting_WedstrijdOpVeld10_LandtOpVeld10_NietOpVeld1()
    {
        // Kern van gevolg B: met de afkap op zes tekens werd dit veldnummer 1 en bleef veld 10 leeg.
        var (veldNummer, subpositie) = PlannerShared.ResolveVeld("veld 10", VeldenLookup());

        veldNummer.Should().Be(10);
        subpositie.Should().BeNull("\"veld 10\" heeft geen subpositie — de oude afkap las hier \"0\"");
    }

    [Fact]
    public void Bezetting_VeldnaamLangerDanZesTekens_ValtNietUitDeBezetting()
    {
        // Een veldnaam van meer dan zes tekens werd afgekapt tot een sleutel die niet bestond;
        // de lookup miste en de wedstrijd verdween volledig uit de bezetting — zelfde dubbele
        // boeking, andere oorzaak.
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["hoofdveld"] = 1,
            ["bijveld noord"] = 2
        };

        PlannerShared.ResolveVeld("hoofdveld", lookup).VeldNummer.Should().Be(1);
        PlannerShared.ResolveVeld("bijveld noord B", lookup).Should().Be((2, "B"));
    }

    [Fact]
    public void Matcher_EnBezetting_KiezenAltijdHetzelfdeVeld()
    {
        // Anti-drift-invariant: het herplanpad (VeldInfo-lijst) en de bezettingsopbouw
        // (naam→nummer-lookup) moeten per definitie tot hetzelfde veldnummer komen. Liepen ze
        // uiteen, dan viel de uitsluiting van de eigen wedstrijd stil.
        var velden = Velden();
        var lookup = VeldenLookup();

        foreach (var sportlinkVeld in velden.Select(v => v.VeldNaam)
                     .Concat(velden.Select(v => $"{v.VeldNaam} A"))
                     .Concat(["veld 13", "trainingsveld", ""]))
        {
            PlannerShared.VindVeldNummer(sportlinkVeld, velden)
                .Should().Be(PlannerShared.ResolveVeld(sportlinkVeld, lookup).VeldNummer,
                    $"matcher en bezetting mogen niet uiteenlopen op '{sportlinkVeld}'");
        }
    }

    // ── Gevolg A: geen spookbezetting op veld 1 ──

    [Fact]
    public void Herplan_EigenWedstrijdOpVeld10_WordtUitgeslotenUitDeBezetting()
    {
        var bezetting = new List<BestaandeWedstrijd>
        {
            Bezetting("veld 10", "12:00", EigenWedstrijdcode, "Eigen team - Tegenstander"),
            Bezetting("veld 3",  "12:00", AndereWedstrijdcode, "Ander team - Tegenstander")
        };

        var resultaat = PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(bezetting, EigenWedstrijdcode);

        resultaat.Should().ContainSingle().Which.Wedstrijdcode.Should().Be(AndereWedstrijdcode);
        resultaat.Should().NotContain(o => o.VeldNummer == 10, "de eigen wedstrijd is uitgesloten");
    }

    [Fact]
    public void Herplan_EigenWedstrijdOpVeld10_LaatGeenSpookbezettingOpVeld1Achter()
    {
        // Gevolg A: veld 1 werd de hele dag geblokkeerd door een wedstrijd die op veld 10 speelde.
        var bezetting = new List<BestaandeWedstrijd>
        {
            Bezetting("veld 10", "12:00", EigenWedstrijdcode, "Eigen team - Tegenstander")
        };

        var resultaat = PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(bezetting, EigenWedstrijdcode);

        resultaat.Should().BeEmpty();
        PastOpVeld(1, "12:00", resultaat).Should().BeTrue("veld 1 is vrij — daar speelt niets");

        var slots = PlannerShared.FindAllSlots(
            Beschikbaarheid(), resultaat, Velden(), GeenTeamRegels, new List<TeamRegel>(),
            1.00m, DuurMinuten, TimeOnly.Parse("08:30"), TimeOnly.Parse("22:00"), sunset: null);

        // Veld 1 moet precies zo vrij zijn als elk ander onaangeroerd veld. Bleef de eigen
        // wedstrijd als spookbezetting op veld 1 staan, dan levert veld 1 minder slots op.
        slots.Count(s => s.VeldNummer == 1).Should().Be(
            slots.Count(s => s.VeldNummer == 2),
            "veld 1 is even vrij als veld 2 — er speelt op geen van beide iets");
        slots.Select(s => s.VeldNummer).Distinct().Should().HaveCount(AantalVelden,
            "elk beschikbaar veld levert alternatieven op");
    }

    // ── Gevolg B: veld 10 is niet vrij zolang er een andere wedstrijd op staat ──

    [Fact]
    public void Herplan_AndereWedstrijdOpVeld10_BlijftVeld10Blokkeren()
    {
        // Dit is de dubbele boeking. De eigen wedstrijd staat om 12:00 op veld 10, een andere
        // wedstrijd om 15:00 op hetzelfde veld. Na uitsluiting van de eigen wedstrijd mag veld 10
        // niet als volledig vrij gelden: de wedstrijd van 15:00 blijft staan.
        var bezetting = new List<BestaandeWedstrijd>
        {
            Bezetting("veld 10", "12:00", EigenWedstrijdcode,  "Eigen team - Tegenstander"),
            Bezetting("veld 10", "15:00", AndereWedstrijdcode, "Ander team - Tegenstander")
        };

        var resultaat = PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(bezetting, EigenWedstrijdcode);

        resultaat.Should().ContainSingle().Which.VeldNummer.Should().Be(
            10, "de andere wedstrijd hoort op veld 10 te staan, niet op veld 1");

        // 15:00–16:45 is bezet; 15:05 ernaast mag nooit passen.
        PastOpVeld(10, "15:00", resultaat).Should().BeFalse("veld 10 is om 15:00 bezet");
        PastOpVeld(10, "15:05", resultaat).Should().BeFalse("vijf minuten ernaast is dezelfde dubbele boeking");
        PastOpVeld(10, "14:00", resultaat).Should().BeFalse("binnen de buffer vóór de bezetting");

        // En veld 1 blijft juist wél vrij — daar staat niets.
        PastOpVeld(1, "15:00", resultaat).Should().BeTrue();
    }

    [Fact]
    public void Herplan_SlotsOpVeld10_RespecterenDeAndereWedstrijd()
    {
        var bezetting = new List<BestaandeWedstrijd>
        {
            Bezetting("veld 10", "12:00", EigenWedstrijdcode,  "Eigen team - Tegenstander"),
            Bezetting("veld 10", "15:00", AndereWedstrijdcode, "Ander team - Tegenstander")
        };
        var resultaat = PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(bezetting, EigenWedstrijdcode);

        var slots = PlannerShared.FindAllSlots(
            Beschikbaarheid(), resultaat, Velden(), GeenTeamRegels, new List<TeamRegel>(),
            1.00m, DuurMinuten, TimeOnly.Parse("08:30"), TimeOnly.Parse("22:00"), sunset: null);

        var opVeld10 = slots.Where(s => s.VeldNummer == 10).ToList();

        opVeld10.Should().NotBeEmpty("het eigen slot van 12:00 is vrijgekomen, dus veld 10 heeft ruimte");
        opVeld10.Should().OnlyContain(
            s => s.EindTijd <= TimeOnly.Parse("14:45") || s.AanvangsTijd >= TimeOnly.Parse("17:00"),
            "geen enkel aangeboden slot op veld 10 mag de wedstrijd van 15:00–16:45 (plus buffer) raken");
    }

    [Fact]
    public void Herplan_EigenSlotWordtNietAlsAlternatiefAangeboden()
    {
        // RescheduleService filtert het eigen slot uit de kandidaten met het veldnummer uit de
        // matcher. Dat nummer moet dus het veld van de eigen wedstrijd zijn (10, niet 1), anders
        // krijgt de aanvrager zijn huidige tijdslot als "alternatief" terug.
        var matchVeldNr = PlannerShared.VindVeldNummer("veld 10", Velden());
        var matchStart = TimeOnly.Parse("12:00");

        matchVeldNr.Should().Be(10, "de eigen wedstrijd staat op veld 10");

        var kandidaten = new List<CandidateSlot>
        {
            new() { VeldNummer = 10, AanvangsTijd = matchStart,                EindTijd = matchStart.AddMinutes(DuurMinuten) },
            new() { VeldNummer = 1,  AanvangsTijd = matchStart,                EindTijd = matchStart.AddMinutes(DuurMinuten) },
            new() { VeldNummer = 10, AanvangsTijd = TimeOnly.Parse("14:00"),   EindTijd = TimeOnly.Parse("15:45") }
        };

        // Exact het filter uit RescheduleService.CheckRescheduleAvailabilityAsync.
        var gefilterd = kandidaten
            .Where(c => !(c.VeldNummer == matchVeldNr && c.AanvangsTijd == matchStart))
            .ToList();

        gefilterd.Should().NotContain(c => c.VeldNummer == 10 && c.AanvangsTijd == matchStart,
            "het eigen tijdslot is geen alternatief");
        gefilterd.Should().Contain(c => c.VeldNummer == 1 && c.AanvangsTijd == matchStart,
            "hetzelfde tijdstip op een ánder veld is juist een geldig alternatief — met veldnummer 1 " +
            "uit de oude matcher werd precies dit alternatief weggefilterd");
        gefilterd.Should().Contain(c => c.VeldNummer == 10 && c.AanvangsTijd == TimeOnly.Parse("14:00"),
            "een andere tijd op het eigen veld blijft een alternatief");
    }
}
