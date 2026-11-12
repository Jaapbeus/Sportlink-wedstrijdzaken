namespace Planner.Shared.Calendars;

/// <summary>Post-query rules for ordered, query-eligible KNVB dates.</summary>
public static class KnvbKalenderSelectie
{
    // Each native query owns date range, day-type comparison and ordering. In particular,
    // SQL Server collation and Postgres text comparison must not be reinterpreted here.
    public static List<DateOnly> SelecteerVrijeZaterdagen(
        IEnumerable<DateOnly> dagen, ISet<DateOnly> reedsBezetteData, int maxAantal)
    {
        return dagen
            .Where(datum => datum.DayOfWeek == DayOfWeek.Saturday)
            .Where(datum => !reedsBezetteData.Contains(datum))
            .Take(maxAantal)
            .ToList();
    }
}
