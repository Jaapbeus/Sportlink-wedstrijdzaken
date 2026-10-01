using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction;
using SportlinkFunction.Sportlink;

namespace SportlinkFunction.Infrastructure;

internal static class SqlSportlinkAuthenticationRegistration
{
    public static void Register(IServiceCollection services)
    {
        // #1411: hostsleutel buiten de database; ontbreken/ongeldig blijft fail-closed.
        var sportlinkProtector = SportlinkAutoLoginConfiguration.CreateProtector(
            Environment.GetEnvironmentVariable(SportlinkAutoLoginConfiguration.KeySetting));
        if (sportlinkProtector is not null)
        {
            services.AddSingleton<ISportlinkAutoLoginStore>(sp => new SqlSportlinkAutoLoginStore(
                SportlinkFunction.SystemUtilities.DatabaseConfig.ConnectionString, () => SportlinkFunction.Planner.ClubScope.Primary, sportlinkProtector));
        }

        if (EgressGuard.ExternalIntegrationsAllowed())
        {
            if (sportlinkProtector is not null)
            {
                services.AddSingleton<ISportlinkClubTokenStore>(sp => sp.GetRequiredService<ISportlinkAutoLoginStore>());
                services.AddSingleton(sp => new SportlinkAutoLoginCoordinator(
                    sp.GetRequiredService<ISportlinkAutoLoginStore>(), new SportlinkAutoLoginProvider()));
            // #1266: de dry-run-stand komt nu uit dbo.AppSettings.SportlinkDryRun in plaats van een harde
            // `true`. Dezelfde gedeelde, fail-safe regel als de Postgres-tier (SportlinkEndpointCore):
            // alles behalve een expliciet geladen "0" is dry-run. Zolang de instellingencache nog niet
            // geladen is — of de kolom ontbreekt op een nog niet gemigreerde database — blijft dry-run dus
            // AAN. Met de omgekeerde polariteit zou een lege cache fail-OPEN zijn en zou een mutatiepad
            // per ongeluk een echte PUT/POST naar Sportlink versturen.
            // #1387: géén HttpClient-brede Timeout meer (was 15s voor élk endpoint, ook de gedocumenteerd
            // trage MatchProgramOverview-reverse-lookup — die gaf zo een valse HTTP 502 met nagenoeg geen
            // marge). SportlinkClubClient bepaalt nu zelf, per endpoint, een gemotiveerd per-aanroep budget
            // (DefaultCallTimeout/ReverseLookupCallTimeout); de .NET-default van 100s hier blijft slechts
            // het buitenste veiligheidsnet.
            services.AddHttpClient<ISportlinkClubClient, SportlinkClubClient>()
            .AddTypedClient<ISportlinkClubClient>((httpClient, sp) =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var dryRunLogger = loggerFactory.CreateLogger("SportlinkDryRun");
                return new SportlinkClubClient(
                    httpClient,
                    sp.GetRequiredService<ISportlinkClubTokenStore>(),
                    loggerFactory.CreateLogger<SportlinkClubClient>(),
                    // De instelling wordt bij ELKE mutatie-aanroep opnieuw gelezen (niet één keer bij
                    // opstarten), zodat de Instellingen-toggle direct effect heeft zonder herstart.
                    isDryRun: () => SportlinkEndpointCore.IsDryRunActief(
                        SportlinkFunction.SystemUtilities.AppSettings.GetSetting, dryRunLogger),
                    autoLogin: sp.GetService<SportlinkAutoLoginCoordinator>());
            });
            }
        }
    }
}
