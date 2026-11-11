namespace Planner.Shared.Calendars;

/// <summary>A KNVB calendar date returned by a tier-specific calendar query.</summary>
public sealed record KnvbKalenderDag(DateOnly Datum, string DagType);

/// <summary>Pure selection rules shared by the SQL Server and Postgres KNVB repositories.</summary>
public static class KnvbKalenderSelectie
{
    private static readonly HashSet<string> DagTypesMetWedstrijden = new(StringComparer.Ordinal)
    {
        "Competitie",
        "Beker",
        "Inhaal",
    };

    public static List<DateOnly> SelecteerVrijeZaterdagen(
        IEnumerable<KnvbKalenderDag> dagen,
        DateOnly van,
        DateOnly totEnMet,
        ISet<DateOnly> reedsBezetteData,
        int maxAantal)
    {
        if (maxAantal <= 0 || van > totEnMet)
            return [];

        return dagen
            .Where(dag => DagTypesMetWedstrijden.Contains(dag.DagType))
            .Where(dag => dag.Datum >= van && dag.Datum <= totEnMet)
            .Where(dag => dag.Datum.DayOfWeek == DayOfWeek.Saturday)
            .Where(dag => !reedsBezetteData.Contains(dag.Datum))
            .Select(dag => dag.Datum)
            .Distinct()
            .OrderBy(datum => datum)
            .Take(maxAantal)
            .ToList();
    }
}
