using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Infrastructure;
using FunctionApp.Postgres.Monitoring;
using FunctionApp.Postgres.Planner;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Dagelijkse contract-check (#998, epic #986) — één read-call per dag op de meest recent
/// gecachte <c>PublicMatchId</c> (niet <c>MatchProgramOverview</c>, die is traag en hier niet
/// nodig), om vroegtijdig te merken als Sportlink de vorm van de <c>Match</c>-respons wijzigt
/// (stille bundle-release, geen aankondiging — zie epic #986/docs/SPORTLINK-WEB-EXTENSION.md).
/// <para>
/// Bewust GEEN hergebruik van <c>public.sportlinkmutationaudit</c> — die tabel is "één rij per
/// mutatiepoging", dit is een aparte, niet-mutatie-gebonden controle
/// (<c>public.sportlinkcontractcheck</c>).
/// </para>
/// <para>
/// Alarmering hergebruikt bewust het BESTAANDE noodmail-pad
/// (<c>EmailProcessorFunction.StuurDatabaseNoodmailAsync</c>-patroon, zelfde
/// <see cref="INoodmailThrottleStore"/>) — geen nieuw alarmeringsmechanisme (kostenbeleid:
/// géén betaalde Log Analytics/App Insights-alert-regel, geen GitHub-issue-reporter).
/// </para>
/// </summary>
public static class SportlinkContractCheckTimerFunction
{
    private const string RolNaam = "Wedstrijdzaken";
    internal const string NoodmailSleutel = "sportlink-contract-noodmail";
    private static readonly TimeSpan NoodmailInterval = TimeSpan.FromHours(24);

    [Function("SportlinkContractCheck")]
    public static async Task Run(
        [TimerTrigger("0 30 6 * * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("SportlinkContractCheck");

        if (PostgresAppSettings.GetSetting("sportlinkExtensionEnabled") != "1")
        {
            log.LogInformation("Sportlink Web Extension staat uit — contract-check overgeslagen.");
            return;
        }

        if (!EgressGuard.ExternalIntegrationsAllowed())
        {
            log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — contract-check overgeslagen (#857).");
            return;
        }

        var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
        if (sportlinkClient == null)
        {
            log.LogWarning("ISportlinkClubClient niet geregistreerd — contract-check kan niet draaien.");
            return;
        }

        var clubCode = PostgresClubScope.Primary;

        await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
        await connection.OpenAsync();

        string? publicMatchId;
        await using (var cmd = new NpgsqlCommand(
            "SELECT publicmatchid FROM public.sportlinkpublicmatchidcache WHERE clubcode = @clubcode ORDER BY opgehaaldop DESC LIMIT 1",
            connection))
        {
            cmd.Parameters.AddWithValue("clubcode", clubCode);
            publicMatchId = (string?)await cmd.ExecuteScalarAsync();
        }

        if (publicMatchId == null)
        {
            log.LogInformation("Geen gecachte PublicMatchId gevonden — contract-check overgeslagen (nog geen wedstrijd opgehaald).");
            return;
        }

        var rawResult = await sportlinkClient.GetMatchRawJsonAsync(RolNaam, publicMatchId);
        bool isOk;
        string? afwijkendeVeldenSamenvatting = null;
        string? foutmeldingSamenvatting = null;

        if (rawResult.Status != SportlinkClubCallStatus.Ok || rawResult.Data == null)
        {
            isOk = false;
            foutmeldingSamenvatting = rawResult.FoutmeldingVoorLog ?? $"Status={rawResult.Status}";
        }
        else
        {
            try
            {
                var afwijkend = SportlinkMatchContract.ControleerVorm(rawResult.Data);
                isOk = afwijkend.Count == 0;
                if (!isOk)
                {
                    // Uitsluitend veldNAMEN, nooit waarden (AVG/CISO-regel) — SportlinkMatchContract
                    // garandeert dit al, hier alleen samenvoegen tot één opslagbare string.
                    afwijkendeVeldenSamenvatting = string.Join(", ", afwijkend);
                    foutmeldingSamenvatting = $"Contractvorm afwijkend: {afwijkendeVeldenSamenvatting}";
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                isOk = false;
                foutmeldingSamenvatting = "Respons is geen geldige JSON — contract gebroken.";
                log.LogWarning(ex, "Contract-check: JSON-parsefout bij vormcontrole");
            }
        }

        await using (var insertCmd = new NpgsqlCommand(@"
            INSERT INTO public.sportlinkcontractcheck
                (clubcode, rolnaam, uitgevoerdop, isok, httpstatus, afwijkendevelden, foutmeldingsamenvatting)
            VALUES
                (@clubcode, @rolnaam, now(), @isok, @httpstatus, @afwijkendevelden, @foutmeldingsamenvatting)",
            connection))
        {
            insertCmd.Parameters.AddWithValue("clubcode", clubCode);
            insertCmd.Parameters.AddWithValue("rolnaam", RolNaam);
            insertCmd.Parameters.AddWithValue("isok", isOk);
            insertCmd.Parameters.AddWithValue("httpstatus", (object?)rawResult.HttpStatusCode ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("afwijkendevelden", (object?)afwijkendeVeldenSamenvatting ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("foutmeldingsamenvatting", (object?)foutmeldingSamenvatting ?? DBNull.Value);
            await insertCmd.ExecuteNonQueryAsync();
        }

        var throttleStore = context.InstanceServices.GetService<INoodmailThrottleStore>();
        if (isOk)
        {
            log.LogInformation("Sportlink contract-check geslaagd voor rol '{Rol}'.", RolNaam);
            if (throttleStore != null) await throttleStore.WisAsync(NoodmailSleutel);
            return;
        }

        log.LogWarning("Sportlink contract-check gaf een afwijking: {Fout}", foutmeldingSamenvatting);
        if (throttleStore == null) return;

        var laatsteKeer = await throttleStore.LaatsteKeerVerstuurdAsync(NoodmailSleutel);
        if (laatsteKeer != null && (DateTime.UtcNow - laatsteKeer.Value) < NoodmailInterval)
        {
            log.LogInformation("Contract-check-noodmail binnen throttle-interval — overgeslagen.");
            return;
        }

        // IEmailGraphService kan null zijn (alleen geregistreerd bij Graph-config + EgressGuard, zie
        // Program.cs) — dan alleen loggen + DB-rij wegschrijven (hierboven al gebeurd), geen crash.
        var graphService = context.InstanceServices.GetService<IEmailGraphService>();
        if (graphService == null)
        {
            log.LogWarning("IEmailGraphService niet geregistreerd — contract-check-noodmail kan niet verstuurd worden.");
            return;
        }

        await StuurContractCheckNoodmailAsync(graphService, foutmeldingSamenvatting, throttleStore, log);
    }

    internal static async Task StuurContractCheckNoodmailAsync(
        IEmailGraphService graphService, string? foutmelding, INoodmailThrottleStore throttleStore, ILogger log)
    {
        var mailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        var body = "Sportlink contract-check gaf een afwijking — de vorm van de Match-respons is veranderd.\n\n"
                 + $"Foutmelding: {foutmelding}\n\n"
                 + "Dit is een vroege waarschuwing dat Sportlink de Club-website (mogelijk) heeft bijgewerkt.\n"
                 + "Controleer docs/SPORTLINK-WEB-EXTENSION.md en de laatste rij in public.sportlinkcontractcheck.\n"
                 + "Deze melding wordt niet binnen 24 uur herhaald.";

        try
        {
            await graphService.SendReplyAsync(mailbox,
                "Sportlink contract-check: afwijking gedetecteerd", body, null);
            await throttleStore.RegistreerVerstuurdAsync(NoodmailSleutel, DateTime.UtcNow);
            log.LogWarning("Contract-check-noodmail verstuurd naar {Mailbox}", mailbox);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon contract-check-noodmail niet versturen");
        }
    }
}
