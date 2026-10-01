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
/// <param name="Groep">
/// Groepeert de kleur in het beheerscherm en op het overzichtstabblad (#1388). Puur presentatie —
/// geen invloed op de CSS-variabele of het opslagcontract.
/// </param>
public sealed record ThemeKleurDefinitie(
    string Sleutel,
    string Label,
    string? Toelichting = null,
    bool StaatAlphaToe = false,
    string Groep = "Merk & interface");

/// <summary>
/// De instelbare kleuren (#1257, epic #1249; #1388 voegde de Planning-statuskleuren en groepering
/// toe; #1401 verwijderde de basisthema-keuzelijst — elke kleur wordt direct bewerkt, licht en
/// donker apart — en voegde dedicated navigatie- en systeemkleuren toe). Client-side, geen
/// databasetabel: <c>StandaardLicht</c>/<c>StandaardDonker</c> zijn de terugval om het
/// kleurenformulier mee te vullen, geen opgeslagen data.
/// </summary>
public static class ThemePresets
{
    /// <summary>
    /// De volgorde bepaalt de volgorde in het beheerscherm. Elke sleutel komt exact overeen met een
    /// <c>--theme-*</c>-variabelenpaar in <c>app.css</c>.
    /// </summary>
    public static IReadOnlyList<ThemeKleurDefinitie> Kleuren { get; } = new[]
    {
        new ThemeKleurDefinitie("primary",         "Primaire kleur",        "Achtergrond zijbalk, knoppen"),
        new ThemeKleurDefinitie("secondary",       "Secundaire kleur",      "Neutrale/secundaire badge op Veld optimalisatie"),
        new ThemeKleurDefinitie("accent",          "Accentkleur (links)"),
        new ThemeKleurDefinitie("textOnPrimary",   "Tekst op primaire achtergrond", "Meestal wit (#ffffff) of zwart (#000000)"),
        new ThemeKleurDefinitie("pageBg",          "Pagina-achtergrond",    "Achtergrond achter het laadscherm"),
        new ThemeKleurDefinitie("cardBg",          "Kaartachtergrond"),
        new ThemeKleurDefinitie("mutedText",       "Gedempte tekst",        "Bijschriften en toelichtingen"),
        new ThemeKleurDefinitie("mutedTextSubtle", "Extra gedempte tekst"),
        new ThemeKleurDefinitie("shadowHover",     "Schaduw bij aanwijzen", "Acht cijfers mag: de laatste twee zijn de doorzichtigheid", StaatAlphaToe: true),

        // Wedstrijdstatuskleuren van Planning en Veld optimalisatie (#1388). Stonden tot die
        // uitbreiding als losse hex-literals in DagplanningWeergaveHelpers.GanttKleur,
        // VeldOptimalisatie.razor.cs (RowClass/VoorkeurBadgeClass/GanttVoorkeurBalkKleur) en de
        // legenda-CSS — daardoor onzichtbaar en niet instelbaar voor de beheerder.
        new ThemeKleurDefinitie("statusOngewijzigd",          "Status: ongewijzigd",        "Gantt-blok, legenda en badge voor een wedstrijd die de planner laat staan", Groep: "Wedstrijdstatus (Planning & Veld optimalisatie)"),
        new ThemeKleurDefinitie("statusWijziging",            "Status: wijziging",          "Gantt-blok, legenda, badge en tabelrij voor een verplaatste wedstrijd", Groep: "Wedstrijdstatus (Planning & Veld optimalisatie)"),
        new ThemeKleurDefinitie("statusNieuwSlot",             "Status: nieuw slot",         "Gantt-blok, legenda, badge en tabelrij voor een nieuw ingepland tijdslot", Groep: "Wedstrijdstatus (Planning & Veld optimalisatie)"),
        new ThemeKleurDefinitie("statusNietInplanbaar",        "Status: niet inplanbaar",    "Badge en tabelrij voor een wedstrijd die de planner niet kon plaatsen", Groep: "Wedstrijdstatus (Planning & Veld optimalisatie)"),
        new ThemeKleurDefinitie("voorkeurOpTijd",              "Voorkeurstijd: op tijd",     "Indicatorbalk en badge als een wedstrijd exact op de voorkeurstijd staat", Groep: "Wedstrijdstatus (Planning & Veld optimalisatie)"),
        new ThemeKleurDefinitie("voorkeurKleineAfwijking",     "Voorkeurstijd: kleine afwijking", "Indicatorbalk en badge bij een kleine afwijking van de voorkeurstijd", Groep: "Wedstrijdstatus (Planning & Veld optimalisatie)"),
        new ThemeKleurDefinitie("voorkeurGroteAfwijking",      "Voorkeurstijd: grote afwijking",  "Indicatorbalk en badge bij een grote afwijking van de voorkeurstijd", Groep: "Wedstrijdstatus (Planning & Veld optimalisatie)"),

        // Hover-correlatie tussen tijdlijnblok en tabelrij (#1315, generiek gemaakt in #1398). Bleef
        // hardcoded toen #1398 de klassen deelde tussen Planning en Veld optimalisatie — #1401 maakt
        // hem alsnog instelbaar.
        new ThemeKleurDefinitie("hoverHighlight",   "Markeerkleur bij hoveren", "Rand bij het aanwijzen van een planningsblok", Groep: "Wedstrijdstatus (Planning & Veld optimalisatie)"),
        new ThemeKleurDefinitie("hoverHighlightBg", "Markeerachtergrond bij hoveren", "Schaduw en rijachtergrond bij het aanwijzen van een planningsblok", StaatAlphaToe: true, Groep: "Wedstrijdstatus (Planning & Veld optimalisatie)"),

        // Dedicated navigatiekleuren (#1401). Ervóór hergebruikte de zijbalk "secondary"/
        // "textOnPrimary" (actief-item) of had helemaal geen instelbare kleur (hover, navbar-
        // toggler, top-row) — een club kon de menu-tekst dus niet los van andere UI-elementen
        // aanpassen. Standaardwaarden = de oude hardcoded kleuren, dus geen visuele wijziging.
        new ThemeKleurDefinitie("navItemText",              "Navigatie-itemtekst", Groep: "Navigatie"),
        new ThemeKleurDefinitie("navItemActiveBackground",  "Actief navigatie-item achtergrond", StaatAlphaToe: true, Groep: "Navigatie"),
        new ThemeKleurDefinitie("navItemHoverBackground",   "Navigatie-item hover achtergrond", StaatAlphaToe: true, Groep: "Navigatie"),
        new ThemeKleurDefinitie("navItemActiveHoverText",   "Navigatietekst bij actief/hover", Groep: "Navigatie"),
        new ThemeKleurDefinitie("navbarTogglerBackground",  "Menuknop-achtergrond (mobiel)", StaatAlphaToe: true, Groep: "Navigatie"),
        new ThemeKleurDefinitie("topRowBackground",         "Bovenbalk-achtergrond", StaatAlphaToe: true, Groep: "Navigatie"),
        new ThemeKleurDefinitie("modalBackdrop",            "Achtergrond achter dialoogvensters", StaatAlphaToe: true, Groep: "Navigatie"),

        // Generieke systeemkleuren (#1401): app-brede UI-concepten (foutmeldingen, laadschermen,
        // testmodus-banner) — los van de Planning-specifieke status*/voorkeur*-kleuren hierboven.
        new ThemeKleurDefinitie("success",         "Succeskleur", "Vinkje op het laadscherm, geldige formuliervelden", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("danger",          "Foutkleur", "Foutmeldingen, ongeldige formuliervelden, onverwachte fouten", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("textOnDanger",    "Tekst op foutachtergrond", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("warning",         "Waarschuwingskleur", "ALLSTARS-testmodusbanner", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("warningBorder",   "Waarschuwingskleur (rand/hover)", "Rand van de testmodus-banner, hover-variant", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("warningBg",       "Waarschuwingsachtergrond", "Achtergrond van de club-kiezer in testmodus", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("warningText",     "Tekst op waarschuwingsachtergrond", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("warningHoverBg",  "Waarschuwing hover-achtergrond", StaatAlphaToe: true, Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("info",            "Informatiekleur", "Opstart-spinner", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("codeText",        "Codekleur", "Kleur van <code>-tekst", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("noticeBg",        "Meldingsachtergrond", "Achtergrond van de ontwikkel-foutbalk onderin", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("trackColor",      "Laadbalk-achtergrond", "De 'lege' baan van laadspinners", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("focusRingGap",    "Focusring — binnenrand", Groep: "Systeemmeldingen"),
        new ThemeKleurDefinitie("focusRingAccent", "Focusring — accent", Groep: "Systeemmeldingen")
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
        ["shadowHover"]     = "#0000001f",

        // Zelfde waarden als de hex-literals die vóór #1388 in GanttKleur/GanttVoorkeurBalkKleur en
        // de legenda-CSS stonden — geen visuele wijziging voor een club die niets aanpast.
        ["statusOngewijzigd"]      = "#198754",
        ["statusWijziging"]        = "#0d6efd",
        ["statusNieuwSlot"]        = "#d97706",
        ["statusNietInplanbaar"]   = "#dc3545",
        ["voorkeurOpTijd"]         = "#22c55e",
        ["voorkeurKleineAfwijking"] = "#f59e0b",
        ["voorkeurGroteAfwijking"]  = "#ef4444",

        // Hover-correlatie (#1315/#1398) — zelfde waarden als de oude hardcoded kleuren in app.css.
        ["hoverHighlight"]   = "#fd7e14",
        ["hoverHighlightBg"] = "#fd7e1499",

        // Navigatie (#1401) — zelfde waarden als de oude hardcoded kleuren in NavMenu.razor.css.
        ["navItemText"]             = "#d7d7d7",
        ["navItemActiveBackground"] = "#ffffff5e",
        ["navItemHoverBackground"]  = "#ffffff1a",
        ["navItemActiveHoverText"]  = "#ffffff",
        ["navbarTogglerBackground"] = "#ffffff1a",
        ["topRowBackground"]        = "#00000066",
        ["modalBackdrop"]           = "#00000073",

        // Systeemmeldingen (#1401) — zelfde waarden als de oude hardcoded kleuren in app.css.
        ["success"]         = "#198754",
        ["danger"]          = "#dc3545",
        ["textOnDanger"]    = "#ffffff",
        ["warning"]         = "#f59e0b",
        ["warningBorder"]   = "#d97706",
        ["warningBg"]       = "#fef3c7",
        ["warningText"]     = "#1c1917",
        ["warningHoverBg"]  = "#f59e0b1a",
        ["info"]            = "#0d6efd",
        ["codeText"]        = "#c02d76",
        ["noticeBg"]        = "#ffffe0",
        ["trackColor"]      = "#e6e9ed",
        ["focusRingGap"]    = "#ffffff",
        ["focusRingAccent"] = "#258cfb"
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
        ["shadowHover"]     = "#0000008c",

        // Iets lichtere/fellere varianten dan het lichte palet, zelfde reden als de bestaande
        // donkere merkkleuren hierboven: voldoende contrast tegen een donkere achtergrond.
        ["statusOngewijzigd"]      = "#2fbf75",
        ["statusWijziging"]        = "#4da3ff",
        ["statusNieuwSlot"]        = "#f59e0b",
        ["statusNietInplanbaar"]   = "#f87171",
        ["voorkeurOpTijd"]         = "#4ade80",
        ["voorkeurKleineAfwijking"] = "#fbbf24",
        ["voorkeurGroteAfwijking"]  = "#f87171",

        // Hover-correlatie (#1315/#1398) — zelfde waarden als het lichte palet.
        ["hoverHighlight"]   = "#fd7e14",
        ["hoverHighlightBg"] = "#fd7e1499",

        // Navigatie (#1401) — zelfde waarden als het lichte palet: de zijbalk-tekst/hover-affordance
        // is universeel wit-op-donker en hoeft niet per modus te verschillen.
        ["navItemText"]             = "#d7d7d7",
        ["navItemActiveBackground"] = "#ffffff40",
        ["navItemHoverBackground"]  = "#ffffff26",
        ["navItemActiveHoverText"]  = "#ffffff",
        ["navbarTogglerBackground"] = "#ffffff1a",
        ["topRowBackground"]        = "#00000066",
        ["modalBackdrop"]           = "#00000073",

        // Systeemmeldingen (#1401): bewust vrijwel gelijk aan de lichte modus — hun betekenis
        // (succes/fout/waarschuwing) staat los van het licht/donker-thema. Alleen trackColor en
        // focusRingGap krijgen een echt donkere tegenhanger, want die liggen tegen de kaart-
        // achtergrond aan.
        ["success"]         = "#22c55e",
        ["danger"]          = "#f87171",
        ["textOnDanger"]    = "#ffffff",
        ["warning"]         = "#fbbf24",
        ["warningBorder"]   = "#d97706",
        ["warningBg"]       = "#fef3c7",
        ["warningText"]     = "#1c1917",
        ["warningHoverBg"]  = "#f59e0b1a",
        ["info"]            = "#0d6efd",
        ["codeText"]        = "#c02d76",
        ["noticeBg"]        = "#ffffe0",
        ["trackColor"]      = "#2a3448",
        ["focusRingGap"]    = "#171f30",
        ["focusRingAccent"] = "#258cfb"
    };
}
