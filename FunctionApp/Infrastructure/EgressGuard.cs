using Planner.Shared.Infrastructure;

namespace SportlinkFunction.Infrastructure;

/// <summary>
/// Eén centrale schakelaar die alle uitgaande integraties blokkeert buiten productie (#857):
/// de Sportlink-synchronisatie, GitHub-issue-rapportage, e-mail via Microsoft Graph en de
/// AI-diensten. Vervangt vier losse, impliciete "is dit geconfigureerd?"-controles door één
/// expliciete, environment-gebaseerde poort.
/// <para>
/// <b>Beslisregel (#1700):</b> een dunne adapter op
/// <see cref="LokaleOntwikkelomgeving.ExterneIntegratiesToegestaan()"/>, gedeeld met de Postgres-tier.
/// Open in productie: Azure-hosting (<c>WEBSITE_SITE_NAME</c> gezet) zónder de lokale schakelaar
/// <c>LOCAL_AUTH_BYPASS</c>. Dit project kent één Azure-deployment per fork/club, dus Azure-hosting is
/// hier gelijk aan productie. De lokale schakelaar opent uitgaand verkeer nooit; staat hij in Azure
/// toch aan, dan blijft het dicht (bij twijfel dicht).
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
    /// <summary>
    /// True als uitgaande integraties toegestaan zijn: in productie (Azure zonder lokale schakelaar),
    /// en daarbuiten alleen met de expliciete opt-in.
    /// </summary>
    public static bool ExternalIntegrationsAllowed() => LokaleOntwikkelomgeving.ExterneIntegratiesToegestaan();
}
