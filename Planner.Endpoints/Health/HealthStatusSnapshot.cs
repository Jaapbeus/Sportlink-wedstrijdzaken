namespace Planner.Endpoints.Health;

/// <summary>
/// One bounded database probe result. Diagnostic values are only returned by the admin projection.
/// <c>ReadPathsOk</c> (#1911): <c>true</c>/<c>false</c> = de leesproef van de planner-leespaden slaagde/faalde,
/// <c>null</c> = niet gemeten (tier zonder leesproef, of geen data om te lezen).
/// </summary>
public sealed record HealthStatusSnapshot(
    string Status,
    string Version,
    string Database,
    bool SettingsReady,
    bool? MigrationsCurrent,
    bool SchemaWarningPresent,
    bool TlsWarningPresent,
    DateTime Timestamp,
    string? Tier,
    string? Provider,
    string? ServerVersion,
    DateTime? LastSync,
    bool? SyncStale,
    string? TlsMode,
    string? TlsWarning,
    string? SchemaWarning,
    IReadOnlyList<string>? PendingMigrations,
    bool? ReadPathsOk = null)
{
    public object ToPublicResponse() => new
    {
        status = Status,
        version = Version,
        database = Database,
        settingsReady = SettingsReady,
        migrationsCurrent = MigrationsCurrent,
        schemaWarningPresent = SchemaWarningPresent,
        tlsWarningPresent = TlsWarningPresent,
        readPathsOk = ReadPathsOk
    };

    public object ToAdminResponse() => new
    {
        status = Status,
        version = Version,
        timestamp = Timestamp,
        database = Database,
        settingsLoaded = SettingsReady,
        tier = Tier,
        provider = Provider,
        serverVersion = ServerVersion,
        lastSync = LastSync,
        syncStale = SyncStale,
        tlsMode = TlsMode,
        tlsWarning = TlsWarning,
        schemaWarning = SchemaWarning,
        pendingMigrations = PendingMigrations,
        readPathsOk = ReadPathsOk
    };
}
