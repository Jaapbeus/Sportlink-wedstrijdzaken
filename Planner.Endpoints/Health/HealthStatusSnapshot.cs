namespace Planner.Endpoints.Health;

/// <summary>One bounded database probe result. Diagnostic values are only returned by the admin projection.</summary>
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
    IReadOnlyList<string>? PendingMigrations)
{
    public object ToPublicResponse() => new
    {
        status = Status,
        version = Version,
        database = Database,
        settingsReady = SettingsReady,
        migrationsCurrent = MigrationsCurrent,
        schemaWarningPresent = SchemaWarningPresent,
        tlsWarningPresent = TlsWarningPresent
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
        pendingMigrations = PendingMigrations
    };
}
