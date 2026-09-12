using Database.Postgres;

// #821: minimale CLI-entrypoint voor MigrationRunner. Wachtwoord/connectiestring komt uitsluitend
// via de omgevingsvariabele POSTGRES_CONNECTION_STRING binnen — nooit als CLI-argument, zelfde
// regel als sqlcmd/SQLCMDPASSWORD elders in dit project: argumenten zijn op elk platform zichtbaar
// in de processenlijst.
var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Fout: omgevingsvariabele POSTGRES_CONNECTION_STRING is niet gezet.");
    return 1;
}

// #1060: tweede modus naast het toepassen van migraties. his.teams/his.matches/his.matchdetails
// worden door geen enkel migratiebestand aangemaakt — PostgresSchemaGenerator doet dat dynamisch
// zodra de ETL zijn eerste sync draait. Op een verse ontwikkeldatabase bestaan ze dus niet, en
// scripts/migrations/003-seed-allstars-demo-matches-postgres.sql weigert daarop (terecht) te
// draaien. Deze vlag maakt ze aan langs exact dezelfde weg als de ETL zelf, zodat er geen
// handgeschreven DDL-kopie bijkomt naast die van de zelftest en de CI-job.
var ensureHisTables = args.Contains("--ensure-his-tables", StringComparer.Ordinal);
var positioneel = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

var normalized = PostgresConnectionStringNormalizer.Normalize(connectionString);

if (ensureHisTables)
{
    try
    {
        var orchestrator = new PostgresMergeOrchestrator(normalized);
        foreach (var entity in KnownEntities.All)
        {
            await orchestrator.EnsureHisTableAsync(entity);
            Console.WriteLine($"his.{entity.EntityName} gereed.");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Aanmaken van de his-tabellen mislukt: {ex.Message}");
        return 1;
    }
}

var migrationsPath = positioneel.Length > 0 ? positioneel[0] : ResolveDefaultMigrationsPath();

try
{
    await MigrationRunner.RunAsync(normalized, migrationsPath);
    Console.WriteLine($"Migraties toegepast vanuit '{migrationsPath}'.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Migratie mislukt: {ex.Message}");
    return 1;
}

// Zelfde "loop omhoog tot .sln gevonden"-patroon als VeldResolutieDriftTests/PostgresPlannerSupportSchema
// — werkt ongeacht of dit via 'dotnet run' vanuit de projectmap of tegen een build-output draait.
static string ResolveDefaultMigrationsPath()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
        dir = dir.Parent;

    if (dir is null)
        throw new InvalidOperationException(
            "Kon de repository-root niet vinden — geef de migratiemap expliciet mee als argument.");

    return Path.Combine(dir.FullName, "Database.Postgres", "migrations");
}
