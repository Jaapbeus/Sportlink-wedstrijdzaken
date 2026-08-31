namespace FunctionApp.Postgres.Infrastructure;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Infrastructure/EgressGuard.cs</c> (#857/#890) —
/// zuivere, omgevingsvariabele-gebaseerde poort zonder databaseafhankelijkheid, dus gedupliceerd
/// (niet gedeeld) net als de andere infrastructuurbestanden in deze aparte implementatieboom.
/// </summary>
public static class EgressGuard
{
    private const string AllowSettingName = "AllowExternalIntegrations";
    private const string AzureHostingSettingName = "WEBSITE_SITE_NAME";

    public static bool ExternalIntegrationsAllowed()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AzureHostingSettingName)))
            return true;

        return string.Equals(
            Environment.GetEnvironmentVariable(AllowSettingName),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }
}
