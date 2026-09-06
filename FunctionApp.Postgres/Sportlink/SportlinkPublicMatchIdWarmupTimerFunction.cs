using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Infrastructure;
using FunctionApp.Postgres.Integrations.SportlinkClub;
using FunctionApp.Postgres.Planner;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Achtergrond-warmup van de PublicMatchId-cache (epic #986, issue #1017), vervolg op #987/#991/#1016.
/// <para>
/// <b>Waarom dit bestaat:</b> de reverse-lookup (<c>MatchProgramOverview</c>) duurt 12+ seconden en
/// is niet club-gescoped (bevat het volledige regio-/competitieprogramma voor die dag). Zonder
/// warmup wacht de EERSTE gebruiker die een wedstrijd opent (Dagplanning-paneel #991, deep-link
/// #989) op die volle vertraging. Deze timer haalt het resultaat al op vóórdat dat gebeurt, gericht
/// op de eerstvolgende dagen (waar de wedstrijdsecretaris in de praktijk mee werkt) — geen
/// dagen-lange horizon, dat zou onnodig veel Sportlink-verkeer genereren voor wedstrijden die nog
/// lang niet relevant zijn.
/// </para>
/// <para>
/// Groepeert per datum en roept <see cref="ISportlinkClubClient.GetMatchProgramOverviewAsync"/> ÉÉN
/// keer per unieke datum aan (niet per wedstrijd) — meerdere eigen wedstrijden op dezelfde dag
/// (bijv. meerdere thuiswedstrijden op zaterdag) delen dezelfde, al opgehaalde dagrespons. Zie de
/// #1017-refactor die dit apart van <c>ResolvePublicMatchIdAsync</c> mogelijk maakte.
/// </para>
/// <para>
/// Alleen voor de Postgres-tier — zelfde reden als <see cref="SportlinkTokenKeepAliveTimerFunction"/>:
/// de SQL Server-tier is rollback-only sinds de Postgres-cutover (#1020).
/// </para>
/// </summary>
public static class SportlinkPublicMatchIdWarmupTimerFunction
{
    private const string RolNaam = "Wedstrijdzaken";

    // 3 dagen (vandaag + 2) — dekt een doordeweekse wedstrijd morgen én het aankomende weekend als
    // de timer op een donderdag/vrijdag draait, zonder een dagen-lange horizon vol nog-niet-
    // relevante wedstrijden op te halen.
    private const int VooruitkijkDagen = 2;

    [Function("SportlinkPublicMatchIdWarmup")]
    public static async Task Run(
        [TimerTrigger("0 0 6 * * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("SportlinkPublicMatchIdWarmup");

        if (PostgresAppSettings.GetSetting("sportlinkExtensionEnabled") != "1")
        {
            log.LogInformation("Sportlink Web Extension staat uit — warmup overgeslagen.");
            return;
        }

        if (!EgressGuard.ExternalIntegrationsAllowed())
        {
            log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — warmup overgeslagen (#857).");
            return;
        }

        var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
        if (sportlinkClient == null)
        {
            log.LogWarning("ISportlinkClubClient niet geregistreerd — warmup kan niet draaien.");
            return;
        }

        var clubCode = PostgresClubScope.Primary;
        var vandaag = DateOnly.FromDateTime(DateTime.UtcNow);
        var totEnMet = vandaag.AddDays(VooruitkijkDagen);

        List<WedstrijdZonderCache> wedstrijden;
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            wedstrijden = await SportlinkPublicMatchIdRepository.ZoekWedstrijdenZonderCacheAsync(
                connection, vandaag, totEnMet, clubCode);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon wedstrijden zonder cache-entry niet ophalen — warmup overgeslagen.");
            return;
        }

        if (wedstrijden.Count == 0)
        {
            log.LogInformation("Geen wedstrijden zonder PublicMatchId-cache in de komende {Dagen} dagen.", VooruitkijkDagen + 1);
            return;
        }

        // Groeperen per datum: één MatchProgramOverview-aanroep per dag, niet per wedstrijd.
        foreach (var groep in wedstrijden.GroupBy(w => w.Datum))
        {
            var overview = await sportlinkClient.GetMatchProgramOverviewAsync(RolNaam, groep.Key);
            if (overview.Status != SportlinkClubCallStatus.Ok || overview.Data == null)
            {
                log.LogWarning("MatchProgramOverview voor {Datum} gaf status {Status} — {Aantal} wedstrijden overgeslagen deze run (proberen volgende run opnieuw).",
                    groep.Key, overview.Status, groep.Count());
                continue;
            }

            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            var gevondenAantal = 0;
            foreach (var wedstrijd in groep)
            {
                var match = overview.Data.FirstOrDefault(e => e.ExternalMatchId == wedstrijd.Wedstrijdnummer);
                if (match == null)
                    continue; // Nog niet bekend bij Sportlink voor deze datum — geen fout, volgende run opnieuw proberen.

                await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(
                    connection, wedstrijd.Wedstrijdcode, clubCode, match.PublicMatchId);
                gevondenAantal++;
            }

            log.LogInformation("Warmup {Datum}: {Gevonden}/{Totaal} wedstrijden gecachet.", groep.Key, gevondenAantal, groep.Count());
        }
    }
}
