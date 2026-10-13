using AwesomeAssertions;
using SportlinkFunction.Infrastructure;
using Xunit;

namespace FunctionApp.Tests.Infrastructure;

/// <summary>
/// Tests voor de #857-egressblokkade: de ene schakelaar die alle uitgaande integraties
/// (Sportlink-sync, GitHub-issue-rapportage, e-mail, AI-diensten) buiten productie tegenhoudt.
/// <para>
/// Manipuleert bewust de procesbrede omgevingsvariabelen <c>WEBSITE_SITE_NAME</c>,
/// <c>LOCAL_AUTH_BYPASS</c> (#1700) en <c>AllowExternalIntegrations</c> — elke test herstelt de
/// oorspronkelijke waarde in een <c>finally</c>-blok. De assembly draait sequentieel
/// (<c>DisableTestParallelization</c> in AssemblyInfo.cs). De volledige beslismatrix staat in
/// <c>Planner.Shared.Tests/Infrastructure/LokaleOntwikkelomgevingTests.cs</c>; dit bewijst de bedrading.
/// </para>
/// </summary>
public class EgressGuardTests
{
    private const string AllowVar = "AllowExternalIntegrations";
    private const string AzureVar = "WEBSITE_SITE_NAME";
    private const string SchakelaarVar = "LOCAL_AUTH_BYPASS";

    private static void WithEnv(string? azureHosting, string? allowOverride, Action assert, string? schakelaar = null)
    {
        var origAzure = Environment.GetEnvironmentVariable(AzureVar);
        var origAllow = Environment.GetEnvironmentVariable(AllowVar);
        var origSchakelaar = Environment.GetEnvironmentVariable(SchakelaarVar);
        try
        {
            Environment.SetEnvironmentVariable(AzureVar, azureHosting);
            Environment.SetEnvironmentVariable(AllowVar, allowOverride);
            Environment.SetEnvironmentVariable(SchakelaarVar, schakelaar);
            assert();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AzureVar, origAzure);
            Environment.SetEnvironmentVariable(AllowVar, origAllow);
            Environment.SetEnvironmentVariable(SchakelaarVar, origSchakelaar);
        }
    }

    [Fact]
    public void ExternalIntegrationsAllowed_AzureHosting_IsAltijdToegestaanOngeachtOverride()
    {
        WithEnv(azureHosting: "aanwezig", allowOverride: null,
            () => EgressGuard.ExternalIntegrationsAllowed().Should().BeTrue(
                "WEBSITE_SITE_NAME aanwezig betekent Azure-hosting — in dit project altijd productie"));
    }

    [Fact]
    public void ExternalIntegrationsAllowed_GeenAzureHostingEnGeenOverride_IsGeblokkeerd()
    {
        WithEnv(azureHosting: null, allowOverride: null,
            () => EgressGuard.ExternalIntegrationsAllowed().Should().BeFalse(
                "buiten Azure-hosting (lokale ontwikkeling, CI, een testrun) staat de blokkade standaard aan"));
    }

    [Fact]
    public void ExternalIntegrationsAllowed_GeenAzureHostingMaarExpliciteteOptIn_IsToegestaan()
    {
        WithEnv(azureHosting: null, allowOverride: "true",
            () => EgressGuard.ExternalIntegrationsAllowed().Should().BeTrue(
                "de expliciete opt-in moet een bewuste, eenmalige handmatige test toestaan"));
    }

    [Fact]
    public void ExternalIntegrationsAllowed_GeenAzureHostingMaarOptInIsFalse_BlijftGeblokkeerd()
    {
        WithEnv(azureHosting: null, allowOverride: "false",
            () => EgressGuard.ExternalIntegrationsAllowed().Should().BeFalse());
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("true")]
    public void ExternalIntegrationsAllowed_OptInIsHoofdletterongevoelig(string waarde)
    {
        WithEnv(azureHosting: null, allowOverride: waarde,
            () => EgressGuard.ExternalIntegrationsAllowed().Should().BeTrue());
    }

    // ── Lokale schakelaar (#1700): opent nooit uitgaand verkeer ──

    [Fact]
    public void ExternalIntegrationsAllowed_LokaleSchakelaar_BlijftGeblokkeerd()
    {
        WithEnv(azureHosting: null, allowOverride: null, schakelaar: "true",
            assert: () => EgressGuard.ExternalIntegrationsAllowed().Should().BeFalse(
                "de lokale bypass geldt voor autorisatie, niet voor uitgaand verkeer"));
    }

    [Fact]
    public void ExternalIntegrationsAllowed_SchakelaarInAzure_IsGeblokkeerd()
    {
        WithEnv(azureHosting: "aanwezig", allowOverride: null, schakelaar: "true",
            assert: () => EgressGuard.ExternalIntegrationsAllowed().Should().BeFalse(
                "een gehoste app met de lokale schakelaar is verkeerd geconfigureerd: bij twijfel dicht"));
    }
}
