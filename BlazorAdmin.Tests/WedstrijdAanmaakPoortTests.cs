using BlazorAdmin.Services;
using AwesomeAssertions;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// Regressietests voor #1436: op "Wedstrijd aanmaken" werd een wedstrijd in Sportlink aangemaakt
/// zonder klik op de knop — een Enter in een tekstveld was een impliciete form-submit.
/// </summary>
public class WedstrijdAanmaakPoortTests
{
    private static readonly IReadOnlyList<string> GeenFouten = Array.Empty<string>();

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Zonder_bevestigde_dryrun_eerst_bevestigen(bool? dryRun)
    {
        var poort = new WedstrijdAanmaakPoort();

        poort.VraagAan(GeenFouten, dryRun).Should().BeFalse("met dry-run uit of onbekend mag er niets zonder bevestiging naar Sportlink");
        poort.Huidig.Should().Be(WedstrijdAanmaakPoort.Stap.Bevestigen);
        poort.InvoerVergrendeld.Should().BeTrue("wat bevestigd wordt, moet ook verstuurd worden");

        poort.Bevestig().Should().BeTrue();
        poort.Huidig.Should().Be(WedstrijdAanmaakPoort.Stap.Bezig);
    }

    [Fact]
    public void Met_dryrun_aan_direct_versturen()
    {
        var poort = new WedstrijdAanmaakPoort();

        poort.VraagAan(GeenFouten, dryRun: true).Should().BeTrue();
        poort.Huidig.Should().Be(WedstrijdAanmaakPoort.Stap.Bezig);
    }

    [Fact]
    public void Tweede_bevestiging_of_aanvraag_tijdens_verzending_doet_niets()
    {
        var poort = new WedstrijdAanmaakPoort();
        poort.VraagAan(GeenFouten, dryRun: false);
        poort.Bevestig().Should().BeTrue();

        poort.Bevestig().Should().BeFalse("een dubbelklik mag geen tweede wedstrijd aanmaken");
        poort.VraagAan(GeenFouten, dryRun: true).Should().BeFalse();

        poort.Klaar();
        poort.Huidig.Should().Be(WedstrijdAanmaakPoort.Stap.Invoer);
    }

    [Fact]
    public void Bevestigen_zonder_aanvraag_doet_niets()
    {
        new WedstrijdAanmaakPoort().Bevestig().Should().BeFalse();
    }

    [Fact]
    public void Annuleren_gaat_terug_naar_invoer()
    {
        var poort = new WedstrijdAanmaakPoort();
        poort.VraagAan(GeenFouten, dryRun: false);

        poort.Annuleer();

        poort.Huidig.Should().Be(WedstrijdAanmaakPoort.Stap.Invoer);
        poort.Bevestig().Should().BeFalse();
    }

    [Fact]
    public void Validatiefouten_houden_de_poort_dicht()
    {
        var poort = new WedstrijdAanmaakPoort();
        var fouten = WedstrijdAanmaakPoort.Valideer(DateTime.Today, "19:00", 90, "TEST", tegenstander: null);

        poort.VraagAan(fouten, dryRun: true).Should().BeFalse();
        poort.Huidig.Should().Be(WedstrijdAanmaakPoort.Stap.Invoer);
    }

    [Fact]
    public void Valideer_geeft_Nederlandse_melding_per_ontbrekend_veld()
    {
        var fouten = WedstrijdAanmaakPoort.Valideer(null, "", 0, " ", null);

        fouten.Should().BeEquivalentTo(
            "Vul een datum in.",
            "Vul een geldige aanvangstijd in (bijv. 19:00).",
            "Vul een duur in tussen 1 en 240 minuten.",
            "Kies een team of vul een vrije teamnaam in.",
            "Vul een tegenstander in.");
    }

    [Fact]
    public void Valideer_volledige_invoer_is_bruikbaar()
    {
        WedstrijdAanmaakPoort.Valideer(DateTime.Today, "19:00", 90, "JO10-1", "SV Voorbeeld").Should().BeEmpty();
    }

    /// <summary>
    /// Broncodeguard: de pagina mag geen form-submit meer kennen. Een <c>&lt;form&gt;</c> of
    /// <c>type="submit"</c> brengt de Enter-submit uit #1436 terug, ook als de code-behind klopt.
    /// </summary>
    [Fact]
    public void Pagina_heeft_geen_form_en_geen_submitknop()
    {
        var pad = Path.Combine(ZoekRepoRoot(), "BlazorAdmin", "Shared", "WedstrijdAanmakenFormulier.razor");
        var markup = File.ReadAllText(pad);

        markup.Should().NotContain("<form", "Enter in een tekstveld zou dan een wedstrijd aanmaken");
        markup.Should().NotContain("@onsubmit");
        markup.Should().NotContain("type=\"submit\"");
    }

    private static string ZoekRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.slnf")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository-root niet gevonden.");
    }
}
