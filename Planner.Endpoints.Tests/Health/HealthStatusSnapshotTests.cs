using System.Text.Json;
using AwesomeAssertions;
using Planner.Endpoints.Health;
using Xunit;

namespace Planner.Endpoints.Tests.Health;

public class HealthStatusSnapshotTests
{
    [Fact]
    public void PublicProjectionContainsAggregateFlagsAndNoDiagnosticValues()
    {
        var snapshot = new HealthStatusSnapshot(
            "degraded", "3.16.0.4", "online", true, false, true, true,
            DateTime.UtcNow, "Postgres", "Npgsql", "server-version", DateTime.UtcNow.AddDays(-3), true,
            "Require", "TLS details", "Schema details", ["migration-name"]);

        var json = JsonSerializer.Serialize(snapshot.ToPublicResponse());

        json.Should().Contain("settingsReady").And.Contain("migrationsCurrent");
        json.Should().Contain("schemaWarningPresent").And.Contain("tlsWarningPresent");
        json.Should().NotContain("server-version").And.NotContain("TLS details").And.NotContain("Schema details");
        json.Should().NotContain("migration-name").And.NotContain("lastSync").And.NotContain("tier");
    }

    [Fact]
    public void AdminProjectionPreservesDiagnosticFields()
    {
        var snapshot = new HealthStatusSnapshot(
            "ok", "3.16.0.4", "online", true, true, false, false,
            DateTime.UtcNow, "Postgres", "Npgsql", "db-version", null, false,
            "VerifyFull", null, null, []);

        var json = JsonSerializer.Serialize(snapshot.ToAdminResponse());

        json.Should().Contain("settingsLoaded").And.Contain("serverVersion").And.Contain("pendingMigrations");
        json.Should().Contain("db-version").And.Contain("VerifyFull");
    }
}
