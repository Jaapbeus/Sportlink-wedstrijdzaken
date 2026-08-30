namespace SportlinkFunction.Infrastructure;

/// <summary>
/// Eén centrale schakelaar die alle uitgaande integraties blokkeert buiten productie (#857):
/// de Sportlink-synchronisatie, GitHub-issue-rapportage, e-mail via Microsoft Graph en de
/// AI-diensten. Vervangt vier losse, impliciete "is dit geconfigureerd?"-controles door één
/// expliciete, environment-gebaseerde poort.
/// <para>
/// <b>Productie-detectie:</b> de aanwezigheid van <c>WEBSITE_SITE_NAME</c> — dezelfde signaal die
/// <see cref="SportlinkFunction.Admin.EasyAuthHelper"/> al gebruikt om lokale ontwikkeling te
/// herkennen (Azure zet deze variabele altijd op een gehoste Function App; lokaal en in CI is hij
/// afwezig). Dit project kent één Azure-deployment per fork/club (zie CLAUDE.md,
/// "Deployment-model"), dus Azure-hosting is hier gelijk aan productie.
/// </para>
/// <para>
/// <b>Waarom dit meer is dan de bestaande "niet geconfigureerd → niet actief"-controles</b>
/// (bijv. <c>GitHubPat</c>/<c>OpenAiApiKey</c>/<c>GraphClientSecret</c> ontbreekt): die controles
/// beschermen niet tegen een ontwikkelaar die zo'n secret toevallig wél lokaal heeft ingevuld
/// (bijvoorbeeld gekopieerd voor een eenmalige handmatige test). Deze poort is onafhankelijk
/// daarvan en staat, buiten Azure-hosting, altijd standaard aan.
/// </para>
/// <para>
/// <b>Expliciete opt-in:</b> app-instelling <c>AllowExternalIntegrations=true</c> — voor het
/// zeldzame, bewuste geval van een handmatige test tegen echte externe diensten vanaf een
/// niet-Azure-omgeving. Nooit als default aan laten staan buiten productie.
/// </para>
/// </summary>
public static class EgressGuard
{
    private const string AllowSettingName = "AllowExternalIntegrations";
    private const string AzureHostingSettingName = "WEBSITE_SITE_NAME";

    /// <summary>
    /// True als uitgaande integraties toegestaan zijn: altijd in Azure-hosting (productie), en
    /// daarbuiten alleen met de expliciete opt-in.
    /// </summary>
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
