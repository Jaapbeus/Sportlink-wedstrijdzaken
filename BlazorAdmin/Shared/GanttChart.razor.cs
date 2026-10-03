using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using static BlazorAdmin.Services.DagplanningWeergaveHelpers;

namespace BlazorAdmin.Shared;

/// <summary>
/// Gedeelde Gantt-tijdlijn voor Planning (read-only veldbezetting) en Veld optimalisatie (sleepbaar,
/// met voorkeurtijd-indicator en conflictmarkering) (#1491). Vóór deze extractie stond dezelfde
/// markup letterlijk twee keer in de pagina's. De component bevat uitsluitend weergave; de pagina
/// bepaalt de items en wat een sleepactie doet.
/// </summary>
public partial class GanttChart
{
    /// <summary>Argumenten van een start van een sleepactie op een blok.</summary>
    public sealed record SleepStartArgs(GanttItem Item, DragEventArgs Event);

    /// <summary>Argumenten van een drop op een veldrij. <c>StartMinuut</c>/<c>TotaalMinuten</c> beschrijven de getoonde tijdas.</summary>
    public sealed record SleepDropArgs(string VeldNaam, int RijIndex, int StartMinuut, int TotaalMinuten, DragEventArgs Event);

    [Parameter, EditorRequired] public IReadOnlyList<GanttItem> Items { get; set; } = [];

    /// <summary>Gedeelde hover-correlatie met de tabel van de pagina.</summary>
    [Parameter] public GanttHoverState? Hover { get; set; }

    /// <summary>Wordt aangeroepen nadat <see cref="Hover"/> is gewijzigd, zodat de pagina (en dus de tabel) opnieuw rendert.</summary>
    [Parameter] public EventCallback HoverGewijzigd { get; set; }

    /// <summary>Blokken krijgen de sleepvariant (grijpcursor, voorkeurbalk, drop-targets) — Veld optimalisatie.</summary>
    [Parameter] public bool Sleepbaar { get; set; }

    /// <summary>Drempel (minuten) tussen een kleine en grote voorkeurafwijking; alleen relevant bij <see cref="Sleepbaar"/>.</summary>
    [Parameter] public int KleineAfwijkingDrempelMinuten { get; set; }

    /// <summary>Is dit item handmatig verplaatst (stippellijn)? Alleen relevant bij <see cref="Sleepbaar"/>.</summary>
    [Parameter] public Func<GanttItem, bool>? IsHandmatigAangepast { get; set; }

    [Parameter] public EventCallback<SleepStartArgs> OnSleepStart { get; set; }

    [Parameter] public EventCallback<SleepDropArgs> OnSleepDrop { get; set; }

    /// <summary>Velden in weergavevolgorde: Kunstgras eerst, daarna alfabetisch.</summary>
    public static List<string> OrdenVelden(IEnumerable<GanttItem> items)
        => items.Select(g => g.VeldNaam).Distinct()
            .OrderBy(v => v.StartsWith("Kunstgras", StringComparison.OrdinalIgnoreCase) ? "0" + v : "1" + v)
            .ToList();

    /// <summary>Tijdas afgerond op hele uren: (startminuut, eindminuut, totaal), minimaal een uur breed.</summary>
    public static (int Start, int Eind, int Totaal) BerekenTijdAs(IReadOnlyCollection<GanttItem> items)
    {
        int start = (items.Min(g => (int)g.Aanvang.ToTimeSpan().TotalMinutes) / 60) * 60;
        int eind = (((int)items.Max(g => g.Einde.ToTimeSpan().TotalMinutes) + 59) / 60) * 60;
        if (eind <= start) eind = start + 60;
        return (start, eind, eind - start);
    }

    private async Task ZetHoverAsync(long? wedstrijdCode)
    {
        Hover?.Zet(wedstrijdCode);
        await HoverGewijzigd.InvokeAsync();
    }

    private async Task WisHoverAsync()
    {
        Hover?.Wis();
        await HoverGewijzigd.InvokeAsync();
    }

    private string BlokHoverKlasse(GanttItem gi) => Hover?.BlokKlasse(gi.WedstrijdCode) ?? "";
}
