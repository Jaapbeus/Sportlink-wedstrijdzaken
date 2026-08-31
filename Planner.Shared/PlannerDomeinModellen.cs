using System;

namespace Planner.Shared;

// ── Tier-agnostische domeinmodellen voor de planningsmotor (#888) ──
//
// Verhuisd uit FunctionApp/Planner/PlannerModels.cs, samen met FieldScheduler
// (Planner.Shared/FieldScheduler.cs) — zelfde precedent als VeldResolver/VeldNormalisatie (#819)
// en TeamNaamNormalisatie (#889): logica die aantoonbaar geen SQL-afhankelijkheid heeft, hoort op
// precies één plek te staan. Een tweede tier-kopie van deze modellen zou de motor die erop werkt
// dwingen zich ook te dupliceren.
//
// Bewust NIET hierheen verhuisd: de HTTP-wire-contracten (CheckAvailabilityRequest,
// AutoPlanResponse, HerplanCheckRequest, ...) die in FunctionApp/Planner/PlannerModels.cs
// achterblijven. Die zijn per tier eigen JSON-vormgeving van hetzelfde endpoint, geen gedeelde
// rekenlogica — zelfde onderscheid als TeamScheduleModels.cs (#888, gedupliceerd) vs.
// TeamNaamNormalisatie (#889, verhuisd).

public class Speeltijd
{
    public string Leeftijd { get; set; } = string.Empty;
    public decimal Veldafmeting { get; set; }
    public int WedstrijdTotaal { get; set; }

    /// <summary>
    /// Standaard voorkeurstijd voor deze leeftijdscategorie (#666). Wordt door de planner gebruikt
    /// als een team géén eigen voorkeurstijd-regel heeft voor de speeldag.
    /// null = geen streeftijd; de planner valt dan terug op het eerst beschikbare slot.
    /// </summary>
    public TimeOnly? StandaardVoorkeurTijd { get; set; }
}

public class VeldInfo
{
    public int VeldNummer { get; set; }
    public string VeldNaam { get; set; } = string.Empty;
    public string VeldType { get; set; } = "kunstgras"; // vrije tekst uit de veldentabel; zie VeldTypeClassificatie
    public bool HeeftKunstlicht { get; set; }

    /// <summary>
    /// Kunstgras volgens de enige classificatie in deze codebase — zie
    /// <see cref="VeldTypeClassificatie"/>. Gebruik altijd deze property of
    /// <see cref="VeldTypeClassificatie"/>, nooit een nieuwe stringvergelijking (#705/#707).
    /// </summary>
    public bool IsKunstgras => VeldTypeClassificatie.IsKunstgras(VeldType);
}

/// <summary>
/// Grondsoort van een veld. Drie waarden, want "niet als kunstgras te herkennen" is niet
/// hetzelfde als "dus natuurgras": het veldtype is vrije tekst en elke club typt het anders.
/// </summary>
public enum VeldSoort
{
    /// <summary>
    /// Leeg, ontbrekend of een aanduiding die we niet met zekerheid kunnen plaatsen
    /// (bijv. "hybride"). Filters mogen een onbekend veld <b>nooit</b> wegfilteren: het slot is
    /// aantoonbaar beschikbaar, en het verzwijgen daarvan is schadelijker dan één optie te veel.
    /// </summary>
    Onbekend = 0,
    Kunstgras = 1,
    Natuurgras = 2
}

/// <summary>
/// De enige plek waar deze codebase bepaalt wat "kunstgras" of "natuurgras" betekent (#705, #707).
///
/// <para><b>Waarom niet gewoon <c>== "kunstgras"</c>:</b> het veldtype komt als vrije tekst uit de
/// databron. Een club typt "Kunstgras", "kunstgras 2" of "KG"; een exacte, case-sensitieve
/// vergelijking noemt die allemaal géén kunstgras. Op het e-mailpad bepaalt dat welke velden een
/// aanvrager te zien krijgt.</para>
///
/// <para><b>Fail-safe:</b> alles wat noch als kunstgras noch als natuurgras herkenbaar is, geldt
/// als <see cref="VeldSoort.Onbekend"/> — nooit als natuurgras. Een filter dat natuurgras weglaat
/// gooit zo nooit een veld weg op basis van een gok over een aanduiding die we niet kennen. Nieuwe
/// schrijfwijzen horen hieronder toegevoegd te worden, niet elders herkend.</para>
/// </summary>
public static class VeldTypeClassificatie
{
    private static readonly char[] Scheidingstekens = [' ', '-', '_', '.', ',', '/', '(', ')', '+'];

    // Fragmenten mogen ergens in de tekst staan — veilig voor varianten als "kunstgrasveld 2".
    private static readonly string[] KunstgrasFragmenten =
        ["kunstgras", "kunst gras", "kunst-gras", "kunstveld", "artificial", "artificieel", "artgras"];

    // Losse codes: alleen als heel woord. Als substring geven ze te veel valse treffers
    // ("kg" zit ook in willekeurige woorden).
    private static readonly string[] KunstgrasWoorden = ["kunst", "kg", "art", "3g", "4g", "5g"];

    private static readonly string[] NatuurgrasFragmenten =
        ["natuurgras", "natuur gras", "natuur-gras", "natuurveld", "natural"];

    private static readonly string[] NatuurgrasWoorden = ["natuur", "ng"];

    // Hybride/versterkt gras is géén van beide. Bewust Onbekend: zulke velden mogen niet
    // wegvallen omdat we ze niet kunnen plaatsen.
    private static readonly string[] OnbekendFragmenten = ["hybride", "hybrid", "semi"];

    /// <summary>Classificeert een vrije-tekst veldtype.</summary>
    public static VeldSoort Bepaal(string? veldType)
    {
        if (string.IsNullOrWhiteSpace(veldType)) return VeldSoort.Onbekend;

        var tekst = veldType.Trim().ToLowerInvariant();
        if (OnbekendFragmenten.Any(f => tekst.Contains(f, StringComparison.Ordinal)))
            return VeldSoort.Onbekend;

        var woorden = tekst.Split(Scheidingstekens, StringSplitOptions.RemoveEmptyEntries);

        // Kunstgras eerst: "kunstgras" bevat ook "gras", dus de natuurgras-check mag er niet vóór.
        if (KunstgrasFragmenten.Any(f => tekst.Contains(f, StringComparison.Ordinal))
            || woorden.Any(w => KunstgrasWoorden.Contains(w)))
            return VeldSoort.Kunstgras;

        if (NatuurgrasFragmenten.Any(f => tekst.Contains(f, StringComparison.Ordinal))
            || woorden.Any(w => NatuurgrasWoorden.Contains(w))
            || tekst.Contains("gras", StringComparison.Ordinal))
            return VeldSoort.Natuurgras;

        return VeldSoort.Onbekend;
    }

    /// <summary>Aantoonbaar kunstgras.</summary>
    public static bool IsKunstgras(string? veldType) => Bepaal(veldType) == VeldSoort.Kunstgras;

    /// <summary>
    /// Aantoonbaar natuurgras. Gebruik dit — niet <c>!IsKunstgras(...)</c> — als je iets
    /// wegfiltert: een onbekend veldtype is geen natuurgras.
    /// </summary>
    public static bool IsNatuurgras(string? veldType) => Bepaal(veldType) == VeldSoort.Natuurgras;

    /// <summary>Niet te plaatsen veldtype (leeg, ontbrekend of onbekende aanduiding).</summary>
    public static bool IsOnbekend(string? veldType) => Bepaal(veldType) == VeldSoort.Onbekend;
}

public class VeldBeschikbaarheidInfo
{
    public int VeldNummer { get; set; }
    public TimeOnly BeschikbaarVanaf { get; set; }
    public TimeOnly BeschikbaarTot { get; set; }
    public bool GebruikZonsondergang { get; set; }
}

public class BestaandeWedstrijd
{
    public DateOnly Datum { get; set; }
    public TimeOnly AanvangsTijd { get; set; }
    public TimeOnly EindTijd { get; set; }
    public int VeldNummer { get; set; }
    public decimal VeldDeelGebruik { get; set; }
    public string? VeldSubpositie { get; set; } // A, B, A1, A2, B1, B2 — voor visuele positionering
    public string? LeeftijdsCategorie { get; set; }
    public string? TeamNaam { get; set; }
    public string? Wedstrijd { get; set; }
    // Sportlink-wedstrijdcode — exacte sleutel voor herplan-exclusie (#574).
    // Null voor planner-slots die nog geen Sportlink-tegenhanger hebben.
    public long? Wedstrijdcode { get; set; }
    public string Bron { get; set; } = string.Empty;
}

public class TeamRegel
{
    public string TeamNaam { get; set; } = string.Empty;
    public string RegelType { get; set; } = string.Empty;
    public int? WaardeMinuten { get; set; }
    public int? WaardeVeldNummer { get; set; }
    public TimeOnly? WaardeTijd { get; set; }
    public int Prioriteit { get; set; }
}

/// <summary>
/// Uitgelezen 'VoorkeurVeld'-regel van één team (#666) — het veld waarop dit team bij voorkeur
/// speelt, optioneel met een tijdstip. Prioriteit: laag getal = belangrijker.
/// </summary>
public class TeamVoorkeurVeld
{
    public string TeamNaam { get; set; } = string.Empty;
    public int VeldNummer { get; set; }
    public TimeOnly? Tijd { get; set; }
    public int Prioriteit { get; set; }
}

public class SlotToewijzing
{
    public string Datum { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public string EindTijd { get; set; } = string.Empty;
    public int VeldNummer { get; set; }
    public string VeldNaam { get; set; } = string.Empty;
    public decimal VeldDeelGebruik { get; set; }
    public int WedstrijdDuurMinuten { get; set; }

    /// <summary>
    /// Veldtype ("kunstgras", "natuurgras", …) — reist mee met het slot zodat consumers het niet
    /// uit het veldnummer hoeven te raden (#705): die nummering geldt maar voor één accommodatie.
    /// <c>null</c> betekent onbekend; een filter op veldtype mag zo'n slot dan nooit wegfilteren,
    /// want het slot is aantoonbaar beschikbaar.
    /// </summary>
    public string? VeldType { get; set; }
}

/// <summary>
/// Eén voorgestelde verplaatsing van een wedstrijd, zoals <see cref="PlannerHtmlGenerator"/> die in
/// de HTML-weergave markeert.
/// <para>
/// Verhuisd van <c>FunctionApp/Planner/PlannerModels.cs</c> naar deze gedeelde plek bij issue 888
/// vervolg (§42), samen met <see cref="PlannerHtmlGenerator"/> dat het als invoermodel gebruikt —
/// zelfde precedent als <see cref="BestaandeWedstrijd"/> bij §38.
/// </para>
/// </summary>
public class OptimalisatieSuggestie
{
    public string Wedstrijd { get; set; } = string.Empty;
    public int HuidigVeldNummer { get; set; }
    public string HuidigVeld { get; set; } = string.Empty;
    public string HuidigeTijd { get; set; } = string.Empty;
    public int NieuwVeldNummer { get; set; }
    public string NieuwVeld { get; set; } = string.Empty;
    public string NieuweTijd { get; set; } = string.Empty;
    public string Reden { get; set; } = string.Empty;
}
