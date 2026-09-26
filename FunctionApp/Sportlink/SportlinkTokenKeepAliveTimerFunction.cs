using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-tegenhanger van
/// <c>FunctionApp.Postgres/Sportlink/SportlinkTokenKeepAliveTimerFunction.cs</c> (#1266, epic #986)
/// — proactieve keep-alive voor Sportlink-refresh-tokens.
/// <para>
/// <b>Waarom dit bestaat:</b> Keycloak deactiveert een refresh-token na een periode zonder gebruik,
/// ook als de geldigheidsduur formeel nog niet verstreken is (live vastgesteld bij #990). Zonder
/// een periodieke, van gebruikersactiviteit onafhankelijke ververs-actie raakt de koppeling dus
/// onbedoeld inactief na een rustige periode (avond/nacht/weekend), en moet een mens de koppeling
/// handmatig opnieuw leggen.
/// </para>
/// <para>
/// Uur-interval is ruim binnen de bevestigde geldigheidsduur (zie
/// <c>docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md</c> §2.6) — geen aparte configureerbare
/// schedule-instelling nodig (anders dan <c>%FETCH_SCHEDULE%</c>): dit interval is geen
/// operationele afweging maar een vaste, technisch bepaalde ondergrens. Zelfde cron als de
/// Postgres-tier.
/// </para>
/// <para>
/// <b>Eén tierverschil, bewust (#1020, herbevestigd bij #1266).</b> De Postgres-tier leest de
/// gekoppelde rollen uit een eigen DB-tabel. Deze tier bewaart refresh-tokens in Function
/// App-instellingen, dus is de vraag "welke rollen zijn gekoppeld?" hier een vraag aan
/// <see cref="ISportlinkClubTokenStore"/>: van de bekende functionele rollen die met een
/// opgeslagen token. Er is bewust géén enumeratiemethode aan die gedeelde interface toegevoegd —
/// dat zou de Postgres-implementatie raken voor iets dat alleen deze tier nodig heeft.
/// </para>
/// </summary>
public static class SportlinkTokenKeepAliveTimerFunction
{
    /// <summary>De functionele rollen waarmee deze app in Sportlink Club werkt. Zelfde bewust
    /// hardcodeerde lijst als <c>SportlinkExtensieRollenFunction</c> — er is geen API om de
    /// Entra-approllen vanuit de backend te bevragen.</summary>
    private static readonly string[] FunctioneleRollen = { SportlinkEndpointSupport.RolWedstrijdzaken };

    [Function("SportlinkTokenKeepAlive")]
    public static async Task Run(
        [TimerTrigger("0 0 * * * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("SportlinkTokenKeepAlive");

        // Zie SportlinkContractCheckTimerFunction: deze tier laadt de instellingencache via
        // WaitForDatabaseAsync, vóór de toggle gelezen wordt.
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Database niet bereikbaar — keep-alive overgeslagen.");
            return;
        }

        var sportlinkClient = SportlinkEndpointSupport.ClientVoorTimer(context, log, "keep-alive");
        if (sportlinkClient == null) return;

        var tokenStore = context.InstanceServices.GetService<ISportlinkClubTokenStore>();
        if (tokenStore == null)
        {
            log.LogWarning("ISportlinkClubTokenStore niet geregistreerd — keep-alive kan niet draaien.");
            return;
        }

        List<string> rollen;
        try
        {
            // Alleen op aanwezigheid toetsen; de tokenwaarde zelf wordt nooit gelogd of bewaard.
            rollen = FunctioneleRollen
                .Where(rol => !string.IsNullOrWhiteSpace(tokenStore.LeesRefreshToken(rol)))
                .ToList();
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

        // Onafhankelijke roltokens parallel verversen — geen gedeelde state tussen rollen buiten
        // sportlinkClient's eigen per-rol-semafoor, en één mislukte rol mag de andere niet blokkeren.
        await Task.WhenAll(rollen.Select(async rol =>
        {
            try
            {
                var status = await sportlinkClient.VerversTokenAsync(rol);
                log.LogInformation("Keep-alive voor rol '{Rol}': {Status}", rol, status);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Onverwachte fout bij keep-alive voor rol '{Rol}'", rol);
            }
        }));
    }
}
