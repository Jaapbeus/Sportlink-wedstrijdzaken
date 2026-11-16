namespace Planner.Shared.Planning;

/// <summary>Begrensde vooruitblik voor de kernpagina en de bestaande voorbereidingstimer.</summary>
public static class PlanningVooruitblik
{
    public const int AantalZaterdagen = 4;

    /// <summary>Vier zaterdagen vanaf de gekozen dag, inclusief de dag zelf als die zaterdag is.</summary>
    public static IReadOnlyList<DateOnly> ZaterdagenVanaf(DateOnly datum)
    {
        var eerste = datum.AddDays(((int)DayOfWeek.Saturday - (int)datum.DayOfWeek + 7) % 7);
        return Enumerable.Range(0, AantalZaterdagen).Select(i => eerste.AddDays(7 * i)).ToArray();
    }

    /// <summary>Bekende speeldagen eerst, daarna ontbrekende zaterdagen; alles binnen het bestaande 28-dagenvenster.</summary>
    public static IReadOnlyList<DateOnly> VoorbereidDatums(DateOnly vandaag, IEnumerable<DateOnly> bekend)
        => bekend.Where(d => d >= vandaag && d <= vandaag.AddDays(28)).Order().Concat(ZaterdagenVanaf(vandaag))
            .Distinct().ToArray();
}
