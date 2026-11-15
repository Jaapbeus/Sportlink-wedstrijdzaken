using System;
using System.Collections.Generic;
using System.Linq;

namespace Planner.Shared.Planning;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// #1430: de ene bron van waarheid voor de conflictregels van de veldplanning.
//
// Dit bestand wordt op twee plekken gecompileerd:
//   1. in Planner.Shared zelf — de automatische planner (FieldScheduler) gebruikt deze regels;
//   2. als gelinkt bronbestand in BlazorAdmin (zie BlazorAdmin.csproj) — de handmatige
//      conflictcontrole na het verslepen op "Veld optimalisatie" gebruikt exact dezelfde regels.
//
// Waarom een gelinkt bestand en geen projectreferentie: BlazorAdmin (WASM) refereert bewust niet
// aan Planner.Shared — dat project trekt Azure.Identity, AngleSharp en Newtonsoft.Json mee, die in
// de browser niets te zoeken hebben. Dit bestand mag daarom UITSLUITEND van de BCL afhangen: geen
// andere Planner.Shared-typen, geen NuGet-pakketten. Een afhankelijkheid toevoegen breekt de
// BlazorAdmin-build, en dat is precies de bedoeling (de grens is dan zichtbaar, niet stil).
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Pure buffer- en veldbaanregels die de automatische planner en de handmatige conflictcontrole
/// delen (#1430). Vóór deze klasse stond de buffermax-regel drie keer in de code (FieldScheduler,
/// AutoPlanService per tier) en kende de client hem helemaal niet: die vergeleek alleen met de
/// algemene buffer, waardoor een teamspecifieke <c>BufferVoor</c>/<c>BufferNa</c> na een handmatige
/// zet stilzwijgend werd genegeerd.
/// </summary>
public static class PlanningBufferRegels
{
    /// <summary>
    /// Effectieve buffer voor één kant (voor of na) van een team: de teamregel telt alleen als hij
    /// groter is dan de algemene buffer. Een ontbrekende of lagere teamregel valt terug op de
    /// algemene buffer — een teamregel kan de buffer dus nooit verkleinen.
    /// </summary>
    public static int Effectief(int algemeneBuffer, int? teamRegelMinuten) =>
        teamRegelMinuten is int t && t > algemeneBuffer ? t : algemeneBuffer;

    /// <summary>
    /// De ruwe teamregels van <paramref name="teamNaam"/> voor het auto-plancontract (#1430):
    /// <c>null</c> per kant als er geen (positieve) regel is. Bewust niet de effectieve waarde: de
    /// client combineert ze met de algemene buffer die op dat moment is ingesteld.
    /// </summary>
    public static (int? Voor, int? Na) TeamRegel(
        IReadOnlyDictionary<string, (int bufferVoor, int bufferNa)> teamBuffers, string? teamNaam)
    {
        if (string.IsNullOrEmpty(teamNaam) || !teamBuffers.TryGetValue(teamNaam, out var b)) return (null, null);
        return (b.bufferVoor > 0 ? b.bufferVoor : null, b.bufferNa > 0 ? b.bufferNa : null);
    }

    /// <summary>
    /// Vereiste ruimte tussen twee wedstrijden die elkaar opvolgen: de grootste van de
    /// <c>BufferNa</c> van de voorganger en de <c>BufferVoor</c> van de opvolger (richtinggevoelig).
    /// </summary>
    public static int VereisteTussenruimte(int voorgangerBufferNa, int opvolgerBufferVoor) =>
        Math.Max(voorgangerBufferNa, opvolgerBufferVoor);

    /// <summary>Overlappen twee halfopen tijdvakken <c>[start, eind)</c> (in minuten vanaf middernacht)?</summary>
    public static bool Overlappen(int aStart, int aEind, int bStart, int bEind) =>
        aStart < bEind && aEind > bStart;

    /// <summary>
    /// Is de ruimte tussen een voorganger (eindigt op <paramref name="voorgangerEind"/>) en een
    /// opvolger (start op <paramref name="opvolgerStart"/>) kleiner dan de vereiste buffer? Een gat
    /// exact gelijk aan de buffer is géén conflict. Rekent in minuten, niet met
    /// <see cref="TimeOnly.AddMinutes(double)"/>: dat wikkelt over middernacht en liet een conflict
    /// vlak voor 24:00 stil passeren.
    /// </summary>
    public static bool BufferTeKort(int voorgangerEind, int voorgangerBufferNa, int opvolgerStart, int opvolgerBufferVoor)
    {
        int gat = opvolgerStart - voorgangerEind;
        return gat >= 0 && gat < VereisteTussenruimte(voorgangerBufferNa, opvolgerBufferVoor);
    }

    /// <summary>Minuten vanaf middernacht.</summary>
    public static int Minuut(TimeOnly tijd) => tijd.Hour * 60 + tijd.Minute;

    /// <summary>
    /// Welke kwartbanen bezet een wedstrijd met deze subpositie? Index 0=A1, 1=A2, 2=B1, 3=B2;
    /// "A"/"B" is een helft, leeg of onbekend is het hele veld.
    /// </summary>
    public static bool[] BanenVanSubpositie(string? subpositie)
    {
        var banen = new bool[4];
        switch ((subpositie ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "A1": banen[0] = true; break;
            case "A2": banen[1] = true; break;
            case "B1": banen[2] = true; break;
            case "B2": banen[3] = true; break;
            case "A":  banen[0] = banen[1] = true; break;
            case "B":  banen[2] = banen[3] = true; break;
            default:   banen[0] = banen[1] = banen[2] = banen[3] = true; break;
        }
        return banen;
    }

    /// <summary>
    /// Botsen twee gelijktijdige wedstrijden fysiek op het veld? Op banen vergeleken, niet op de som
    /// van de veldfracties: een half veld op A plus een kwart op A1 telt op tot 0,75 maar botst wél.
    /// </summary>
    public static bool BanenBotsen(string? subpositieA, string? subpositieB)
    {
        var a = BanenVanSubpositie(subpositieA);
        var b = BanenVanSubpositie(subpositieB);
        for (int k = 0; k < 4; k++)
            if (a[k] && b[k]) return true;
        return false;
    }
}

/// <summary>Soort conflict, in de vaste meldingsvolgorde: eerst per veld, dan per team.</summary>
public enum PlanningConflictSoort
{
    /// <summary>Gelijktijdig op hetzelfde veldgedeelte.</summary>
    VeldOverlap,
    /// <summary>Achter elkaar op hetzelfde veld met te weinig buffer.</summary>
    VeldBuffer,
    /// <summary>Hetzelfde team gelijktijdig op twee plekken.</summary>
    TeamOverlap,
    /// <summary>Hetzelfde team achter elkaar met te weinig buffer.</summary>
    TeamBuffer,
}

/// <summary>
/// Eén geplande wedstrijd als invoer voor <see cref="PlanningConflictDetectie"/>. De teambuffers
/// zijn de ruwe teamregelwaarden (<c>null</c> = geen regel); de effectieve buffer wordt pas in de
/// detectie bepaald, met de algemene buffer van dat moment.
/// </summary>
/// <param name="Sleutel">Vrij te kiezen verwijzing naar de bron (bijv. het DTO), voor de aanroeper.</param>
public sealed record PlanningSlot(
    string TeamNaam,
    string? VeldNaam,
    string? Subpositie,
    TimeOnly Start,
    int DuurMinuten,
    int? TeamBufferVoor = null,
    int? TeamBufferNa = null,
    object? Sleutel = null)
{
    /// <summary>Start in minuten vanaf middernacht.</summary>
    public int StartMinuut => PlanningBufferRegels.Minuut(Start);

    /// <summary>Einde in minuten vanaf middernacht; kan boven 1440 uitkomen, wikkelt bewust niet.</summary>
    public int EindMinuut => StartMinuut + DuurMinuten;
}

/// <summary>
/// Eén gevonden conflict. <paramref name="Eerste"/> start niet later dan <paramref name="Tweede"/>.
/// <paramref name="GatMinuten"/> en <paramref name="VereisteBufferMinuten"/> zijn alleen zinvol bij
/// de buffersoorten (anders 0).
/// </summary>
/// <param name="Groep">De veldnaam (veldconflict) of de teamnaam (teamconflict).</param>
public sealed record PlanningConflict(
    PlanningConflictSoort Soort,
    string Groep,
    PlanningSlot Eerste,
    PlanningSlot Tweede,
    int GatMinuten,
    int VereisteBufferMinuten)
{
    /// <summary>Komt de vereiste buffer uit een teamregel (groter dan de algemene buffer)?</summary>
    public bool DoorTeamregel(int algemeneBuffer) => VereisteBufferMinuten > algemeneBuffer;
}

/// <summary>
/// Conflictdetectie over een volledige planning (#1430, uit <c>VeldOptimalisatie.razor.cs</c>
/// gehaald). Toetst dezelfde regels die <see cref="PlanningBufferRegels"/> aan de automatische
/// planner levert: per veld velddeeloverlap en buffer, per team gelijktijdigheid en buffer over alle
/// velden heen (#939). Worst-case kwadratisch in de groepsgrootte — bewust ongeoptimaliseerd: een
/// speeldag telt enkele tientallen wedstrijden, er is geen gemeten bottleneck.
/// </summary>
public static class PlanningConflictDetectie
{
    /// <summary>
    /// Alle conflicten, in stabiele volgorde: eerst per veld (velden in volgorde van eerste
    /// voorkomen), dan per team; binnen een groep op starttijd (stabiel bij gelijke tijden).
    /// Slots zonder duur worden genegeerd; zonder veldnaam tellen ze alleen mee voor de
    /// teamcontrole, zonder teamnaam alleen voor de veldcontrole.
    /// </summary>
    public static IReadOnlyList<PlanningConflict> Detecteer(IEnumerable<PlanningSlot> slots, int algemeneBuffer)
    {
        var geldig = slots.Where(s => s.DuurMinuten > 0).ToList();
        var conflicten = new List<PlanningConflict>();

        foreach (var veld in geldig.Where(s => !string.IsNullOrEmpty(s.VeldNaam)).GroupBy(s => s.VeldNaam!))
            VoegPaarConflictenToe(conflicten, veld.Key, veld, algemeneBuffer, perVeld: true);

        foreach (var team in geldig.Where(s => !string.IsNullOrWhiteSpace(s.TeamNaam))
                     .GroupBy(s => s.TeamNaam, StringComparer.OrdinalIgnoreCase))
            VoegPaarConflictenToe(conflicten, team.First().TeamNaam, team, algemeneBuffer, perVeld: false);

        return conflicten;
    }

    private static void VoegPaarConflictenToe(List<PlanningConflict> conflicten, string groep,
        IEnumerable<PlanningSlot> groepSlots, int algemeneBuffer, bool perVeld)
    {
        var lijst = groepSlots.OrderBy(s => s.StartMinuut).ToList();
        for (int i = 0; i < lijst.Count; i++)
            for (int j = i + 1; j < lijst.Count; j++)
            {
                var conflict = BeoordeelPaar(groep, lijst[i], lijst[j], algemeneBuffer, perVeld);
                if (conflict != null) conflicten.Add(conflict);
            }
    }

    /// <summary>
    /// Beoordeelt één paar (<paramref name="a"/> start niet later dan <paramref name="b"/>).
    /// Gelijktijdig: per veld alleen een conflict als de banen botsen, per team altijd.
    /// Achter elkaar: buffer = grootste van BufferNa(a) en BufferVoor(b), elk minstens de algemene.
    /// </summary>
    public static PlanningConflict? BeoordeelPaar(string groep, PlanningSlot a, PlanningSlot b,
        int algemeneBuffer, bool perVeld)
    {
        if (PlanningBufferRegels.Overlappen(a.StartMinuut, a.EindMinuut, b.StartMinuut, b.EindMinuut))
        {
            if (!perVeld)
                return new PlanningConflict(PlanningConflictSoort.TeamOverlap, groep, a, b, 0, 0);
            return PlanningBufferRegels.BanenBotsen(a.Subpositie, b.Subpositie)
                ? new PlanningConflict(PlanningConflictSoort.VeldOverlap, groep, a, b, 0, 0)
                : null;
        }

        int bufferNa = PlanningBufferRegels.Effectief(algemeneBuffer, a.TeamBufferNa);
        int bufferVoor = PlanningBufferRegels.Effectief(algemeneBuffer, b.TeamBufferVoor);
        if (!PlanningBufferRegels.BufferTeKort(a.EindMinuut, bufferNa, b.StartMinuut, bufferVoor))
            return null;

        return new PlanningConflict(
            perVeld ? PlanningConflictSoort.VeldBuffer : PlanningConflictSoort.TeamBuffer,
            groep, a, b, b.StartMinuut - a.EindMinuut,
            PlanningBufferRegels.VereisteTussenruimte(bufferNa, bufferVoor));
    }
}
