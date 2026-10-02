using BlazorAdmin.Models;
using Planner.Shared.Planning;

namespace BlazorAdmin.Services;

/// <summary>
/// Vertaalt een (handmatig aangepaste) auto-planning naar de conflictmeldingen op "Veld
/// optimalisatie" (#1430). De beslislogica zelf zit in <see cref="PlanningConflictDetectie"/> — het
/// gelinkte bestand dat de automatische planner ook gebruikt; hier alleen de vertaling van DTO naar
/// invoer en van resultaat naar Nederlandse tekst.
/// </summary>
public static class VeldplanningConflictMeldingen
{
    /// <summary>
    /// Conflictmeldingen voor de optimale planning, met de nu ingestelde algemene buffer. Wedstrijden
    /// zonder geldige tijd of zonder duur tellen niet mee; zonder veldnaam alleen voor de teamcontrole.
    /// </summary>
    public static List<string> Bepaal(IEnumerable<AutoPlanWedstrijdItemDto> wedstrijden, int algemeneBuffer)
    {
        var slots = new List<PlanningSlot>();
        foreach (var w in wedstrijden)
        {
            if (w.OptimaalTijd == null || w.DuurMinuten <= 0 || !TimeOnly.TryParse(w.OptimaalTijd, out var start))
                continue;
            slots.Add(new PlanningSlot(w.TeamNaam ?? string.Empty, w.OptimaalVeldNaam,
                DagplanningWeergaveHelpers.GanttExtractSubPos(w.OptimaalVeld), start, w.DuurMinuten,
                w.TeamBufferVoor, w.TeamBufferNa, w));
        }

        return PlanningConflictDetectie.Detecteer(slots, algemeneBuffer)
            .Select(c => Formatteer(c, algemeneBuffer))
            .ToList();
    }

    /// <summary>Eén melding. De teksten zijn gelijk aan die van vóór #1430; alleen een buffer die uit een
    /// teamregel komt wordt nu benoemd, omdat "de ingestelde buffer" dan niet het getal is dat de
    /// gebruiker op het scherm ziet staan.</summary>
    public static string Formatteer(PlanningConflict c, int algemeneBuffer)
    {
        var a = c.Eerste;
        var b = c.Tweede;
        var buffer = c.DoorTeamregel(algemeneBuffer)
            ? $"de vereiste teambuffer van {c.VereisteBufferMinuten} min"
            : $"de ingestelde buffer van {c.VereisteBufferMinuten} min";
        return c.Soort switch
        {
            PlanningConflictSoort.VeldOverlap =>
                $"{c.Groep}: {a.TeamNaam} en {b.TeamNaam} staan op hetzelfde veldgedeelte op dezelfde tijd.",
            PlanningConflictSoort.VeldBuffer =>
                $"{c.Groep}: tussen {a.TeamNaam} en {b.TeamNaam} zit {c.GatMinuten} min, minder dan {buffer}.",
            PlanningConflictSoort.TeamOverlap =>
                $"{c.Groep}: staat tegelijk ingepland op {a.VeldNaam} en {b.VeldNaam} om {a.Start:HH\\:mm}.",
            _ =>
                $"{c.Groep}: tussen de wedstrijd op {a.VeldNaam} en die op {b.VeldNaam} zit {c.GatMinuten} min, minder dan {buffer}.",
        };
    }
}
