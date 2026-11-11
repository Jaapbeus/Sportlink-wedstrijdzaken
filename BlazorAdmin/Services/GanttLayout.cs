namespace BlazorAdmin.Services;

/// <summary>
/// Calculates the top and height of one Gantt bar in the Planning page as percentages of the fixed
/// row height (72px; see <c>Shared/GanttChart.razor.css</c>). Both values use the same Sportlink subposition
/// suffix; server-side <c>Veldafmeting</c> is a height fallback when the suffix is absent or
/// unrecognized.
/// This keeps partial-field bookings from extending into the next row. A defensive clamp keeps
/// every bar within its row.
/// <para>
/// The server derives <c>Veldafmeting</c> from the same subposition first and uses the team setting
/// only as a fallback.
/// </para>
/// </summary>
public static class GanttLayout
{
    private static double Top(string? subpositie) => subpositie switch
    {
        "A" or "A1" => 0, "A2" => 25, "B" or "B1" => 50, "B2" => 75, _ => 0
    };

    /// <summary>
    /// Primaire bron is dezelfde subpositie-suffix als <see cref="Top"/> gebruikt.
    /// <paramref name="fractieTerugval"/> (de server-Veldafmeting) is alleen nog de terugval voor
    /// wanneer Sportlink geen suffix meegeeft — dezelfde volgorde als de server-kant.
    /// </summary>
    private static double Hoogte(string? subpositie, decimal fractieTerugval) => subpositie switch
    {
        "A1" or "A2" or "B1" or "B2" => 25,
        "A" or "B" => 50,
        _ => fractieTerugval switch { <= 0.26m => 25, <= 0.51m => 50, _ => 100 }
    };

    /// <summary>
    /// Top + hoogte voor één Gantt-balk, met een defensieve clamp: ongeacht welke combinatie van
    /// bron-data <see cref="Top"/>/<see cref="Hoogte"/> oplevert, mag een balk nooit buiten zijn
    /// eigen rij renderen. Dit is het laatste vangnet, niet de primaire fix — de primaire fix is dat
    /// beide nu uit dezelfde subpositie komen (zie <see cref="Hoogte"/>'s doc-comment).
    /// </summary>
    public static (double Top, double Hoogte) Bereken(string? subpositie, decimal fractieTerugval)
    {
        var top = Top(subpositie);
        var hoogte = Hoogte(subpositie, fractieTerugval);
        if (top + hoogte > 100) hoogte = Math.Max(0, 100 - top);
        return (top, hoogte);
    }
}
