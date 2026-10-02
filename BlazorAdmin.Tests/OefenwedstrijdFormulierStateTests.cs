using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>#1437: voorinvulling bij een teamkeuze, "Leegmaken" en de Sportlink-link van "Wedstrijd aanmaken".</summary>
public class OefenwedstrijdFormulierStateTests
{
    private static OefenwedstrijdFormulierState MetTeams()
    {
        var state = new OefenwedstrijdFormulierState();
        state.ZetTeamGegevens(new[]
        {
            new OefenwedstrijdFormulierTeamDto { TeamNaam = "ALLSTARS JO10-1", Leeftijdscategorie = "JO10", AgeClassCode = "110", Duur = 60, Veldafmeting = "0.5" },
            new OefenwedstrijdFormulierTeamDto { TeamNaam = "ALLSTARS 35+4", Leeftijdscategorie = "1-99", AgeClassCode = null, Duur = null, Veldafmeting = null },
        });
        return state;
    }

    [Fact]
    public void BeginStand_IsVandaagTijd1900Duur90HeelVeldEnRestLeeg()
    {
        var state = new OefenwedstrijdFormulierState();

        state.Datum.Should().Be(DateTime.Today);
        state.Tijd.Should().Be("19:00");
        state.Duur.Should().Be(90);
        state.Velddeel.Should().Be("1.0");
        state.AgeClassCode.Should().BeEmpty();
        state.TeamSelectie.Should().BeNull();
        state.Tegenstander.Should().BeNull();
        state.VeldNummer.Should().BeNull();
        state.Omschrijving.Should().BeNull();
    }

    [Fact]
    public void KiesTeam_VultDuurLeeftijdscategorieEnVelddeelVoor()
    {
        var state = MetTeams();

        state.TeamSelectie = "ALLSTARS JO10-1";

        state.Duur.Should().Be(60);
        state.AgeClassCode.Should().Be("110");
        state.Velddeel.Should().Be("0.5");
    }

    [Fact]
    public void KiesTeam_ZonderBekendeGegevens_BlijftBijDuurEnZetLeeftijdscategorieLegEnVelddeelHeel()
    {
        var state = MetTeams();
        state.TeamSelectie = "ALLSTARS JO10-1";
        state.Duur = 45;

        state.TeamSelectie = "ALLSTARS 35+4";

        state.Duur.Should().Be(45, "onbekende duur wordt niet overschreven");
        state.AgeClassCode.Should().BeEmpty("geen bekende Sportlink-categorie → Sportlink-standaard");
        state.Velddeel.Should().Be("1.0");
    }

    [Fact]
    public void GebruikerKanDeVoorinvullingDaarnaOverschrijven()
    {
        var state = MetTeams();
        state.TeamSelectie = "ALLSTARS JO10-1";

        state.Duur = 75;
        state.Velddeel = "0.25";
        state.AgeClassCode = "215";

        state.Duur.Should().Be(75);
        state.Velddeel.Should().Be("0.25");
        state.AgeClassCode.Should().Be("215");
    }

    [Fact]
    public void VrijeTekst_VultNietVoorEnGebruiktDeVrijeNaam()
    {
        var state = MetTeams();

        state.TeamSelectie = OefenwedstrijdFormulierState.VrijeTekstOptie;
        state.TeamVrijeTekst = "TEST";

        state.IsVrijeTekst.Should().BeTrue();
        state.TeamNaam.Should().Be("TEST");
        state.Duur.Should().Be(90);
    }

    [Fact]
    public void Leegmaken_ZetElkVeldTerugEnBehoudtDeTeamgegevens()
    {
        var state = MetTeams();
        state.TeamSelectie = "ALLSTARS JO10-1";
        state.TeamVrijeTekst = "x";
        state.Datum = new DateTime(2030, 1, 1);
        state.Tijd = "10:30";
        state.Tegenstander = "SV Voorbeeld";
        state.VeldNummer = 3;
        state.Omschrijving = "eigen tekst";

        state.Leegmaken();

        state.Datum.Should().Be(DateTime.Today);
        state.Tijd.Should().Be("19:00");
        state.Duur.Should().Be(90);
        state.TeamSelectie.Should().BeNull();
        state.TeamVrijeTekst.Should().BeNull();
        state.Tegenstander.Should().BeNull();
        state.VeldNummer.Should().BeNull();
        state.Velddeel.Should().Be("1.0");
        state.AgeClassCode.Should().BeEmpty();
        state.Omschrijving.Should().BeNull();

        state.TeamSelectie = "ALLSTARS JO10-1";
        state.Duur.Should().Be(60, "de geladen teamgegevens blijven na Leegmaken beschikbaar");
    }

    [Fact]
    public void NaGeslaagdeAanmaak_WistAlleenTegenstanderEnOmschrijving()
    {
        var state = MetTeams();
        state.TeamSelectie = "ALLSTARS JO10-1";
        state.Tegenstander = "SV Voorbeeld";
        state.Omschrijving = "tekst";

        state.NaGeslaagdeAanmaak();

        state.Tegenstander.Should().BeNull();
        state.Omschrijving.Should().BeNull();
        state.TeamSelectie.Should().Be("ALLSTARS JO10-1");
    }

    [Fact]
    public void SportlinkWedstrijdUrl_VerwijstNaarDeWedstrijddetailsEnMaaktDeIdVeilig()
    {
        OefenwedstrijdFormulierState.SportlinkWedstrijdUrl("abc123")
            .Should().Be("https://club.sportlink.com/competition-affairs/match-details/abc123");
        OefenwedstrijdFormulierState.SportlinkWedstrijdUrl("a/b?c")
            .Should().Be("https://club.sportlink.com/competition-affairs/match-details/a%2Fb%3Fc");
    }

    [Fact]
    public void Valideer_WeigertEenVelddeelDatNietInDeLijstStaat()
    {
        var fouten = WedstrijdAanmaakPoort.Valideer(DateTime.Today, "19:00", 90, "ALLSTARS JO10-1", "SV Voorbeeld", "0.75");

        fouten.Should().ContainSingle().Which.Should().Contain("velddeel");
        WedstrijdAanmaakPoort.Valideer(DateTime.Today, "19:00", 90, "ALLSTARS JO10-1", "SV Voorbeeld", "0.125").Should().BeEmpty();
    }
}
