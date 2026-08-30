namespace Database.Postgres;

/// <summary>
/// Laadt de DDL voor de operationele configuratietabellen waar
/// <see cref="PostgresPlannerViewGenerator"/> van afhangt (#819): <c>public.appsettings</c>,
/// <c>public.velden</c>, <c>public.speeltijden</c> en <c>planner.geplandewedstrijden</c>.
/// <para>
/// #821: deze DDL leefde eerder als C#-stringconstantes in deze klasse; sinds #821's
/// migratie-mechanisme bestaat, is <c>migrations/001_baseline.sql</c> de enige bron van waarheid.
/// Deze klasse leest nu dat bestand in plaats van een eigen kopie te onderhouden — anders zouden
/// de teststub en de daadwerkelijke migratie stilzwijgend uiteen kunnen lopen.
/// </para>
/// <para>
/// <b>Scope-afbakening (ongewijzigd sinds #819).</b> `001_baseline.sql` dekt bewust nog niet élke
/// configuratietabel — zie de header-comment van dat bestand voor de volledige lijst ontbrekende
/// tabellen.
/// </para>
/// </summary>
public static class PostgresPlannerSupportSchema
{
    /// <summary>
    /// Volledige inhoud van <c>migrations/001_baseline.sql</c>, als één uitvoerbaar SQL-statement-blok
    /// (Npgsql voert een command met meerdere puntkomma-gescheiden statements gewoon in volgorde uit).
    /// </summary>
    public static string BaselineSql => File.ReadAllText(ResolveBaselinePath());

    private static string ResolveBaselinePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Kon de repository-root niet vinden vanaf AppContext.BaseDirectory.");

        return Path.Combine(dir.FullName, "Database.Postgres", "migrations", "001_baseline.sql");
    }
}
