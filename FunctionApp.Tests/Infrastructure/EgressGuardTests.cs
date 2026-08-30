using FluentAssertions;
using SportlinkFunction.Infrastructure;
using Xunit;

namespace FunctionApp.Tests.Infrastructure;

/// <summary>
/// Tests voor de #857-egressblokkade: de ene schakelaar die alle uitgaande integraties
/// (Sportlink-sync, GitHub-issue-rapportage, e-mail, AI-diensten) buiten productie tegenhoudt.
/// <para>
/// Manipuleert bewust de procesbrede omgevingsvariabelen <c>WEBSITE_SITE_NAME</c> en
/// <c>AllowExternalIntegrations</c> — elke test herstelt de oorspronkelijke waarde in een
/// <c>finally</c>-blok. Deze klasse is de enige in <c>FunctionApp.Tests</c> die deze twee
/// variabelen aanraakt; xUnit draait testmethoden binnen één klasse niet parallel aan elkaar
/// (alleen andere testklassen lopen parallel), dus dit is veilig zonder extra serialisatie.
/// </para>
/// </summary>
public class EgressGuardTests
{
    private const string AllowVar = "AllowExternalIntegrations";
    private const string AzureVar = "WEBSITE_SITE_NAME";

    private static void WithEnv(string? azureHosting, string? allowOverride, Action assert)
    {
        var origAzure = Environment.GetEnvironmentVariable(AzureVar);
        var origAllow = Environment.GetEnvironmentVariable(AllowVar);
        try
        {
            Environment.SetEnvironmentVariable(AzureVar, azureHosting);
            Environment.SetEnvironmentVariable(AllowVar, allowOverride);
            assert();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AzureVar, origAzure);
            Environment.SetEnvironmentVariable(AllowVar, origAllow);
        }
    }

    [Fact]
    public void ExternalIntegrationsAllowed_AzureHosting_IsAltijdToegestaanOngeachtOverride()
    {
        WithEnv(azureHosting: "func-testclub-sportlink", allowOverride: null,
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
}
