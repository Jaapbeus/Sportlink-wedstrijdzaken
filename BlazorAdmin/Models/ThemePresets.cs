namespace BlazorAdmin.Models;

/// <summary>
/// Eén instelbare themakleur: de sleutel zoals die naar de API en naar <c>theme.js</c> gaat, plus
/// hoe het beheerscherm hem presenteert.
/// </summary>
/// <param name="Sleutel">
/// camelCase, precies zoals <c>theme.js</c> hem naar een CSS-variabele vertaalt
/// (<c>cardBg</c> → <c>--theme-card-bg-light</c>). Wijzigt deze lijst, dan faalt de CI-guard
/// <c>check-theme-js-contract.js</c> als <c>app.css</c> niet meegroeit.
/// </param>
/// <param name="StaatAlphaToe">
/// <c>true</c> voor kleuren die doorzichtigheid nodig hebben, zoals de schaduw. Voor die kleuren
/// toont het scherm geen kleurenkiezer: <c>&lt;input type="color"&gt;</c> kent geen alpha en zou de
/// waarde bij het openen stilzwijgend terugbrengen tot zes cijfers.
/// </param>
public sealed record ThemeKleurDefinitie(
    string Sleutel,
    string Label,
    string? Toelichting = null,
    bool StaatAlphaToe = false);

/// <summary>Een basisthema: een complete kleurenset voor beide modi, als startpunt.</summary>
public sealed record ThemePreset(
    string Naam,
    string Toelichting,
    IReadOnlyDictionary<string, string> Licht,
    IReadOnlyDictionary<string, string> Donker);

/// <summary>
/// De instelbare kleuren en de ingebouwde basisthema's (#1257, epic #1249). Client-side, geen
/// databasetabel: dit zijn startpunten om het kleurenformulier mee te vullen, geen opgeslagen data.
/// <para>
/// <b>Geen clubnamen of clubkleuren hier.</b> Deze repository is publiek en wordt door meerdere
/// verenigingen geforkt; een voorinstelling die een vereniging bij naam noemt of haar huisstijl
/// meelevert hoort niet in de broncode (zie "Geen club-specifieke strings in code" in CLAUDE.md en
/// de lijst met niet-geaccepteerde bijdragen in CONTRIBUTING.md). De thema's hieronder zijn daarom
/// benoemd naar hun uiterlijk. Een club zet haar eigen kleuren met "Ophalen" van de eigen website
/// of door ze in te typen.
/// </para>
/// </summary>
public static class ThemePresets
{
    /// <summary>
    /// De volgorde bepaalt de volgorde in het beheerscherm. Deze negen sleutels komen exact overeen
    /// met de <c>--theme-*</c>-variabelen in <c>app.css</c>.
    /// </summary>
    public static IReadOnlyList<ThemeKleurDefinitie> Kleuren { get; } = new[]
    {
        new ThemeKleurDefinitie("primary",         "Primaire kleur",        "Achtergrond zijbalk, knoppen"),
        new ThemeKleurDefinitie("secondary",       "Secundaire kleur"),
        new ThemeKleurDefinitie("accent",          "Accentkleur (links)"),
        new ThemeKleurDefinitie("textOnPrimary",   "Tekst op primaire achtergrond", "Meestal wit (#ffffff) of zwart (#000000)"),
        new ThemeKleurDefinitie("pageBg",          "Pagina-achtergrond",    "Achtergrond achter het laadscherm"),
        new ThemeKleurDefinitie("cardBg",          "Kaartachtergrond"),
        new ThemeKleurDefinitie("mutedText",       "Gedempte tekst",        "Bijschriften en toelichtingen"),
        new ThemeKleurDefinitie("mutedTextSubtle", "Extra gedempte tekst"),
        new ThemeKleurDefinitie("shadowHover",     "Schaduw bij aanwijzen", "Acht cijfers mag: de laatste twee zijn de doorzichtigheid", StaatAlphaToe: true)
    };

    /// <summary>De huidige standaardkleuren van de applicatie — ongewijzigd sinds #325.</summary>
    public static IReadOnlyDictionary<string, string> StandaardLicht { get; } = new Dictionary<string, string>
    {
        ["primary"]         = "#1b6ec2",
        ["secondary"]       = "#6c757d",
        ["accent"]          = "#0071c1",
        ["textOnPrimary"]   = "#ffffff",
        ["pageBg"]          = "#f0f2f5",
        ["cardBg"]          = "#ffffff",
        ["mutedText"]       = "#6c757d",
        ["mutedTextSubtle"] = "#adb5bd",
        ["shadowHover"]     = "#0000001f"
    };

    /// <summary>De neutrale donkerwaarden die ook in <c>app.css</c> als terugval staan (#1255).</summary>
    public static IReadOnlyDictionary<string, string> StandaardDonker { get; } = new Dictionary<string, string>
    {
        ["primary"]         = "#4a9eff",
        ["secondary"]       = "#9aa4b2",
        ["accent"]          = "#6cb6ff",
        ["textOnPrimary"]   = "#0c1424",
        ["pageBg"]          = "#0c1424",
        ["cardBg"]          = "#171f30",
        ["mutedText"]       = "#9aa4b2",
        ["mutedTextSubtle"] = "#6b7686",
        ["shadowHover"]     = "#0000008c"
    };

    public static IReadOnlyList<ThemePreset> Alle { get; } = new[]
    {
        new ThemePreset("Standaard", "De kleuren waarmee de applicatie standaard werkt", StandaardLicht, StandaardDonker),

        new ThemePreset("Donkerblauw en geel", "Klassieke combinatie voor een sportvereniging",
            Licht: Samenstellen(StandaardLicht, new()
            {
                ["primary"] = "#1f3a6e", ["secondary"] = "#5a6474", ["accent"] = "#1f3a6e",
                ["textOnPrimary"] = "#ffffff"
            }),
            Donker: Samenstellen(StandaardDonker, new()
            {
                ["primary"] = "#f5c518", ["secondary"] = "#9aa4b2", ["accent"] = "#f5c518",
                ["textOnPrimary"] = "#14213d", ["pageBg"] = "#0c1424", ["cardBg"] = "#14213d"
            })),

        new ThemePreset("Groen", "Rustige groentint met een neutrale achtergrond",
            Licht: Samenstellen(StandaardLicht, new()
            {
                ["primary"] = "#1b6b4a", ["secondary"] = "#5f6b66", ["accent"] = "#14875a",
                ["textOnPrimary"] = "#ffffff"
            }),
            Donker: Samenstellen(StandaardDonker, new()
            {
                ["primary"] = "#4ade9b", ["secondary"] = "#8fa39a", ["accent"] = "#4ade9b",
                ["textOnPrimary"] = "#0b1a14", ["pageBg"] = "#0b1a14", ["cardBg"] = "#132620"
            })),

        new ThemePreset("Rood", "Warme roodtint met een neutrale achtergrond",
            Licht: Samenstellen(StandaardLicht, new()
            {
                ["primary"] = "#9b1c1c", ["secondary"] = "#6f5a5a", ["accent"] = "#b91c1c",
                ["textOnPrimary"] = "#ffffff"
            }),
            Donker: Samenstellen(StandaardDonker, new()
            {
                ["primary"] = "#f87171", ["secondary"] = "#b09a9a", ["accent"] = "#f87171",
                ["textOnPrimary"] = "#1a0b0b", ["pageBg"] = "#1a0b0b", ["cardBg"] = "#2a1414"
            }))
    };

    // Een preset hoeft alleen te noemen wat afwijkt; de rest komt uit de standaardset. Zo groeit
    // elke preset automatisch mee als er een kleur bij komt, in plaats van stilzwijgend incompleet
    // te raken.
    private static IReadOnlyDictionary<string, string> Samenstellen(
        IReadOnlyDictionary<string, string> basis,
        Dictionary<string, string> afwijkingen)
    {
        var resultaat = new Dictionary<string, string>(basis);
        foreach (var (sleutel, waarde) in afwijkingen) resultaat[sleutel] = waarde;
        return resultaat;
    }
}
