using Planner.Shared;
using FluentAssertions;
using SportlinkFunction.Email;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Regressietests voor issue #705: het automatische antwoord selecteerde alternatieven op basis van
/// een gok over het veldnummer (1-4 = kunstgras, 5 = gras) in plaats van op het werkelijke
/// <c>VeldType</c> uit <c>dbo.Velden</c>.
///
/// Bij een club met natuurgras op de lage nummers en kunstgras op de hoge zag die logica drie
/// "kunstgras"-slots op de natuurgrasvelden en gooide vervolgens alle échte kunstgrasvelden weg —
/// het antwoord verzweeg dan beschikbare velden. De veldnummering geldt maar voor één accommodatie
/// en is dus een club-specifieke aanname in code.
///
/// Tweede eis: een onbekend (leeg/ontbrekend) veldtype mag nooit tot wegfilteren leiden. Zo'n slot
/// is aantoonbaar beschikbaar; niet vermelden is schadelijker dan één optie te veel aanbieden.
///
/// Derde eis (#707): er is precies één definitie van "kunstgras" (<see cref="VeldTypeClassificatie"/>),
/// en het veldtype reist mee op élk pad — ook het herplan-pad, dat het eerder niet vulde waardoor het
/// filter daar stilzwijgend op <c>null</c> werkte.
/// </summary>
public class BerichtResponseGeneratorVeldTypeTests
{
    private const string Kunstgras = "kunstgras";
    private const string Natuurgras = "natuurgras";

    /// <summary>Voetnoot-only snapshot: houdt de handtekening buiten deze tests (zie #677).</summary>
    private static readonly ClubAppSettingsSnapshot ClubSettings = new(
        PlannerAfzenderNaam: null,
        CoordinatorNaam: null,
        CoordinatorFunctie: null,
        EmailVoetnoot: "Testomgeving",
        HerplanDeadlineDagen: null);

    private static InkomendBericht MaakEmail() => new()
    {
        MessageId = "veldtype-test",
        Afzender = "trainer@voorbeeld.nl",
        AfzenderNaam = "Jan de Vries",
        Onderwerp = "Beschikbaarheid",
        OntvangstDatum = DateTime.UtcNow,
        Body = "Is er ruimte?"
    };

    private static BerichtClassificatie MaakClassificatie(string? aanvangsTijd = null) => new()
    {
        Type = VerzoekType.BeschikbaarheidCheck,
        Datum = "2026-09-12",
        AanvangsTijd = aanvangsTijd
    };

    private static SlotToewijzing Slot(int veldNummer, string? veldType, string aanvangsTijd) => new()
    {
        Datum = "2026-09-12",
        AanvangsTijd = aanvangsTijd,
        EindTijd = TimeOnly.Parse(aanvangsTijd).AddMinutes(105).ToString("HH:mm"),
        VeldNummer = veldNummer,
        VeldNaam = $"veld {veldNummer}",
        VeldType = veldType,
        VeldDeelGebruik = 1.00m,
        WedstrijdDuurMinuten = 105
    };

    /// <summary>
    /// Bouwt een slot exact zoals het herplan-pad dat doet: via
    /// <see cref="PlannerShared.ToSlotToewijzing"/> met de veldenlijst uit dbo.Velden. Zo test dit
    /// bestand het gedrag van RescheduleService zonder dat bestand aan te raken.
    /// </summary>
    private static SlotToewijzing HerplanSlot(int veldNummer, string aanvangsTijd, List<VeldInfo> velden)
    {
        var aanvang = TimeOnly.Parse(aanvangsTijd);
        var kandidaat = new CandidateSlot
        {
            VeldNummer = veldNummer,
            AanvangsTijd = aanvang,
            EindTijd = aanvang.AddMinutes(105),
            VeldFractie = 1.00m
        };
        return PlannerShared.ToSlotToewijzing(new DateOnly(2026, 9, 12), kandidaat, 105, velden);
    }

    private static BeschikbaarVenster Venster(int veldNummer, string? veldType, string van, string tot) => new()
    {
        VeldNummer = veldNummer,
        VeldNaam = $"veld {veldNummer}",
        VeldType = veldType,
        Van = van,
        Tot = tot,
        MaxDuurMinuten = (int)(TimeOnly.Parse(tot) - TimeOnly.Parse(van)).TotalMinutes
    };

    // ── Alternatieven ──

    [Fact]
    public void Alternatieven_NatuurgrasOpLageNummers_HoudtDeEchteKunstgrasvelden()
    {
        // Accommodatie met natuurgras op 1-3 en kunstgras op 5-7 — precies omgekeerd aan de
        // nummer-aanname die hier eerder stond.
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            Alternatieven =
            [
                Slot(1, Natuurgras, "10:00"),
                Slot(2, Natuurgras, "11:00"),
                Slot(3, Natuurgras, "12:00"),
                Slot(5, Kunstgras,  "13:00"),
                Slot(6, Kunstgras,  "14:00"),
                Slot(7, Kunstgras,  "15:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie("09:00"), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 5").And.Contain("veld 6").And.Contain("veld 7");
        body.Should().NotContain("veld 1").And.NotContain("veld 2").And.NotContain("veld 3");
    }

    [Fact]
    public void Alternatieven_MinderDanDrieKunstgrasvelden_HoudtNatuurgrasAlsAanbod()
    {
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            Alternatieven =
            [
                Slot(5, Kunstgras,  "10:00"),
                Slot(6, Kunstgras,  "11:00"),
                Slot(1, Natuurgras, "12:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie("09:00"), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 1");
    }

    [Fact]
    public void Alternatieven_OnbekendVeldType_WordtNooitWeggefilterd()
    {
        // Kanalen die het veldtype (nog) niet meegeven, zoals het herplan-pad: zonder bekend type
        // mag er niets verdwijnen. De nummer-aanname zou veld 5 hier hebben weggegooid omdat
        // 1, 2 en 3 als "kunstgras" golden.
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            Alternatieven =
            [
                Slot(5, null, "10:00"),
                Slot(1, null, "11:00"),
                Slot(2, null, "12:00"),
                Slot(3, null, "13:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie("09:00"), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 5");
    }

    [Fact]
    public void Alternatieven_OnbekendVeldTypeNaastDrieKunstgrasvelden_BlijftStaan()
    {
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            Alternatieven =
            [
                Slot(9, null,      "10:00"),
                Slot(5, Kunstgras, "11:00"),
                Slot(6, Kunstgras, "12:00"),
                Slot(7, Kunstgras, "13:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie("09:00"), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 9");
    }

    // ── Beschikbare vensters ──

    [Fact]
    public void Vensters_NatuurgrasOpLageNummers_HoudtDeEchteKunstgrasvensters()
    {
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            BeschikbareVensters =
            [
                Venster(1, Natuurgras, "09:00", "12:00"),
                Venster(5, Kunstgras,  "09:00", "12:00"),
                Venster(6, Kunstgras,  "09:00", "12:00"),
                Venster(7, Kunstgras,  "09:00", "12:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie("13:00"), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 5").And.Contain("veld 6").And.Contain("veld 7");
        body.Should().NotContain("veld 1");
    }

    [Fact]
    public void Vensters_NatuurgrasInEigenTijdsblok_BlijftStaan()
    {
        // Toegevoegde waarde: het natuurgrasveld is in dit tijdsblok het enige aanbod.
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            BeschikbareVensters =
            [
                Venster(5, Kunstgras,  "09:00", "12:00"),
                Venster(6, Kunstgras,  "09:00", "12:00"),
                Venster(7, Kunstgras,  "09:00", "12:00"),
                Venster(1, Natuurgras, "14:00", "17:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie("13:00"), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 1");
    }

    [Fact]
    public void Vensters_OnbekendVeldType_BlijftStaanOokBijOverlap()
    {
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            BeschikbareVensters =
            [
                Venster(5, Kunstgras, "09:00", "12:00"),
                Venster(6, Kunstgras, "09:00", "12:00"),
                Venster(7, Kunstgras, "09:00", "12:00"),
                Venster(9, null,      "09:00", "12:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie("13:00"), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 9");
    }

    // ── Eén gedeelde kunstgras-definitie (#707) ──

    [Theory]
    [InlineData("kunstgras")]
    [InlineData("Kunstgras")]
    [InlineData("KUNSTGRAS 2")]
    [InlineData("kunstgrasveld")]
    [InlineData("kunst gras")]
    [InlineData("KG")]
    [InlineData("3G")]
    [InlineData("art. gras")]
    [InlineData("artificial grass")]
    public void Classificatie_KunstgrasVarianten_GeldenAlsKunstgras(string veldType)
    {
        VeldTypeClassificatie.Bepaal(veldType).Should().Be(VeldSoort.Kunstgras);
        VeldTypeClassificatie.IsNatuurgras(veldType).Should().BeFalse(
            "een kunstgrasvariant mag nooit als natuurgras gelden — dan kan hij worden weggefilterd");
    }

    [Theory]
    [InlineData("natuurgras")]
    [InlineData("Natuurgras")]
    [InlineData("gras")]
    [InlineData("grasveld")]
    [InlineData("natural grass")]
    public void Classificatie_NatuurgrasVarianten_GeldenAlsNatuurgras(string veldType)
    {
        VeldTypeClassificatie.Bepaal(veldType).Should().Be(VeldSoort.Natuurgras);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hybride")]
    [InlineData("hybrid turf")]
    [InlineData("semi-water")]
    [InlineData("toplaag onbekend")]
    public void Classificatie_NietTePlaatsen_GeldtAlsOnbekend(string? veldType)
    {
        VeldTypeClassificatie.Bepaal(veldType).Should().Be(VeldSoort.Onbekend);
        VeldTypeClassificatie.IsNatuurgras(veldType).Should().BeFalse(
            "onbekend is geen natuurgras — anders filtert de fail-safe alsnog weg");
    }

    [Fact]
    public void EenDefinitie_PlannerEnEmailfilter_GebruikenDezelfdeKunstgrasRegel()
    {
        // Vóór #707 stonden er twee regels naast elkaar: VeldInfo.IsKunstgras vergeleek exact en
        // case-sensitief ("kunstgras"), het e-mailantwoord deed een case-insensitieve Contains.
        // "Kunstgras 2" was daardoor kunstgras in de e-mail en natuurgras in de planner.
        const string variant = "Kunstgras 2";

        new VeldInfo { VeldNummer = 5, VeldNaam = "veld 5", VeldType = variant }
            .IsKunstgras.Should().BeTrue("de planner moet dezelfde definitie gebruiken als het e-mailantwoord");

        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            Alternatieven =
            [
                Slot(5, variant,     "10:00"),
                Slot(6, "KUNSTGRAS", "11:00"),
                Slot(7, "kunstgras", "12:00"),
                Slot(1, Natuurgras,  "13:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie("09:00"), MaakEmail(), ClubSettings);

        // Drie kunstgrasvelden gehaald ondanks de afwijkende schrijfwijzen → natuurgras vervalt.
        body.Should().Contain("veld 5").And.Contain("veld 6").And.Contain("veld 7");
        body.Should().NotContain("veld 1");
    }

    [Fact]
    public void Alternatieven_AfwijkendVeldType_WordtNooitWeggefilterd()
    {
        // "hybride" is noch kunstgras noch natuurgras. Fail-safe: onbekend blijft staan, ook als de
        // drempel van drie kunstgrasvelden gehaald is. Een substring-regel die alles zonder
        // "kunstgras" als zeker natuurgras behandelde, gooide zo'n veld wél weg.
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            Alternatieven =
            [
                Slot(9, "hybride",  "10:00"),
                Slot(5, Kunstgras,  "11:00"),
                Slot(6, Kunstgras,  "12:00"),
                Slot(7, Kunstgras,  "13:00"),
                Slot(1, Natuurgras, "14:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie("09:00"), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 9", "een onbekend veldtype mag nooit tot wegfilteren leiden");
    }

    [Fact]
    public void Vensters_AfwijkendVeldType_BlijftStaanOokBijOverlap()
    {
        var response = new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            BeschikbareVensters =
            [
                Venster(5, Kunstgras, "09:00", "12:00"),
                Venster(6, Kunstgras, "09:00", "12:00"),
                Venster(7, Kunstgras, "09:00", "12:00"),
                Venster(9, "hybride", "09:00", "12:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            response, MaakClassificatie("13:00"), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 9");
    }

    // ── Herplannen ──

    /// <summary>
    /// Gat 1 van #707, direct op de conversie die het herplan-pad gebruikt: RescheduleService bouwt
    /// zijn slots via <see cref="PlannerShared.ToSlotToewijzing"/>. Vulde die het veldtype niet, dan
    /// was <c>VeldType</c> op het hele herplan-pad <c>null</c> en filterde het e-mailantwoord daar
    /// op niets — zonder dat iets faalde.
    /// </summary>
    [Fact]
    public void ToSlotToewijzing_NeemtVeldTypeUitDeVeldenlijstMee()
    {
        var velden = new List<VeldInfo>
        {
            new() { VeldNummer = 1, VeldNaam = "veld 1", VeldType = Natuurgras },
            new() { VeldNummer = 5, VeldNaam = "veld 5", VeldType = Kunstgras }
        };

        HerplanSlot(1, "10:00", velden).VeldType.Should().Be(Natuurgras);
        HerplanSlot(5, "10:00", velden).VeldType.Should().Be(Kunstgras);

        // Veld dat niet (meer) in dbo.Velden staat → onbekend, nooit een gok.
        var onbekend = HerplanSlot(9, "10:00", velden);
        onbekend.VeldType.Should().BeNull();
        onbekend.VeldNaam.Should().Be("veld 9");
    }

    [Fact]
    public void Herplan_MetVeldtypeUitDeVeldenlijst_HoudtKunstgrasEnLaatNatuurgrasWeg()
    {
        // Zelfde opbouw als RescheduleService: slots via PlannerShared.ToSlotToewijzing. Natuurgras
        // staat hier op een laag nummer en kunstgras op hoge — precies de accommodatie waarop de oude
        // veldnummer-aanname de verkeerde velden weggooide.
        var velden = new List<VeldInfo>
        {
            new() { VeldNummer = 2, VeldNaam = "veld 2", VeldType = Natuurgras },
            new() { VeldNummer = 5, VeldNaam = "veld 5", VeldType = Kunstgras },
            new() { VeldNummer = 6, VeldNaam = "veld 6", VeldType = Kunstgras },
            new() { VeldNummer = 7, VeldNaam = "veld 7", VeldType = Kunstgras }
        };

        var wedstrijd = new ZoekWedstrijdResponse
        {
            Wedstrijdcode = 123456,
            Wedstrijd = "TEST JO14-1 - Ander JO14-1",
            Datum = "2026-09-12",
            AanvangsTijd = "14:00",
            EindTijd = "15:45",
            VeldNaam = "veld 4"
        };
        var opties = new HerplanCheckResponse
        {
            Beschikbaar = true,
            Alternatieven =
            [
                HerplanSlot(2, "09:00", velden),
                HerplanSlot(5, "10:00", velden),
                HerplanSlot(6, "11:00", velden),
                HerplanSlot(7, "12:00", velden)
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwHerplanAntwoord(
            wedstrijd, opties, MaakClassificatie(), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 5 om 10:00").And.Contain("veld 6 om 11:00").And.Contain("veld 7 om 12:00");
        body.Should().NotContain("veld 2",
            "met drie kunstgrasvelden hoeft het antwoord geen natuurgras aan te bieden — en dat kan het " +
            "alleen weten als het veldtype ook op het herplan-pad meereist");
    }

    [Fact]
    public void Herplan_MetTweeKunstgrasvelden_HoudtNatuurgrasAlsAanbod()
    {
        // Tegenproef op de test hierboven: onder de drempel mag het herplan-pad niets weglaten.
        var velden = new List<VeldInfo>
        {
            new() { VeldNummer = 2, VeldNaam = "veld 2", VeldType = Natuurgras },
            new() { VeldNummer = 5, VeldNaam = "veld 5", VeldType = Kunstgras },
            new() { VeldNummer = 6, VeldNaam = "veld 6", VeldType = Kunstgras }
        };

        var wedstrijd = new ZoekWedstrijdResponse
        {
            Wedstrijdcode = 123456,
            Wedstrijd = "TEST JO14-1 - Ander JO14-1",
            Datum = "2026-09-12",
            AanvangsTijd = "14:00",
            EindTijd = "15:45",
            VeldNaam = "veld 4"
        };
        var opties = new HerplanCheckResponse
        {
            Beschikbaar = true,
            Alternatieven =
            [
                HerplanSlot(2, "09:00", velden),
                HerplanSlot(5, "10:00", velden),
                HerplanSlot(6, "11:00", velden)
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwHerplanAntwoord(
            wedstrijd, opties, MaakClassificatie(), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 2 om 09:00");
    }

    [Fact]
    public void Herplan_ZonderBekendVeldType_ToontAlleZinvolleAlternatieven()
    {
        // Het herplan-pad vult het veldtype nog niet; dan mag de filtering niets weggooien.
        var wedstrijd = new ZoekWedstrijdResponse
        {
            Wedstrijdcode = 123456,
            Wedstrijd = "TEST JO14-1 - Ander JO14-1",
            Datum = "2026-09-12",
            AanvangsTijd = "14:00",
            EindTijd = "15:45",
            VeldNaam = "veld 1"
        };
        var opties = new HerplanCheckResponse
        {
            Beschikbaar = true,
            Alternatieven =
            [
                Slot(5, null, "09:00"),
                Slot(1, null, "10:00"),
                Slot(2, null, "11:00"),
                Slot(3, null, "12:00")
            ]
        };

        var (_, body) = BerichtResponseGenerator.BouwHerplanAntwoord(
            wedstrijd, opties, MaakClassificatie(), MaakEmail(), ClubSettings);

        body.Should().Contain("veld 5 om 09:00");
    }
}
