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

// #1246: derde modus. Het demodata-seedscript staat bewust buiten Database.Postgres/migrations/ --
// het vult his.teams/his.matches, die geen migratie aanmaakt -- en werd daardoor nergens in de
// pipeline uitgevoerd. Deze vlag geeft deploy.yml een manier om het alsnog te draaien zonder de
// connectiestring door een psql-aanroep te halen (argumenten staan in de processenlijst en in het
// joblog). Het script is idempotent, dus herhaald draaien is veilig en bovendien nodig: de
// speeltijden-copy hangt af van data die de beheerder pas later invoert.
var seedDemodataIndex = Array.IndexOf(args, "--seed-demodata");
var seedDemodata = seedDemodataIndex >= 0;

var normalization = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(connectionString);
var normalized = normalization.ConnectionString;

// #1095: het TLS-beleid van #1004 is niet meer fail-closed; wél zichtbaar. Naar stderr, zodat het
// in CI en in een handmatige migratieronde opvalt zonder de uitvoer van de migraties zelf te storen.
if (normalization.TlsWarning is not null)
    Console.Error.WriteLine($"Waarschuwing: {normalization.TlsWarning}");

if (seedDemodata)
{
    var scriptPad = seedDemodataIndex + 1 < args.Length && !args[seedDemodataIndex + 1].StartsWith("--", StringComparison.Ordinal)
        ? args[seedDemodataIndex + 1]
        : Path.Combine(ResolveRepoRoot(), "scripts", "migrations", "003-seed-allstars-demo-matches-postgres.sql");

    try
    {
        var telling = await DemodataSeeder.RunAsync(normalized, scriptPad);
        if (telling is null)
        {
            // Een fork mag de democlub weghalen; dat is geen deployfout (#1246).
            Console.WriteLine(
                $"Democlub {DemodataSeeder.DemoClubCode} staat niet in public.appsettings — demodata overgeslagen.");
            return 0;
        }

        Console.WriteLine($"Demodata geseed vanuit '{Path.GetFileName(scriptPad)}' (club {DemodataSeeder.DemoClubCode}):");
        Console.WriteLine($"  his.teams           {Beschrijf(telling.HisTeams)}");
        Console.WriteLine($"  public.teams        {Beschrijf(telling.PublicTeams)}");
        Console.WriteLine($"  his.matches         {Beschrijf(telling.HisMatches)}");
        Console.WriteLine($"  avg.teambegeleiding {Beschrijf(telling.Teambegeleiding)}");
        Console.WriteLine($"  public.speeltijden  {Beschrijf(telling.Speeltijden)}");

        // Geen fout: public.teams is afgeleid en wordt opgebouwd door POST /api/beheer/teams/herstel,
        // dat RequireAdmin is. Een pipeline heeft geen Entra-token, dus dit blijft een handeling van
        // de beheerder -- maar hij moet hem wel kunnen zien (#1246).
        if (telling.CanoniekeLijstOntbreekt)
            Console.Error.WriteLine(
                "DEMOCLUB_CANONIEKE_LIJST_ONTBREEKT: his.teams is gevuld maar public.teams is leeg. " +
                "Bouw de canonieke teamlijst op via de knop op de pagina Teamaliassen (POST /api/beheer/teams/herstel) " +
                "met de democlub geselecteerd -- de pipeline kan dat endpoint niet aanroepen (RequireAdmin).");

        return 0;
    }
    catch (Exception ex)
    {
        // #1225: nooit ex.Message -- zelfde reden als hieronder.
        Console.Error.WriteLine(MigratieFoutRapportage.Beschrijf("Seeden van de demodata", ex, Path.GetFileName(scriptPad)));
        return 1;
    }
}

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

static string Beschrijf(int aantal) => aantal < 0 ? "(tabel bestaat nog niet)" : aantal.ToString();

// Zelfde "loop omhoog tot .sln gevonden"-patroon als VeldResolutieDriftTests/PostgresPlannerSupportSchema
// — werkt ongeacht of dit via 'dotnet run' vanuit de projectmap of tegen een build-output draait.
// ResolveRepoRoot is alleen een terugval: deploy.yml geeft het seedscriptpad expliciet mee.
static string ResolveRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
        dir = dir.Parent;

    if (dir is null)
        throw new InvalidOperationException(
            "Kon de repository-root niet vinden — geef het pad naar het seedscript expliciet mee.");

    return dir.FullName;
}

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
