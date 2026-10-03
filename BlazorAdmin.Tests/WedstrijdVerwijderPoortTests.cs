using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>#1440: verwijderen van een zojuist aangemaakte oefenwedstrijd — alleen na bevestiging, alleen op een echte aanmaak.</summary>
public class WedstrijdVerwijderPoortTests
{
    private static SportlinkMutatieResultaatDto EchteAanmaak() => new() { IsSuccess = true, PublicMatchId = "M000000001" };

    [Fact]
    public void Knop_alleen_bij_echte_geslaagde_aanmaak_met_PublicMatchId()
    {
        WedstrijdVerwijderPoort.MagAanbieden(EchteAanmaak()).Should().BeTrue();

        WedstrijdVerwijderPoort.MagAanbieden(null).Should().BeFalse();
        WedstrijdVerwijderPoort.MagAanbieden(new() { IsSuccess = true, IsDryRun = true, PublicMatchId = "M1" }).Should().BeFalse("bij een dry-run bestaat de wedstrijd niet");
        WedstrijdVerwijderPoort.MagAanbieden(new() { IsSuccess = true, IsDryRun = true, IsForcedDryRun = true, PublicMatchId = "M1" }).Should().BeFalse();
        WedstrijdVerwijderPoort.MagAanbieden(new() { IsSuccess = false, PublicMatchId = "M1" }).Should().BeFalse();
        WedstrijdVerwijderPoort.MagAanbieden(new() { IsSuccess = true, PublicMatchId = "" }).Should().BeFalse();
    }

    [Fact]
    public void Verwijderen_vraagt_altijd_eerst_bevestiging()
    {
        var poort = new WedstrijdVerwijderPoort();

        poort.Bevestig().Should().BeFalse("zonder aanvraag geen verwijdering");
        poort.VraagAan();
        poort.Huidig.Should().Be(WedstrijdVerwijderPoort.Stap.Bevestigen);
        poort.ToonKnop(EchteAanmaak()).Should().BeFalse("in de bevestigstap staat de bevestiging, niet de knop");

        poort.Bevestig().Should().BeTrue();
        poort.Bevestig().Should().BeFalse("een dubbelklik mag geen tweede verwijderaanroep geven");
    }

    [Fact]
    public void Annuleren_gaat_terug_zonder_te_versturen()
    {
        var poort = new WedstrijdVerwijderPoort();
        poort.VraagAan();

        poort.Annuleer();

        poort.Huidig.Should().Be(WedstrijdVerwijderPoort.Stap.Gereed);
        poort.Bevestig().Should().BeFalse();
    }

    [Fact]
    public void Na_echte_verwijdering_verdwijnt_de_knop_na_simulatie_niet()
    {
        var poort = new WedstrijdVerwijderPoort();
        poort.VraagAan();
        poort.Bevestig();
        poort.Klaar(new() { IsSuccess = true, IsDryRun = true, IsForcedDryRun = true });
        poort.ToonKnop(EchteAanmaak()).Should().BeTrue("een simulatie heeft niets verwijderd");

        poort.VraagAan();
        poort.Bevestig();
        poort.Klaar(new() { IsSuccess = true });
        poort.Huidig.Should().Be(WedstrijdVerwijderPoort.Stap.Verwijderd);
        poort.ToonKnop(EchteAanmaak()).Should().BeFalse();
        poort.VraagAan();
        poort.Huidig.Should().Be(WedstrijdVerwijderPoort.Stap.Verwijderd);
    }
}
