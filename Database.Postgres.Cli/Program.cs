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

var normalization = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(connectionString);
var normalized = normalization.ConnectionString;

// #1095: het TLS-beleid van #1004 is niet meer fail-closed; wél zichtbaar. Naar stderr, zodat het
// in CI en in een handmatige migratieronde opvalt zonder de uitvoer van de migraties zelf te storen.
if (normalization.TlsWarning is not null)
    Console.Error.WriteLine($"Waarschuwing: {normalization.TlsWarning}");

if (ensureHisTables)
{
    // Welke stap liep toen het misging? Dat is de enige contextinformatie die de foutregel nog
    // mag dragen (#1225) — de entiteitsnaam komt uit KnownEntities, niet uit de connectiestring.
    string? huidigeEntiteit = null;
    try
    {
        var orchestrator = new PostgresMergeOrchestrator(normalized);
        foreach (var entity in KnownEntities.All)
        {
            huidigeEntiteit = $"his.{entity.EntityName}";
            await orchestrator.EnsureHisTableAsync(entity);
            Console.WriteLine($"his.{entity.EntityName} gereed.");
        }
        return 0;
    }
    catch (Exception ex)
    {
        // #1225: nooit ex.Message — zie MigratieFoutRapportage voor het waarom.
        Console.Error.WriteLine(MigratieFoutRapportage.Beschrijf("Aanmaken van de his-tabellen", ex, huidigeEntiteit));
        return 1;
    }
}

var migrationsPath = positioneel.Length > 0 ? positioneel[0] : ResolveDefaultMigrationsPath();

// Laatst gestarte migratiebestand — bij een fout is dat de stap die faalde. Dit is de vervanger
// van de exception-tekst, niet een aanvulling erop (#1225).
string? huidigeMigratie = null;

try
{
    var result = await MigrationRunner.RunAsync(normalized, migrationsPath, onMigratieStart: naam => huidigeMigratie = naam);
    Console.WriteLine(
        $"Migraties toegepast vanuit '{migrationsPath}': {result.Applied.Count} nieuw, " +
        $"{result.AlreadyApplied.Count} al toegepast, {result.ChecksumNormalized.Count} ledger-checksum(s) genormaliseerd.");
    foreach (var naam in result.Applied)
        Console.WriteLine($"  + {naam}");
    // #1112: een omgeschreven ledger-checksum is geen fout, maar moet wél opvallen in een deploy-log
    // of handmatige ronde — het bewijst dat die database ooit vanaf een CRLF-checkout is gemigreerd.
    foreach (var naam in result.ChecksumNormalized)
        Console.Error.WriteLine($"Waarschuwing: ledger-checksum van '{naam}' was een rauwe CRLF-waarde en is genormaliseerd naar LF (#1112) — inhoud ongewijzigd.");
    return 0;
}
catch (Exception ex)
{
    // #1225: deze regel verscheen in de publieke Actions-log van db-migrate-postgres. Een
    // Npgsql-verbindingsfout noemt host en poort, een authenticatiefout de gebruikersnaam —
    // allemaal deelstrings van POSTGRES_CONNECTION_STRING, die GitHub niet maskeert.
    Console.Error.WriteLine(MigratieFoutRapportage.Beschrijf("Migratie", ex, huidigeMigratie));
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
