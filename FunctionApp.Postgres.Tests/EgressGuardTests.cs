using AwesomeAssertions;
using FunctionApp.Postgres.Infrastructure;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Bedrading van de Postgres-<c>EgressGuard</c> op de gedeelde beslisregel (#857, #1700). De volledige
/// matrix staat in <c>Planner.Shared.Tests/Infrastructure/LokaleOntwikkelomgevingTests.cs</c>; hier
/// alleen het bewijs dat deze tier de procesomgeving leest en de regel volgt. Manipuleert procesbrede
/// omgevingsvariabelen en herstelt ze in <c>finally</c>; de assembly draait sequentieel.
/// </summary>
public class EgressGuardTests
{
    private static readonly string[] Namen = ["WEBSITE_SITE_NAME", "LOCAL_AUTH_BYPASS", "AllowExternalIntegrations"];

    private static bool Met(string? azure, string? schakelaar, string? optIn)
    {
        var origineel = Namen.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", azure);
            Environment.SetEnvironmentVariable("LOCAL_AUTH_BYPASS", schakelaar);
            Environment.SetEnvironmentVariable("AllowExternalIntegrations", optIn);
            return EgressGuard.ExternalIntegrationsAllowed();
        }
        finally
        {
            foreach (var n in Namen) Environment.SetEnvironmentVariable(n, origineel[n]);
        }
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("func-test-01", null, null, true)]
    [InlineData(null, "true", null, false)]
    [InlineData("func-test-01", "true", null, false)]
    [InlineData(null, "true", "true", true)]
    public void ExternalIntegrationsAllowed_VolgtDeGedeeldeRegel(string? azure, string? schakelaar, string? optIn, bool verwacht)
        => Met(azure, schakelaar, optIn).Should().Be(verwacht);
}
