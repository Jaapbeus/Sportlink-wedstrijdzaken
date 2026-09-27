using BlazorAdmin.Models;
using AwesomeAssertions;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// Tests voor de instelbare kleuren (#1257, epic #1249). De basisthema-keuzelijst (<c>ThemePreset</c>/
/// <c>ThemePresets.Alle</c>) is verwijderd in #1401 — een club bewerkt elke kleur direct, licht en
/// donker apart. Wat blijft: een sleutel in <see cref="ThemePresets.Kleuren"/> zonder waarde in
/// <see cref="ThemePresets.StandaardLicht"/>/<see cref="ThemePresets.StandaardDonker"/> valt in
/// <c>Thema.razor.cs</c>'s <c>Kleur()</c> stilzwijgend terug op "#000000" — dat ziet eruit als een
/// bug, niet als een foutmelding — en een kleurwaarde die niet aan het serverformaat voldoet wordt
/// bij opslaan met HTTP 400 geweigerd, pas ná het invullen.
/// <para>
/// Dat de sleutels overeenkomen met de CSS-variabelen in <c>app.css</c> wordt in CI bewaakt door
/// <c>scripts/ci/check-theme-js-contract.js</c>; dat kan hier niet, want dat vergelijkt met een
/// bestand buiten dit project.
/// </para>
/// </summary>
public class ThemePresetsTests
{
    // Hetzelfde patroon als ThemeCore.IsValidPaletWaarde op de server (#1254): #rrggbb of #rrggbbaa.
    private const string HexPatroon = "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$";

    [Fact]
    public void StandaardLicht_HeeftElkeKleurUitKleuren()
    {
        var verwacht = ThemePresets.Kleuren.Select(k => k.Sleutel).ToArray();
        ThemePresets.StandaardLicht.Keys.Should().Contain(verwacht,
            "anders valt Kleur() terug op #000000 voor een club zonder eigen instelling");
    }

    [Fact]
    public void StandaardDonker_HeeftElkeKleurUitKleuren()
    {
        var verwacht = ThemePresets.Kleuren.Select(k => k.Sleutel).ToArray();
        ThemePresets.StandaardDonker.Keys.Should().Contain(verwacht,
            "anders valt Kleur() terug op #000000 voor een club zonder eigen instelling");
    }

    [Fact]
    public void StandaardLicht_HeeftUitsluitendServerGeldigeKleurwaarden()
    {
        foreach (var (sleutel, waarde) in ThemePresets.StandaardLicht)
            waarde.Should().MatchRegex(HexPatroon, $"'{sleutel}' wordt anders bij opslaan geweigerd");
    }

    [Fact]
    public void StandaardDonker_HeeftUitsluitendServerGeldigeKleurwaarden()
    {
        foreach (var (sleutel, waarde) in ThemePresets.StandaardDonker)
            waarde.Should().MatchRegex(HexPatroon, $"'{sleutel}' wordt anders bij opslaan geweigerd");
    }

    [Fact]
    public void Kleursleutels_ZijnCamelCaseZoalsDeServerEist()
    {
        // theme.js vertaalt de sleutel naar een CSS-propertynaam, en de server valideert hem tegen
        // ^[a-z][a-zA-Z0-9-]{0,39}$ (#1254). Een sleutel die daar niet aan voldoet komt er nooit in.
        foreach (var kleur in ThemePresets.Kleuren)
            kleur.Sleutel.Should().MatchRegex("^[a-z][a-zA-Z0-9-]{0,39}$");
    }

    [Fact]
    public void Kleursleutels_ZijnUniek()
        => ThemePresets.Kleuren.Select(k => k.Sleutel).Should().OnlyHaveUniqueItems();

    [Fact]
    public void StandaardLicht_KomtOvereenMetDeHuidigeStandaardkleuren()
    {
        // Deze vier zijn de defaults sinds #325 en tevens de terugval van ThemeDto. Wijzigen ze
        // hier stilzwijgend, dan verandert het uiterlijk van elke club die niets heeft ingesteld.
        ThemePresets.StandaardLicht["primary"].Should().Be("#1b6ec2");
        ThemePresets.StandaardLicht["secondary"].Should().Be("#6c757d");
        ThemePresets.StandaardLicht["accent"].Should().Be("#0071c1");
        ThemePresets.StandaardLicht["textOnPrimary"].Should().Be("#ffffff");
    }

    [Fact]
    public void StandaardLicht_BevatDeWedstrijdstatuskleurenVanVeldOptimalisatie()
    {
        // #1388: dit waren vóór deze uitbreiding hex-literals in
        // DagplanningWeergaveHelpers.GanttKleur/GanttVoorkeurBalkKleur en de legenda-CSS van
        // VeldOptimalisatie.razor.css. Wijzigen ze hier stilzwijgend, dan verandert de kleur van de
        // Gantt-blokken/legenda/badges voor elke club die niets heeft ingesteld.
        ThemePresets.StandaardLicht["statusOngewijzigd"].Should().Be("#198754");
        ThemePresets.StandaardLicht["statusWijziging"].Should().Be("#0d6efd");
        ThemePresets.StandaardLicht["statusNieuwSlot"].Should().Be("#d97706");
        ThemePresets.StandaardLicht["statusNietInplanbaar"].Should().Be("#dc3545");
        ThemePresets.StandaardLicht["voorkeurOpTijd"].Should().Be("#22c55e");
        ThemePresets.StandaardLicht["voorkeurKleineAfwijking"].Should().Be("#f59e0b");
        ThemePresets.StandaardLicht["voorkeurGroteAfwijking"].Should().Be("#ef4444");
    }

    [Fact]
    public void Kleuren_GroepeertWedstrijdstatuskleurenApart()
    {
        var wedstrijdstatusSleutels = ThemePresets.Kleuren
            .Where(k => k.Groep == "Wedstrijdstatus (Planning & Veld optimalisatie)")
            .Select(k => k.Sleutel)
            .ToArray();

        wedstrijdstatusSleutels.Should().BeEquivalentTo(new[]
        {
            "statusOngewijzigd", "statusWijziging", "statusNieuwSlot", "statusNietInplanbaar",
            "voorkeurOpTijd", "voorkeurKleineAfwijking", "voorkeurGroteAfwijking",
            "hoverHighlight", "hoverHighlightBg"
        });
    }

    [Fact]
    public void StandaardLicht_BevatDeOudeHardcodedHoverHighlightkleuren()
    {
        // #1401: zelfde waarden als de hardcoded kleuren die #1398 gedeeld maakte in app.css
        // (.gantt-blok-hover/.gantt-rij-hover) zonder ze instelbaar te maken.
        ThemePresets.StandaardLicht["hoverHighlight"].Should().Be("#fd7e14");
        ThemePresets.StandaardLicht["hoverHighlightBg"].Should().Be("#fd7e1499");
    }

    [Fact]
    public void Kleuren_GroepeertNavigatiekleurenApart()
    {
        // #1401: dedicated navigatiekleuren i.p.v. hergebruik van secondary/textOnPrimary.
        var navigatieSleutels = ThemePresets.Kleuren
            .Where(k => k.Groep == "Navigatie")
            .Select(k => k.Sleutel)
            .ToArray();

        navigatieSleutels.Should().BeEquivalentTo(new[]
        {
            "navItemText", "navItemActiveBackground", "navItemHoverBackground",
            "navItemActiveHoverText", "navbarTogglerBackground", "topRowBackground", "modalBackdrop"
        });
    }

    [Fact]
    public void StandaardLicht_BevatDeOudeHardcodedNavigatiekleuren()
    {
        // #1401: zelfde waarden als de hardcoded kleuren die vóór deze uitbreiding in
        // NavMenu.razor.css stonden — geen visuele wijziging voor een club die niets aanpast.
        ThemePresets.StandaardLicht["navItemText"].Should().Be("#d7d7d7");
        ThemePresets.StandaardLicht["navItemActiveBackground"].Should().Be("#ffffff5e");
        ThemePresets.StandaardLicht["navItemHoverBackground"].Should().Be("#ffffff1a");
    }

    [Fact]
    public void Kleuren_GroepeertSysteemmeldingenApart()
    {
        // #1401: app-brede UI-concepten, los van de Planning-specifieke status*/voorkeur*-kleuren.
        var systeemSleutels = ThemePresets.Kleuren
            .Where(k => k.Groep == "Systeemmeldingen")
            .Select(k => k.Sleutel)
            .ToArray();

        systeemSleutels.Should().BeEquivalentTo(new[]
        {
            "success", "danger", "textOnDanger", "warning", "warningBorder", "warningBg",
            "warningText", "warningHoverBg", "info", "codeText", "noticeBg", "trackColor",
            "focusRingGap", "focusRingAccent"
        });
    }
}
