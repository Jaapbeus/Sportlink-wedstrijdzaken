using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Integrations.SportlinkClub;
using SportlinkFunction.Planner;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-tegenhanger van
/// <c>FunctionApp.Postgres/Sportlink/SportlinkPublicMatchIdWarmupTimerFunction.cs</c> (#1266,
/// #1017, epic #986) — achtergrond-warmup van de PublicMatchId-cache.
/// <para>
/// <b>Waarom dit bestaat:</b> de reverse-lookup (<c>MatchProgramOverview</c>) duurt 12+ seconden en
/// is niet club-gescoped (bevat het volledige regio-/competitieprogramma voor die dag). Zonder
/// warmup wacht de EERSTE gebruiker die een wedstrijd opent op die volle vertraging. Deze timer
/// haalt het resultaat al op vóórdat dat gebeurt, gericht op de eerstvolgende dagen — geen
/// dagen-lange horizon, dat zou onnodig veel Sportlink-verkeer genereren voor wedstrijden die nog
/// lang niet relevant zijn.
/// </para>
/// <para>
/// Groepeert per datum en roept <see cref="ISportlinkClubClient.GetMatchProgramOverviewAsync"/> ÉÉN
/// keer per unieke datum aan (niet per wedstrijd) — meerdere eigen wedstrijden op dezelfde dag
/// delen dezelfde, al opgehaalde dagrespons.
/// </para>
/// </summary>
public static class SportlinkPublicMatchIdWarmupTimerFunction
{
    private const string RolNaam = SportlinkEndpointSupport.RolWedstrijdzaken;

    /// <summary>Vandaag + dit aantal dagen. Gedeeld met de Postgres-tier, zodat een tierwissel niet
    /// stilzwijgend een andere horizon oplevert.</summary>
    private const int VooruitkijkDagen = SportlinkEndpointCore.WarmupVooruitkijkDagen;

    [Function("SportlinkPublicMatchIdWarmup")]
    public static async Task Run(
        [TimerTrigger("0 0 6 * * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("SportlinkPublicMatchIdWarmup");

        // Zie SportlinkContractCheckTimerFunction: deze tier laadt de instellingencache via
        // WaitForDatabaseAsync, vóór de toggle gelezen wordt.
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Database niet bereikbaar — warmup overgeslagen.");
            return;
        }

        var sportlinkClient = SportlinkEndpointSupport.ClientVoorTimer(context, log, "warmup");
        if (sportlinkClient == null) return;

        var clubCode = ClubScope.Primary;
        var vandaag = DateOnly.FromDateTime(DateTime.UtcNow);
        var totEnMet = vandaag.AddDays(VooruitkijkDagen);

        List<WedstrijdZonderCache> wedstrijden;
        try
        {
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            wedstrijden = await SportlinkPublicMatchIdRepository.ZoekWedstrijdenZonderCacheAsync(
                connection, vandaag, totEnMet, clubCode);
        }
        catch (Exception ex)
        {
            // Onder andere: his.matches bestaat nog niet (dynamisch aangemaakt bij de eerste sync).
            log.LogError(ex, "Kon wedstrijden zonder cache-entry niet ophalen — warmup overgeslagen.");
            return;
        }

        if (wedstrijden.Count == 0)
        {
            log.LogInformation("Geen wedstrijden zonder PublicMatchId-cache in de komende {Dagen} dagen.", VooruitkijkDagen + 1);
            return;
        }

        // Groeperen per datum: één MatchProgramOverview-aanroep per dag, niet per wedstrijd. De
        // overview-aanroepen zijn onderling onafhankelijk (elk 12+ seconden) en lopen daarom parallel;
        // het wegschrijven erna hergebruikt één connectie in plaats van één per datum.
        var groepen = wedstrijden.GroupBy(w => w.Datum).ToList();
        var overviews = await Task.WhenAll(groepen.Select(async groep =>
            (Groep: groep, Overview: await sportlinkClient.GetMatchProgramOverviewAsync(RolNaam, groep.Key))));

        using var writeConnection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await writeConnection.OpenAsync();

        foreach (var (groep, overview) in overviews)
        {
            if (overview.Status != SportlinkClubCallStatus.Ok || overview.Data == null)
            {
                log.LogWarning("MatchProgramOverview voor {Datum} gaf status {Status} — {Aantal} wedstrijden overgeslagen deze run (proberen volgende run opnieuw).",
                    groep.Key, overview.Status, groep.Count());
                continue;
            }

            var gevondenAantal = 0;
            foreach (var wedstrijd in groep)
            {
                var match = overview.Data.FirstOrDefault(e => e.ExternalMatchId == wedstrijd.Wedstrijdnummer);
                if (match == null)
                    continue; // Nog niet bekend bij Sportlink voor deze datum — geen fout, volgende run opnieuw proberen.

                await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(
                    writeConnection, wedstrijd.Wedstrijdcode, clubCode, match.PublicMatchId);
                gevondenAantal++;
            }

            log.LogInformation("Warmup {Datum}: {Gevonden}/{Totaal} wedstrijden gecachet.", groep.Key, gevondenAantal, groep.Count());
        }
    }
}
