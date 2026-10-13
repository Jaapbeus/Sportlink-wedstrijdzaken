namespace Planner.Shared;

/// <summary>
/// Bouwt LIKE-patronen uit vrije tekst: <c>%</c>, <c>_</c>, <c>[</c> en de escape-tekens zelf worden
/// letterlijk gezocht. Gebruik samen met <c>ESCAPE '\'</c> (SQL Server vereist die clausule; Postgres
/// kent <c>\</c> al als standaard escape-teken).
/// </summary>
public static class LikePatroon
{
    /// <summary>Maakt wildcards in <paramref name="tekst"/> onschadelijk (\ % _ [).</summary>
    public static string Escape(string? tekst) =>
        (tekst ?? "").Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    /// <summary>Substring-patroon <c>%tekst%</c> met ge-escapete tekst.</summary>
    public static string Bevat(string? tekst) => "%" + Escape(tekst) + "%";
}
