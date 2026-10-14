using AwesomeAssertions;
using Planner.Shared;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>
/// Gevallen van de gedeelde antwoordgenerator (#1758, slice 6 van epic #1716). Stonden per tier als
/// <c>BerichtResponseGenerator</c>; hier gelden ze voor beide tiers. De tier-omhulsels hebben alleen nog een test
/// van de instellingenbron (snapshot of globale cache) en de bestaande tests die via het omhulsel lopen.
/// </summary>
[Collection(BerichtAntwoordCollectie.Naam)]
public class BerichtAntwoordGeneratorTests
{
    private const string Datum = "2026-09-12";
    private static readonly string DatumTekst = AntwoordTekst.Datum(Datum);
    private static readonly TestInstellingen Voetnoot = TestInstellingen.AlleenVoetnoot();
    private static readonly TestAfzender Email = new();

    private static BerichtClassificatie Classificatie(string? aanvangsTijd = null, VerzoekType type = VerzoekType.BeschikbaarheidCheck) => new()
    {
        Type = type,
        Datum = Datum,
        AanvangsTijd = aanvangsTijd
    };

    private static SlotToewijzing Slot(int veld, string aanvang, string eind = "11:45", string? veldType = null) => new()
    {
        Datum = Datum,
        AanvangsTijd = aanvang,
        EindTijd = eind,
        VeldNummer = veld,
        VeldNaam = $"veld {veld}",
        VeldType = veldType
    };

    private static TestVenster Venster(int veld, string van, string tot, string? opmerking = null, string? veldType = null) => new()
    {
        VeldNummer = veld,
        VeldNaam = $"veld {veld}",
        Van = van,
        Tot = tot,
        Opmerking = opmerking,
        VeldType = veldType
    };

    private static TestWedstrijd Wedstrijd(string aanvang = "14:00", string? veld = "veld 4") => new()
    {
        Wedstrijd = "TEST JO14-1 - Ander JO14-1",
        Datum = Datum,
        AanvangsTijd = aanvang,
        EindTijd = "15:45",
        VeldNaam = veld
    };

    // ── Beschikbaarheid ──

    [Fact]
    public void Beschikbaar_MetToewijzing_NoemtVeldTijdEnEinde()
    {
        var response = new TestBeschikbaarheid { Beschikbaar = true, Toewijzing = Slot(5, "10:00") };

        var (onderwerp, body) = BerichtAntwoordGenerator.BouwBeschikbaarheidAntwoord(response, Classificatie("10:00"), Email, Voetnoot);

        onderwerp.Should().Be("Re: Beschikbaarheid");
        body.Should().Contain(" Jan,\n\n");
        body.Should().Contain($"Op {DatumTekst} is veld 5 beschikbaar om 10:00. De wedstrijd eindigt om 11:45.");
        body.Should().NotContain("diverse andere mogelijkheden");
        body.Should().EndWith("\n\nTestomgeving");
    }

    [Fact]
    public void Beschikbaar_MetVierVensters_MeldtDiverseMogelijkhedenEnWaarschuwingen()
    {
        var response = new TestBeschikbaarheid
        {
            Beschikbaar = true,
            Toewijzing = Slot(5, "10:00"),
            BeschikbareVensters = [Venster(1, "09:00", "12:00"), Venster(2, "09:00", "12:00"), Venster(3, "09:00", "12:00"), Venster(4, "09:00", "12:00")],
            Waarschuwingen = ["Eerste.", "Tweede."]
        };

        var (_, body) = BerichtAntwoordGenerator.BouwBeschikbaarheidAntwoord(response, Classificatie("10:00"), Email, Voetnoot);

        body.Should().Contain("De wedstrijd eindigt om 11:45. Er zijn op deze dag nog diverse andere mogelijkheden.");
        body.Should().Contain("\n\nLet op: Eerste. Tweede.");
    }

    [Fact]
    public void Beschikbaar_MetDrieVensters_MeldtGeenDiverseMogelijkheden()
    {
        var response = new TestBeschikbaarheid
        {
            Beschikbaar = true,
            Toewijzing = Slot(5, "10:00"),
            BeschikbareVensters = [Venster(1, "09:00", "12:00"), Venster(2, "09:00", "12:00"), Venster(3, "09:00", "12:00")]
        };

        var (_, body) = BerichtAntwoordGenerator.BouwBeschikbaarheidAntwoord(response, Classificatie("10:00"), Email, Voetnoot);

        body.Should().NotContain("diverse andere mogelijkheden");
    }

    [Fact]
    public void NietBeschikbaar_MetVoorkeurstijdEnVensters_ToontVenstersAlsAlternatief()
    {
        var response = new TestBeschikbaarheid
        {
            BeschikbareVensters = [Venster(5, "09:00", "21:00", "halve veld"), Venster(6, "13:00", "20:59")]
        };

        var (_, body) = BerichtAntwoordGenerator.BouwBeschikbaarheidAntwoord(response, Classificatie("10:00"), Email, Voetnoot);

        body.Should().Contain($"Op {DatumTekst} om 10:00 is helaas geen ruimte. Beschikbare mogelijkheden:\n"
            + "- veld 5: beschikbaar van 09:00 tot einde dag (halve veld)\n"
            + "- veld 6: beschikbaar van 13:00 tot 20:59\n"
            + "\nGeef een voorkeurstijd door, dan plannen we het in.");
    }

    [Fact]
    public void OpenVraag_ZonderVoorkeurstijd_ToontDeMogelijkheden()
    {
        var response = new TestBeschikbaarheid { BeschikbareVensters = [Venster(5, "09:00", "12:00")] };

        var (_, body) = BerichtAntwoordGenerator.BouwBeschikbaarheidAntwoord(response, Classificatie(), Email, Voetnoot);

        body.Should().Contain($"Op {DatumTekst} zijn de volgende mogelijkheden:\n- veld 5: beschikbaar van 09:00 tot 12:00\n");
    }

    [Fact]
    public void NietBeschikbaar_MetAlternatieven_ToontHoogstensDrieEnDeWaarschuwing()
    {
        var response = new TestBeschikbaarheid
        {
            Alternatieven = [Slot(1, "10:00"), Slot(2, "11:00"), Slot(3, "12:00"), Slot(4, "13:00")],
            Waarschuwingen = ["Let goed op."]
        };

        var (_, body) = BerichtAntwoordGenerator.BouwBeschikbaarheidAntwoord(response, Classificatie("09:00"), Email, Voetnoot);

        body.Should().Contain($"Op {DatumTekst} om 09:00 is helaas geen ruimte. Alternatieven:\n"
            + "- veld 1 om 10:00 (eindigt 11:45)\n- veld 2 om 11:00 (eindigt 11:45)\n- veld 3 om 12:00 (eindigt 11:45)\n"
            + "\nLet op: Let goed op.");
        body.Should().NotContain("veld 4");
    }

    [Fact]
    public void TeamConflict_NoemtDeRedenEnGeenVeldtekst()
    {
        var response = new TestBeschikbaarheid { HeeftTeamConflict = true, Reden = "Het team speelt al om 10:00." };

        var (_, body) = BerichtAntwoordGenerator.BouwBeschikbaarheidAntwoord(response, Classificatie(), Email, Voetnoot);

        body.Should().Contain($"Het team speelt al om 10:00.\n\nHierdoor kan op {DatumTekst} geen oefenwedstrijd worden ingepland.");
    }

    [Fact]
    public void GeenRuimteEnGeenAlternatief_GebruiktDeDagdeelZin()
    {
        var response = new TestBeschikbaarheid { Reden = "Alles bezet.", GecontroleerdDagdeel = "ochtend" };

        var (_, body) = BerichtAntwoordGenerator.BouwBeschikbaarheidAntwoord(response, Classificatie(), Email, Voetnoot);

        body.Should().Contain(DagdeelVenster.GeenVeldZin(DatumTekst, "ochtend", "Alles bezet."));
    }

    // ── Meerdere datums ──

    [Fact]
    public void MultiDatum_BouwtPerDatumEenSectieEnNoemtDeCoordinator()
    {
        var resultaten = new List<(string, IAntwoordBeschikbaarheid)>
        {
            ("2026-09-12", new TestBeschikbaarheid { Beschikbaar = true, Toewijzing = Slot(5, "10:00"), Waarschuwingen = ["Kort."] }),
            ("2026-09-19", new TestBeschikbaarheid { HeeftTeamConflict = true, Reden = "Al een wedstrijd." }),
            ("2026-09-26", new TestBeschikbaarheid { Alternatieven = [Slot(2, "13:00")] }),
        };
        var instellingen = new TestInstellingen { EmailVoetnoot = "Voet", CoordinatorNaam = "Frenkie" };

        var (_, body) = BerichtAntwoordGenerator.BouwMultiDatumBeschikbaarheidAntwoord(resultaten, Classificatie("09:00"), Email, instellingen);

        body.Should().Contain($"**{AntwoordTekst.Datum("2026-09-12")}:** veld 5 is beschikbaar om 10:00 (eindigt 11:45). Let op: Kort.\n");
        body.Should().Contain($"**{AntwoordTekst.Datum("2026-09-19")}:** Al een wedstrijd. Hierdoor kan op deze dag geen oefenwedstrijd worden ingepland.\n");
        body.Should().Contain($"**{AntwoordTekst.Datum("2026-09-26")}:** Om 09:00 is helaas geen ruimte. Alternatieven:\n- veld 2 om 13:00 (eindigt 11:45)\n");
        body.Should().Contain("dan gaan we samen met Frenkie plannen en definitief opnemen in de planning.");
    }

    [Fact]
    public void MultiDatum_ZonderCoordinator_BlijftDeZinCompleet()
    {
        var resultaten = new List<(string, IAntwoordBeschikbaarheid)> { ("2026-09-12", new TestBeschikbaarheid { Reden = "Vol." }) };

        var (_, body) = BerichtAntwoordGenerator.BouwMultiDatumBeschikbaarheidAntwoord(resultaten, Classificatie(), Email, Voetnoot);

        body.Should().Contain("Laat weten welke optie(s) de voorkeur hebben, dan gaan we samen plannen en definitief opnemen in de planning.");
        body.Should().Contain(DagdeelVenster.GeenVeldRegel(DatumTekst, null, "Vol."));
    }

    [Fact]
    public void MultiDatum_VenstersZonderVoorkeurstijd_ToontDeVensters()
    {
        var resultaten = new List<(string, IAntwoordBeschikbaarheid)>
        {
            ("2026-09-12", new TestBeschikbaarheid { BeschikbareVensters = [Venster(7, "09:00", "12:00")] })
        };

        var (_, body) = BerichtAntwoordGenerator.BouwMultiDatumBeschikbaarheidAntwoord(resultaten, Classificatie(), Email, Voetnoot);

        body.Should().Contain($"**{DatumTekst}:** is helaas geen ruimte. Beschikbare mogelijkheden:\n- veld 7: beschikbaar van 09:00 tot 12:00\n");
    }

    // ── Herplannen ──

    [Fact]
    public void Herplan_ZonderWedstrijd_VraagtTeamEnDatumTeControleren()
    {
        var (_, body) = BerichtAntwoordGenerator.BouwHerplanAntwoord(null, null, Classificatie(), Email, Voetnoot);

        body.Should().Contain($"Er is geen wedstrijd gevonden voor het opgegeven team op {DatumTekst}. Controleer de teamnaam en datum en probeer het opnieuw.");
    }

    [Fact]
    public void Herplan_ZonderOpties_KanNietVerplaatsen()
    {
        var (_, body) = BerichtAntwoordGenerator.BouwHerplanAntwoord(Wedstrijd(), null, Classificatie(), Email, Voetnoot);

        body.Should().Contain($"De wedstrijd TEST JO14-1 - Ander JO14-1 op {DatumTekst} om 14:00 op veld 4 kan helaas niet verplaatst worden. Er zijn geen alternatieven beschikbaar.");
    }

    [Fact]
    public void Herplan_SplitstEerderEnLaterEnLaatSlotsBinnenDertigMinutenWeg()
    {
        var opties = new TestHerplanOpties { Alternatieven = [Slot(1, "12:00"), Slot(2, "13:31"), Slot(3, "14:29"), Slot(4, "14:30"), Slot(5, "16:00")] };
        var classificatie = Classificatie();
        classificatie.KnvbNotitie = "Binnen de termijn.";

        var (_, body) = BerichtAntwoordGenerator.BouwHerplanAntwoord(Wedstrijd(), opties, classificatie, Email, Voetnoot);

        body.Should().Contain($"De wedstrijd TEST JO14-1 - Ander JO14-1 staat gepland op {DatumTekst} om 14:00 op veld 4.\n"
            + "\nEerdere mogelijkheden:\n- veld 1 om 12:00 (eindigt 11:45)\n"
            + "\nLatere mogelijkheden:\n- veld 4 om 14:30 (eindigt 11:45)\n- veld 5 om 16:00 (eindigt 11:45)\n"
            + "\nLaat weten welke optie de voorkeur heeft.");
        body.Should().NotContain("veld 2").And.NotContain("veld 3");
        body.Should().Contain("\n\nLet op: Binnen de termijn. Zie ook: https://www.knvb.nl/");
    }

    [Fact]
    public void Herplan_AlleenSlotsDichtbij_MeldtVolleWedstrijddag()
    {
        var opties = new TestHerplanOpties { Alternatieven = [Slot(1, "14:00"), Slot(2, "14:15")] };

        var (_, body) = BerichtAntwoordGenerator.BouwHerplanAntwoord(Wedstrijd(), opties, Classificatie(), Email, Voetnoot);

        body.Should().Contain("Het is een volle wedstrijddag en er zijn helaas geen zinvolle alternatieven beschikbaar.");
        body.Should().NotContain("Let op:");
    }

    [Fact]
    public void HerplanTeLaat_NoemtTermijnEnDagen()
    {
        var (_, zonder) = BerichtAntwoordGenerator.BouwHerplanTeLaatAntwoord(null, 7, 2, Classificatie(), Email, Voetnoot);
        var (_, met) = BerichtAntwoordGenerator.BouwHerplanTeLaatAntwoord(Wedstrijd(), 7, 2, Classificatie(), Email, Voetnoot);

        zonder.Should().Contain("Je herplanverzoek is helaas te laat ingediend. Volgens onze richtlijn moet een herplanverzoek minimaal 7 dagen voor de wedstrijd worden ingediend.");
        met.Should().Contain($"staat gepland op {DatumTekst} om 14:00 op veld 4. Dat is over 2 dag(en).");
        met.Should().Contain("minimaal 7 dagen voor de wedstrijd worden ingediend.");
    }

    [Fact]
    public void VerzetZonderDatum_SomtVrijeZaterdagenOpEnMeldtDeBijlage()
    {
        var classificatie = Classificatie();
        classificatie.VoegKnvbPdfBijlageToe = true;

        var (_, body) = BerichtAntwoordGenerator.BouwVerzetZonderDatumAntwoord(
            Wedstrijd(), ["2026-09-19", "2026-09-26"], classificatie, Email, Voetnoot);

        body.Should().Contain("Bedankt voor je verzoek om de wedstrijd TEST JO14-1 - Ander JO14-1");
        body.Should().Contain($"geen wedstrijd:\n- {AntwoordTekst.Datum("2026-09-19")}\n- {AntwoordTekst.Datum("2026-09-26")}\n");
        body.Should().Contain("\n\nDe KNVB-speeldagenkalender is als bijlage toegevoegd.");
    }

    [Fact]
    public void VerzetZonderDatum_ZonderVrijeZaterdagenEnZonderWedstrijd()
    {
        var (_, geenVrij) = BerichtAntwoordGenerator.BouwVerzetZonderDatumAntwoord(Wedstrijd(), [], Classificatie(), Email, Voetnoot);
        var (_, geenWedstrijd) = BerichtAntwoordGenerator.BouwVerzetZonderDatumAntwoord(null, [], Classificatie(), Email, Voetnoot);

        geenVrij.Should().Contain("Op korte termijn hebben we geen speelvrije zaterdagen kunnen vinden");
        geenVrij.Should().NotContain("als bijlage toegevoegd");
        geenWedstrijd.Should().Contain($"Er is geen wedstrijd gevonden voor het opgegeven team op {DatumTekst}.");
    }

    [Fact]
    public void GewensteDatum_GeenRuimte_GebruiktDeDagdeelZin()
    {
        var beschikbaarheid = new TestBeschikbaarheid { Reden = "Vol.", GecontroleerdDagdeel = "middag" };

        var (_, body) = BerichtAntwoordGenerator.BouwHerplanGewensteDatumAntwoord(
            Wedstrijd(), "2026-09-19", beschikbaarheid, Classificatie(), Email, Voetnoot);

        body.Should().Contain($"De wedstrijd TEST JO14-1 - Ander JO14-1 staat momenteel gepland op {DatumTekst} om 14:00 op veld 4.\n\n"
            + DagdeelVenster.GeenRuimteOpGewensteDatum(AntwoordTekst.Datum("2026-09-19"), "middag", "Vol."));
    }

    [Fact]
    public void GewensteDatum_VenstersToewijzingOfAlleenRuimte()
    {
        var gewenst = AntwoordTekst.Datum("2026-09-19");
        var vensters = new TestBeschikbaarheid { Beschikbaar = true, BeschikbareVensters = [Venster(6, "09:00", "22:00", "opmerking")] };
        var toewijzing = new TestBeschikbaarheid { Beschikbaar = true, Toewijzing = Slot(6, "10:00") };
        var ruimte = new TestBeschikbaarheid { Beschikbaar = true };

        var (_, a) = BerichtAntwoordGenerator.BouwHerplanGewensteDatumAntwoord(Wedstrijd(), "2026-09-19", vensters, Classificatie(), Email, Voetnoot);
        var (_, b) = BerichtAntwoordGenerator.BouwHerplanGewensteDatumAntwoord(Wedstrijd(), "2026-09-19", toewijzing, Classificatie(), Email, Voetnoot);
        var (_, c) = BerichtAntwoordGenerator.BouwHerplanGewensteDatumAntwoord(Wedstrijd(), "2026-09-19", ruimte, Classificatie(), Email, Voetnoot);

        a.Should().Contain($"Op {gewenst} zijn de volgende mogelijkheden:\n- veld 6: beschikbaar van 09:00 tot einde dag\n");
        a.Should().NotContain("(opmerking)", "dit pad toont bewust geen opmerking");
        b.Should().Contain($"Op {gewenst} is veld 6 beschikbaar om 10:00 (eindigt 11:45).");
        c.Should().Contain($"Op {gewenst} is er ruimte beschikbaar.");
    }

    // ── Vaste antwoorden ──

    [Fact]
    public void TeamContact_NoemtHetTeamAlleenAlsHetBevestigdIs()
    {
        var bevestigd = Classificatie(type: VerzoekType.TeamContactOpvragen);
        bevestigd.TeamNaam = "JO13-2";
        var onbevestigd = Classificatie(type: VerzoekType.TeamContactOpvragen);
        onbevestigd.TeamNaam = "JO13-2";
        onbevestigd.TeamNaamOnbevestigd = true;

        var (_, a) = BerichtAntwoordGenerator.BouwTeamContactAutoReply(bevestigd, Email, Voetnoot);
        var (_, b) = BerichtAntwoordGenerator.BouwTeamContactAutoReply(onbevestigd, Email, Voetnoot);

        a.Should().Contain("Uw vraag over de begeleiding van JO13-2 is doorgestuurd. De begeleider neemt rechtstreeks contact met u op. Contactgegevens worden niet gedeeld conform AVG.");
        b.Should().Contain("Uw vraag over de begeleiding van het opgegeven team is doorgestuurd.");
    }

    [Fact]
    public void VasteAntwoorden_HebbenHunEigenTekst()
    {
        BerichtAntwoordGenerator.BouwBevestigingAntwoord(Email, Classificatie(), Voetnoot).body
            .Should().Contain("Bedankt voor je bevestiging. Het verzoek is geregistreerd en wordt door de coördinator verwerkt.");
        BerichtAntwoordGenerator.BouwBuitenScopeAntwoord(Email, Voetnoot).body
            .Should().Contain("Dit verzoek vereist handmatige afhandeling en is ter beoordeling bij de coördinator neergelegd.");
        BerichtAntwoordGenerator.BouwFoutAntwoord(Email, Classificatie(), Voetnoot).body
            .Should().Contain("Er is een fout opgetreden bij het verwerken van je verzoek.");
        BerichtAntwoordGenerator.BouwTeamOnbekendAntwoord("Ander FC", Classificatie(), Email, Voetnoot).body
            .Should().Contain("We kunnen de wedstrijd van Ander FC niet vinden in ons programma. Tegen welk van onze teams zou deze wedstrijd zijn?");
        BerichtAntwoordGenerator.BouwTeamOnbekendAntwoord("", Classificatie(), Email, Voetnoot).body
            .Should().Contain("We kunnen de wedstrijd van  niet vinden");
    }

    [Fact]
    public void WedstrijdAlIngepland_MetEnZonderVeldEnZonderWedstrijd()
    {
        BerichtAntwoordGenerator.BouwWedstrijdAlIngeplandAntwoord(Wedstrijd(), Classificatie(), Email, Voetnoot).body
            .Should().Contain($"De wedstrijd TEST JO14-1 - Ander JO14-1 staat al ingepland op {DatumTekst} om 14:00 op veld 4.");
        BerichtAntwoordGenerator.BouwWedstrijdAlIngeplandAntwoord(Wedstrijd(veld: " "), Classificatie(), Email, Voetnoot).body
            .Should().Contain($"staat al ingepland op {DatumTekst} om 14:00.");
        BerichtAntwoordGenerator.BouwWedstrijdAlIngeplandAntwoord(null, Classificatie(), Email, Voetnoot).body
            .Should().Contain("Er is een fout opgetreden bij het ophalen van de wedstrijdgegevens.");
    }

    // ── Sjabloon ──

    [Fact]
    public void Sjabloon_VultPlaceholdersInEnGebruiktHetSjabloononderwerp()
    {
        var classificatie = Classificatie("10:30");
        classificatie.TeamNaam = "JO13-2";
        classificatie.Tegenstander = "Ander FC";
        var sjabloon = new TestSjabloon("test", "Onderwerp {{TEAM}}", "{{voornaam}} {{datum}} {{team}} {{tegenstander}} {{aanvangstijd}} {{extra}} {{onbekend}}");

        var (onderwerp, body) = BerichtAntwoordGenerator.BouwAangepasteAntwoord(
            sjabloon, classificatie, Email, Voetnoot, new Dictionary<string, string> { ["extra"] = "X", ["team"] = "overschreven" });

        onderwerp.Should().Be("Onderwerp overschreven");
        body.Should().StartWith($"Jan {DatumTekst} overschreven Ander FC 10:30 X {{{{onbekend}}}}");
        body.Should().EndWith("\n\nTestomgeving");
    }

    [Fact]
    public void Sjabloon_ZonderOnderwerp_ValtTerugOpReEnVoegtDeDagdeelzinToe()
    {
        var sjabloon = new TestSjabloon("test", " ", "Tekst.");

        var (onderwerp, body) = BerichtAntwoordGenerator.BouwAangepasteAntwoord(
            sjabloon, Classificatie(), Email, Voetnoot, gecontroleerdDagdeel: "ochtend");

        onderwerp.Should().Be("Re: Beschikbaarheid");
        body.Should().StartWith(DagdeelVenster.VoegControleZinToeTenzijVermeld("Tekst.", "ochtend", "Tekst."));
    }

    [Fact]
    public void VulPlaceholdersIn_IsHoofdletterongevoeligEnLaatOnbekendeStaan()
    {
        var waarden = new Dictionary<string, string> { ["naam"] = "Jan" };

        BerichtAntwoordGenerator.VulPlaceholdersIn("{{NAAM}} {{rest}}", waarden).Should().Be("Jan {{rest}}");
        BerichtAntwoordGenerator.VulPlaceholdersIn("{{naam}}", new Dictionary<string, string>()).Should().Be("{{naam}}");
        BerichtAntwoordGenerator.VulPlaceholdersIn("", waarden).Should().Be("");
    }

    // ── Handtekening ──

    [Fact]
    public void Handtekening_ZonderVoetnoot_BouwtUitAfzenderEnCoordinator()
    {
        var instellingen = new TestInstellingen { EmailVoetnoot = " ", PlannerAfzenderNaam = "Planner", CoordinatorNaam = "Frenkie", CoordinatorFunctie = "Coördinator" };

        var (_, body) = BerichtAntwoordGenerator.BouwBuitenScopeAntwoord(Email, instellingen);

        body.Should().EndWith("\n\nMet vriendelijke groet,\n\nPlanner\nGeautomatiseerd antwoord namens Frenkie\nCoördinator");
    }

    [Fact]
    public void Handtekening_AlleenAfzender_LaatCoordinatorregelsWeg()
    {
        var (_, body) = BerichtAntwoordGenerator.BouwBuitenScopeAntwoord(Email, new TestInstellingen { PlannerAfzenderNaam = "Planner" });

        body.Should().EndWith("\n\nMet vriendelijke groet,\n\nPlanner");
    }

    [Fact]
    public void Handtekening_ZonderVoetnootEnAfzender_GooitDeMeldingVanDeTier()
    {
        var act = () => BerichtAntwoordGenerator.BouwBuitenScopeAntwoord(Email, new TestInstellingen { OntbrekendeAfzenderNaamMelding = "tiermelding" });

        act.Should().Throw<InvalidOperationException>().WithMessage("tiermelding");
    }

    // ── Reviewmodus (procesbrede omgevingsvariabele) ──

    [Fact]
    public void Reviewmodus_ZetEenKopBovenHetAntwoordEnHetSjabloon()
    {
        var oud = Environment.GetEnvironmentVariable(EmailReviewModus.InstellingNaam);
        try
        {
            Environment.SetEnvironmentVariable(EmailReviewModus.InstellingNaam, "TRUE");

            var (_, standaard) = BerichtAntwoordGenerator.BouwBuitenScopeAntwoord(Email, Voetnoot);
            var (_, sjabloon) = BerichtAntwoordGenerator.BouwAangepasteAntwoord(new TestSjabloon("sleutel", "", "Tekst."), Classificatie(), Email, Voetnoot);

            standaard.Should().StartWith("=== REVIEW MODE ===\nOriginele afzender: trainer@voorbeeld.nl\nOnderwerp: Beschikbaarheid\nClassificatie: BuitenScope\n==================\n\n");
            sjabloon.Should().StartWith("=== REVIEW MODE ===\nOriginele afzender: trainer@voorbeeld.nl\nOnderwerp: Beschikbaarheid\nClassificatie: BeschikbaarheidCheck\nTemplate: sleutel\n==================\n\nTekst.");

            Environment.SetEnvironmentVariable(EmailReviewModus.InstellingNaam, "nee");
            BerichtAntwoordGenerator.BouwBuitenScopeAntwoord(Email, Voetnoot).body.Should().NotContain("REVIEW MODE");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EmailReviewModus.InstellingNaam, oud);
        }
    }
}
