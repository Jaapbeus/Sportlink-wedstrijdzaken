using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Infrastructure;
using FunctionApp.Postgres.Planner;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Proactieve keep-alive voor Sportlink-refresh-tokens (epic #986, vervolg op #990/#991).
/// <para>
/// <b>Waarom dit bestaat:</b> Keycloak deactiveert een refresh-token na een periode zonder gebruik
/// — live vastgesteld tijdens het testen van #987's vervolgscript
/// (<c>invalid_grant: "Token is not active"</c>), ondanks dat de 6-uurs <c>refresh_expires_in</c>
/// nog niet verstreken was (zie de #990-comment van 2026-09-05). Zonder een periodieke, van
/// gebruikersactiviteit onafhankelijke ververs-actie raakt de koppeling dus onbedoeld inactief na
/// een rustige periode (avond/nacht/weekend zonder Wedstrijdzaken-activiteit) — dan moet een mens
/// de koppeling handmatig opnieuw leggen via <c>Tools/SportlinkTokenCapture</c>.
/// </para>
/// <para>
/// Uur-interval is ruim binnen de bevestigde 6-uurs-geldigheidsduur (zie
/// <c>docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md</c> §2.6) — geen aparte configureerbare
/// schedule-instelling nodig (in tegenstelling tot <c>%FETCH_SCHEDULE%</c>), dit interval is geen
/// operationele afweging maar een vaste, technisch bepaalde ondergrens.
/// </para>
/// <para>
/// Alleen voor de Postgres-tier: de SQL Server-tier is sinds de productiecutover rollback-only
/// (geen productieverkeer) en heeft nog de oudere ARM-API-tokenopslag — zie issue #1020 voor de
/// bewust nog niet genomen beslissing daarover. Een keep-alive bouwen voor een tier die mogelijk
/// een andere tokenopslag krijgt, zou voorbarig werk zijn.
/// </para>
/// </summary>
public static class SportlinkTokenKeepAliveTimerFunction
{
    [Function("SportlinkTokenKeepAlive")]
    public static async Task Run(
        [TimerTrigger("0 0 * * * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("SportlinkTokenKeepAlive");

        if (PostgresAppSettings.GetSetting("sportlinkExtensionEnabled") != "1")
        {
            log.LogInformation("Sportlink Web Extension staat uit — keep-alive overgeslagen.");
            return;
        }

        if (!EgressGuard.ExternalIntegrationsAllowed())
        {
            log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — keep-alive overgeslagen (#857).");
            return;
        }

        var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
        if (sportlinkClient == null)
        {
            log.LogWarning("ISportlinkClubClient niet geregistreerd — keep-alive kan niet draaien.");
            return;
        }

        List<string> rollen;
        try
        {
            rollen = await LeesGekoppeldeRollenAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon gekoppelde Sportlink-rollen niet ophalen — keep-alive overgeslagen.");
            return;
        }

        if (rollen.Count == 0)
        {
            log.LogInformation("Geen gekoppelde Sportlink-rollen gevonden — niets te verversen.");
            return;
        }

        foreach (var rol in rollen)
        {
            try
            {
                var status = await sportlinkClient.VerversTokenAsync(rol);
                log.LogInformation("Keep-alive voor rol '{Rol}': {Status}", rol, status);
            }
            catch (Exception ex)
            {
                // Eén mislukte rol mag de andere rollen niet blokkeren.
                log.LogError(ex, "Onverwachte fout bij keep-alive voor rol '{Rol}'", rol);
            }
        }
    }

    /// <summary>Alle rolnamen met een opgeslagen refresh-token voor de primaire club — geen
    /// aparte enumeratiemethode op <see cref="ISportlinkClubTokenStore"/> nodig (die interface is
    /// gedeeld met de nog-ARM-API-gebaseerde SQL Server-tier, zie #1020); deze query blijft
    /// Postgres-tier-specifiek, net als de rest van dit bestand.</summary>
    private static async Task<List<string>> LeesGekoppeldeRollenAsync()
    {
        var rollen = new List<string>();
        await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
        await connection.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT DISTINCT rolnaam FROM public.sportlinkservicetokens WHERE clubcode = @clubcode",
            connection);
        cmd.Parameters.AddWithValue("clubcode", PostgresClubScope.Primary);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rollen.Add(reader.GetString(0));

        return rollen;
    }
}
