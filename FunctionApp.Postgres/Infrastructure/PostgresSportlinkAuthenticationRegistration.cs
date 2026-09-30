using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;
using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Sportlink;

namespace FunctionApp.Postgres.Infrastructure;

internal static class PostgresSportlinkAuthenticationRegistration
{
    public static void Register(IServiceCollection services)
    {
        // #1411: hostsleutel buiten de database; ontbreken/ongeldig blijft fail-closed.
        var sportlinkProtector = SportlinkAutoLoginConfiguration.CreateProtector(
            Environment.GetEnvironmentVariable(SportlinkAutoLoginConfiguration.KeySetting));
        if (sportlinkProtector is not null)
        {
            services.AddSingleton<ISportlinkAutoLoginStore>(sp => new PostgresSportlinkAutoLoginStore(
                PostgresDatabaseConfig.ConnectionString, () => FunctionApp.Postgres.Planner.PostgresClubScope.Primary, sportlinkProtector,
                new PostgresSportlinkClubTokenStore(PostgresDatabaseConfig.ConnectionString, sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresSportlinkClubTokenStore>())));
        }

        if (EgressGuard.ExternalIntegrationsAllowed())
        {
            services.AddSingleton<ISportlinkClubTokenStore>(sp =>
                (ISportlinkClubTokenStore?)sp.GetService<ISportlinkAutoLoginStore>() ??
                new PostgresSportlinkClubTokenStore(PostgresDatabaseConfig.ConnectionString, sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresSportlinkClubTokenStore>()));
            if (sportlinkProtector is not null)
                services.AddSingleton(sp => new SportlinkAutoLoginCoordinator(
                    sp.GetRequiredService<ISportlinkAutoLoginStore>(), new SportlinkAutoLoginProvider()));
            // #998: .AddTypedClient overschrijft bewust de standaard-constructiewijze van AddHttpClient<T,I>
            // (die zelf géén onbekende ctor-parameters zoals Func<bool> kan invullen) zodat de dry-run-
            // delegate hier expliciet meegegeven kan worden — PostgresAppSettings.GetSetting wordt bij ELKE
            // mutatie-aanroep opnieuw gelezen (niet één keer bij opstarten), zodat de Instellingen-toggle
            // direct effect heeft zonder herstart.
            // #1387: géén HttpClient-brede Timeout meer (was 15s voor élk endpoint, ook de gedocumenteerd
            // trage MatchProgramOverview-reverse-lookup — die gaf zo een valse HTTP 502 met nagenoeg geen
            // marge). SportlinkClubClient bepaalt nu zelf, per endpoint, een gemotiveerd per-aanroep budget
            // (DefaultCallTimeout/ReverseLookupCallTimeout); de .NET-default van 100s hier blijft slechts
            // het buitenste veiligheidsnet.
            services.AddHttpClient<ISportlinkClubClient, SportlinkClubClient>()
            .AddTypedClient<ISportlinkClubClient>((httpClient, sp) => new SportlinkClubClient(
                httpClient,
                sp.GetRequiredService<ISportlinkClubTokenStore>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<SportlinkClubClient>(),
                // #1122 (CISO): fail-safe. Alles behalve een expliciet geladen "0" is dry-run — dezelfde
                // polariteit als SportlinkExtensieHealthFunction. Met "== \"1\"" was een nog niet geladen
                // instellingencache (null) fail-OPEN: het statuspaneel toonde "dry-run aan" terwijl een
                // bevestigde mutatie écht naar Sportlink zou gaan. Sinds #1266 staat die regel in
                // SportlinkEndpointCore, zodat beide tiers dezelfde polariteit hebben.
                isDryRun: () => SportlinkEndpointCore.IsDryRunActief(PostgresAppSettings.GetSetting),
                autoLogin: sp.GetService<SportlinkAutoLoginCoordinator>()));
        }
    }
}
