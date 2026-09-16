namespace BlazorAdmin.Services;

/// <summary>
/// Top/hoogte-berekening voor één Gantt-balk in de Dagplanning-tijdlijn (#1194), als percentage van
/// de vaste rijhoogte (72px, zie <c>Dagplanning.razor</c>). Losgetrokken uit
/// <c>Dagplanning.razor.cs</c> zodat de kernlogica van deze fix — top én hoogte uit dezelfde
/// subpositie-suffix, met een defensieve clamp — unit-testbaar is zonder de rest van de pagina op
/// te tuigen.
/// <para>
/// <b>Vóór deze fix</b> kwam de verticale positie (<c>Top</c>) uit de subpositie-suffix die
/// Sportlink zelf in de veld-tekst meegeeft ("veld 3 A" → boven, "veld 3 B" → onder), terwijl de
/// hoogte uitsluitend uit de server-<c>Veldafmeting</c> kwam — een teaminstelling uit
/// <c>public.speeltijden</c>, losgekoppeld van wat Sportlink voor déze specifieke wedstrijd
/// meegeeft. Een team met "heel veld" als standaard dat ad-hoc een halve-veld-boeking kreeg (zoals
/// "V+1") kreeg zo een balk die half positioneerde maar heel hoog tekende — hij liep door in de rij
/// van het volgende veld. Zie <c>FunctionApp.Postgres.Planner.AutoPlanService.VeldafmetingVoorWedstrijd</c>
/// voor de server-kant van dezelfde fix: die berekent <c>Veldafmeting</c> nu ook primair uit de
/// subpositie, met de teaminstelling alleen nog als terugval.
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
