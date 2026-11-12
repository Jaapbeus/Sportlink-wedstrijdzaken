using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using FunctionApp.Postgres.Infrastructure;
using FunctionApp.Postgres.Admin;
using Planner.Endpoints.Health;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres;

/// <summary>
/// Publieke aggregate health en admin-only diagnose voor de Postgres-tier (#891, #1811).
/// Detailvelden <c>tier</c>/<c>provider</c> komen uit build-time assembly-metadata; <c>serverVersion</c>
/// komt aantoonbaar uit de database zelf. De publieke respons bevat alleen statusflags.
/// <para>
/// <b>Geen "paused"-status:</b> de SQL Server-tier herkent Azure SQL's serverless auto-pause aan
/// foutnummer 40613 — een Azure-SQL-specifiek concept. Zonder bevestigde, vergelijkbare
/// auto-pause-laag voor de gekozen Postgres-hosting zou een "paused"-status hier verzonnen zijn;
/// een onbereikbare database is dus altijd "unavailable" of "timeout".
/// </para>
/// </summary>
public static class HealthFunction
{
    [Function("Health")]
    public static async Task<IActionResult> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req,
        FunctionContext context)
    {
        var snapshot = await GetCachedHealthSnapshotAsync(context.GetLogger("Health"), req.HttpContext.RequestAborted);
        var response = snapshot.ToPublicResponse();
        return snapshot.Database == "unconfigured"
            ? new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable }
            : new OkObjectResult(response);
    }

    [Function("AdminHealth")]
    public static Task<IActionResult> AdminHealth(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/health")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminHealth");
        return AdminEndpoint.ExecuteZonderDatabaseAsync(req, log, "gezondheidsdetails ophalen", async () =>
        {
            var snapshot = await GetCachedHealthSnapshotAsync(log, req.HttpContext.RequestAborted);
            var response = snapshot.ToAdminResponse();
            return snapshot.Database == "unconfigured"
                ? new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable }
                : new OkObjectResult(response);
        });
    }

    private static Task<HealthStatusSnapshot> GetCachedHealthSnapshotAsync(ILogger log, CancellationToken callerCancellation) =>
        HealthStatusCacheAccessor.Instance.GetAsync("Postgres", () => CreateHealthSnapshotAsync(log), callerCancellation);

    private static async Task<HealthStatusSnapshot> CreateHealthSnapshotAsync(ILogger log)
    {
        var version = typeof(HealthFunction).Assembly.GetName().Version?.ToString(4) ?? "?";
        var (dbStatus, serverVersion) = await GetDatabaseStatusAsync();
        var settingsLoaded = await ProbeSettingsAsync(dbStatus, log);
        var (lastSync, syncStale) = await GetSyncStatusAsync(dbStatus);
        var (tlsMode, tlsWarning) = GetTlsStatus();
        var pendingMigrations = await GetPendingMigrationsAsync(dbStatus);
        var leesPadenOk = await ProbeLeesPadenAsync(dbStatus, log);
        var schemaWarning = PostgresAppSettings.SchemaWarning;
        var status = dbStatus == "online" && settingsLoaded && !(syncStale && SyncWordtVerwacht)
                     && pendingMigrations is { Count: 0 } && leesPadenOk != false ? "ok" : "degraded";
        return new HealthStatusSnapshot(
            status,
            version,
            dbStatus,
            settingsLoaded,
            pendingMigrations is null ? null : pendingMigrations.Count == 0,
            !string.IsNullOrEmpty(schemaWarning),
            !string.IsNullOrEmpty(tlsWarning),
            DateTime.UtcNow,
            GetAssemblyMetadata("DatabaseTier") ?? "onbekend",
            GetAssemblyMetadata("DatabaseProvider") ?? "onbekend",
            serverVersion,
            lastSync,
            syncStale,
            tlsMode,
            tlsWarning,
            schemaWarning,
            pendingMigrations,
            leesPadenOk);
    }

    /// <summary>
    /// #1911: leest het speeltijden-leespad van de Planning met dezelfde productiecode als het endpoint. Een
    /// driver- of typemappingfout (Npgsql 10 geeft een <c>time</c>-kolom als <see cref="TimeOnly"/>) viel
    /// voorheen pas op bij de eerste gebruiker: de smoke tests keken alleen naar bereikbaarheid, auth en migraties.
    /// <c>false</c> laat de deploy-smoke-test falen (<c>scripts/dev/health-deploy-predicate.sh</c>); <c>null</c> =
    /// niet gemeten (database offline of geen speeltijden-rij). Er wordt een rij mét voorkeurstijd gekozen, want
    /// juist een gevulde <c>time</c>-waarde loopt door de typemapping. De foutdetails staan alleen in het log.
    /// </summary>
    private static async Task<bool?> ProbeLeesPadenAsync(string dbStatus, ILogger log)
    {
        if (dbStatus != "online") return null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync(cts.Token);
            await using var kies = new NpgsqlCommand(
                "SELECT clubcode FROM public.speeltijden ORDER BY (standaardvoorkeurtijd IS NULL), clubcode LIMIT 1", connection)
            { CommandTimeout = 5 };
            if (await kies.ExecuteScalarAsync(cts.Token) is not string clubCode) return null;

            await FunctionApp.Postgres.Planner.PlannerSettingsRepository.GetSpeeltijdenLookupAsync(connection, null, clubCode)
                .WaitAsync(cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Health: leesproef van de planner-leespaden mislukt.");
            return false;
        }
    }

    /// <summary>
    /// Draait er op deze omgeving überhaupt een synchronisatie? Zo niet, dan is een oude of
    /// ontbrekende <c>lastsynctimestamp</c> het verwachte gedrag. Dezelfde poort als de timer zelf
    /// gebruikt (<see cref="EgressGuard.ExternalIntegrationsAllowed"/>, #857) — anders zouden de
    /// twee uit de pas kunnen lopen.
    /// </summary>
    /// <summary>
    /// De beslisregel zelf, los van database en omgeving zodat hij toetsbaar is (#1081).
    /// <c>null</c> betekent "nooit gesynchroniseerd" en telt als verouderd: waar een synchronisatie
    /// hoort te draaien is dat geen neutrale begintoestand maar een storing. Of dat oordeel de
    /// status beïnvloedt, beslist <see cref="SyncWordtVerwacht"/> — niet deze functie.
    /// </summary>
    internal static bool IsSyncVerouderd(DateTime? laatsteSync, DateTime nuUtc, int maxLeeftijdUren)
        => laatsteSync is null || nuUtc - laatsteSync.Value > TimeSpan.FromHours(maxLeeftijdUren);

    private static bool SyncWordtVerwacht => EgressGuard.ExternalIntegrationsAllowed();

    /// <summary>
    /// Standaard 36 uur: de timer draait dagelijks, dus één gemiste run valt op terwijl een run die
    /// een paar uur uitloopt dat niet doet. Overschrijfbaar met <c>SyncMaxAgeHours</c> voor
    /// installaties met een ander schema.
    /// </summary>
    private static int MaxSyncLeeftijdUren =>
        int.TryParse(Environment.GetEnvironmentVariable("SyncMaxAgeHours"), out var uren) && uren > 0
            ? uren : 36;

    /// <summary>
    /// Leest de laatste synchronisatietijd van de primaire club. Bewust dezelfde selectie als
    /// <see cref="PostgresAppSettings"/>: alleen clubs met <c>syncenabled = true</c>, zodat de
    /// democlub (die per ontwerp niet synchroniseert) dit oordeel nooit kan beïnvloeden.
    /// </summary>
    private static async Task<(DateTime? LastSync, bool Stale)> GetSyncStatusAsync(string dbStatus)
    {
        if (dbStatus != "online") return (null, false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync(cts.Token);
            await using var cmd = new NpgsqlCommand(
                "SELECT MAX(lastsynctimestamp) FROM public.appsettings WHERE syncenabled = true", connection)
            { CommandTimeout = 5 };
            var waarde = await cmd.ExecuteScalarAsync(cts.Token);

            if (waarde is null || waarde == DBNull.Value)
                return (null, IsSyncVerouderd(null, DateTime.UtcNow, MaxSyncLeeftijdUren));

            var laatste = DateTime.SpecifyKind((DateTime)waarde, DateTimeKind.Utc);
            return (laatste, IsSyncVerouderd(laatste, DateTime.UtcNow, MaxSyncLeeftijdUren));
        }
        catch
        {
            // Een mislukte leesactie mag dit endpoint niet laten vallen; 'database' dekt de
            // verbinding al af en de volledige fout staat in het functielog.
            return (null, false);
        }
    }

    /// <summary>
    /// #1098: <c>settingsLoaded</c> zei tot nu toe alleen iets over de <i>laatste</i> laadpoging.
    /// Direct na een (her)start was er nog geen poging geweest, dus meldde een verse host
    /// <c>true</c> terwijl het eerste beheerscherm daarna op 500 zou lopen — precies het gat
    /// waardoor de smoke test in <c>deploy.yml</c> het v3.3.0.0-incident niet zag. Health doet nu
    /// zelf één laadpoging zodra de database bereikbaar is: één kleine query, en het antwoord is
    /// daarna een feit in plaats van een aanname. Een mislukking wordt hier gevangen — de reden
    /// staat in het functielog; de publieke respons bevat geen foutdetails.
    /// </summary>
    private static async Task<bool> ProbeSettingsAsync(string dbStatus, ILogger log)
    {
        if (dbStatus == "online")
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await PostgresAppSettings.LoadSettingsAsync(log, cts.Token, commandTimeoutSeconds: 5); }
            catch (Exception ex) { log.LogWarning(ex, "Health: instellingen laden mislukt."); }
        }
        return !PostgresAppSettings.LastLoadFailed;
    }

    /// <summary>
    /// #1098: vergelijkt de meegeleverde migratienamen met de ledger. Leest alleen — een migratie
    /// tegen productie blijft een bewuste handeling van de eigenaar (ARCHITECTUUR-DATABASE-TIERS.md
    /// §49). <c>null</c> als de database niet bereikbaar is of de vergelijking zelf faalt.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> GetPendingMigrationsAsync(string dbStatus)
    {
        if (dbStatus != "online") return null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync(cts.Token);
            return await Database.Postgres.MigrationRunner.GetPendingMigrationsAsync(connection, cts.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// #1095: leest de effectieve TLS-modus en -waarschuwing. Dezelfde static initializer als
    /// <see cref="PostgresDatabaseConfig.ConnectionString"/>; is die niet te bouwen, dan is
    /// <c>database</c> al "unconfigured" en blijven beide velden hier <c>null</c>.
    /// </summary>
    private static (string? Mode, string? Warning) GetTlsStatus()
    {
        try { return (PostgresDatabaseConfig.EffectiveSslMode, PostgresDatabaseConfig.TlsWarning); }
        catch { return (null, null); }
    }

    internal static string? GetAssemblyMetadata(string key) =>
        typeof(HealthFunction).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

    // connectionStringOverride is uitsluitend voor tests (#859), zelfde precedent als de SQL
    // Server-tier: PostgresDatabaseConfig.ConnectionString is static readonly en dus al gevuld
    // vóórdat een test kan draaien.
    internal static async Task<(string status, string? serverVersion)> GetDatabaseStatusAsync(
        Func<string>? connectionStringOverride = null)
    {
        string connStr;
        try { connStr = (connectionStringOverride ?? (() => PostgresDatabaseConfig.ConnectionString))(); }
        catch { return ("unconfigured", null); }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(cts.Token);
            await using var cmd = new NpgsqlCommand("SHOW server_version", conn) { CommandTimeout = 5 };
            var serverVersion = (string?)await cmd.ExecuteScalarAsync(cts.Token);
            return ("online", serverVersion);
        }
        catch (OperationCanceledException)
        {
            return ("timeout", null);
        }
        catch
        {
            return ("unavailable", null);
        }
    }
}
