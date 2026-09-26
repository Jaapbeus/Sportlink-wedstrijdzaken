using BlazorAdmin.Models;
using AwesomeAssertions;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// Tests voor de ingebouwde basisthema's (#1257, epic #1249). Twee dingen die stil misgaan als
/// niemand ze bewaakt: een preset die een kleur mist (die kleur blijft dan op de vorige waarde
/// staan bij het toepassen, wat eruitziet als "de preset doet niets"), en een kleurwaarde die niet
/// aan het serverformaat voldoet (die wordt bij opslaan met HTTP 400 geweigerd — pas ná het
/// invullen, dus met een onbegrijpelijke foutmelding voor de beheerder).
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

    public static TheoryData<string> PresetNamen()
    {
        var data = new TheoryData<string>();
        foreach (var preset in ThemePresets.Alle) data.Add(preset.Naam);
        return data;
    }

    [Theory]
    [MemberData(nameof(PresetNamen))]
    public void ElkePreset_HeeftElkeKleurInBeideModi(string naam)
    {
        var preset = ThemePresets.Alle.Single(p => p.Naam == naam);
        var verwacht = ThemePresets.Kleuren.Select(k => k.Sleutel).ToArray();

        preset.Licht.Keys.Should().Contain(verwacht, $"'{naam}' laat anders kleuren op hun vorige waarde staan");
        preset.Donker.Keys.Should().Contain(verwacht, $"'{naam}' laat anders kleuren op hun vorige waarde staan");
    }

    [Theory]
    [MemberData(nameof(PresetNamen))]
    public void ElkePreset_HeeftUitsluitendServerGeldigeKleurwaarden(string naam)
    {
        var preset = ThemePresets.Alle.Single(p => p.Naam == naam);

        foreach (var (sleutel, waarde) in preset.Licht.Concat(preset.Donker))
            waarde.Should().MatchRegex(HexPatroon, $"'{sleutel}' in '{naam}' wordt anders bij opslaan geweigerd");
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
    public void PresetNamen_ZijnUniek()
        => ThemePresets.Alle.Select(p => p.Naam).Should().OnlyHaveUniqueItems();

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
    public void Presets_BevattenGeenClubspecifiekeAanduiding()
    {
        // Deze repository is publiek en wordt door meerdere verenigingen geforkt: een preset mag
        // geen vereniging identificeren (CLAUDE.md, "Geen club-specifieke strings in code").
        // Namen beschrijven daarom het uiterlijk, niet een club.
        foreach (var preset in ThemePresets.Alle)
        {
            preset.Naam.Should().NotContainAny("FC", "VV", ".nl", "@");
            preset.Toelichting.Should().NotContainAny(".nl", "@");
        }
    }
}
