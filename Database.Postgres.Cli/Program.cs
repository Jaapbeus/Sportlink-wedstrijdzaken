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

var migrationsPath = args.Length > 0 ? args[0] : ResolveDefaultMigrationsPath();

try
{
    await MigrationRunner.RunAsync(connectionString, migrationsPath);
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
