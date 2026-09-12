using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using FunctionApp.Postgres.Infrastructure;
using Npgsql;

namespace FunctionApp.Postgres;

/// <summary>
/// /api/health voor de Postgres-tier (#891) — zelfde vorm en velden als de SQL Server-tier
/// (<c>FunctionApp/Planner/PlannerFunction.cs</c>, #863): <c>tier</c>/<c>provider</c> komen uit
/// build-time assembly-metadata (nooit een runtime-gok, dus ook gevuld als de database
/// onbereikbaar is), <c>serverVersion</c> komt aantoonbaar uit de database zelf.
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
        var version = typeof(HealthFunction).Assembly.GetName().Version?.ToString(4) ?? "?";
        var (dbStatus, serverVersion) = await GetDatabaseStatusAsync();
        var settingsLoaded = !PostgresAppSettings.LastLoadFailed;
        var (lastSync, syncStale) = await GetSyncStatusAsync(dbStatus);
        var (tlsMode, tlsWarning) = GetTlsStatus();
        var body = new
        {
            // #1081: een verouderde synchronisatie telt mee in de status, maar alleen waar een
            // synchronisatie ook hoort te draaien. Lokaal en in CI blokkeert EgressGuard (#857)
            // uitgaand verkeer, dus is een lege of oude lastsynctimestamp daar het verwachte
            // gedrag en geen storing — die twee zouden anders permanent 'degraded' melden en het
            // signaal waardeloos maken.
            status = dbStatus == "online" && settingsLoaded && !(syncStale && SyncWordtVerwacht)
                ? "ok" : "degraded",
            version,
            timestamp = DateTime.UtcNow,
            database = dbStatus,
            // #859: apart van 'database' — een geslaagde verbinding zegt niets over of de
            // instellingencache ook echt gevuld is. Bewust geen foutdetails hier: dit endpoint is
            // anoniem toegankelijk, de volledige exceptie staat al in het functielog.
            settingsLoaded,
            // #1081: de synchronisatie draaide acht dagen elke nacht en werkte niets bij, zonder
            // dat iets dat meldde. Deze twee velden maken dat zichtbaar zonder een betaalde
            // metric-alert: lastSync is de waarheid uit de database, syncStale het oordeel.
            lastSync,
            syncStale,
            tier = GetAssemblyMetadata("DatabaseTier") ?? "onbekend",
            provider = GetAssemblyMetadata("DatabaseProvider") ?? "onbekend",
            serverVersion,
            // #1095: de TLS-modus die daadwerkelijk geldt, plus een waarschuwing als het beleid
            // van #1004 (VerifyFull) niet gehaald wordt. Bewust geen hostnaam of credentials —
            // dit endpoint is anoniem. Zo is een onvolledige TLS-configuratie zichtbaar zonder
            // dat de applicatie er eerst op uitvalt (dat was het v3.3.0.0-incident).
            tlsMode,
            tlsWarning
        };
        // #859: "niet geconfigureerd" (geen bruikbare connectiereeks) is geen 200 OK — een
        // draaiende maar onbereikbare database (timeout/unavailable) blijft wel 200 met status
        // "degraded", dat is een tijdelijke toestand.
        return dbStatus == "unconfigured"
            ? new ObjectResult(body) { StatusCode = StatusCodes.Status503ServiceUnavailable }
            : new OkObjectResult(body);
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

        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT MAX(lastsynctimestamp) FROM public.appsettings WHERE syncenabled = true", connection);
            var waarde = await cmd.ExecuteScalarAsync();

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
