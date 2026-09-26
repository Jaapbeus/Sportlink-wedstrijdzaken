using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Email;
using SportlinkFunction.Monitoring;
using SportlinkFunction.Planner;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-tegenhanger van
/// <c>FunctionApp.Postgres/Sportlink/SportlinkContractCheckTimerFunction.cs</c> (#1266, #998,
/// epic #986) — dagelijkse contract-check: één read-call per dag op de meest recent gecachte
/// <c>PublicMatchId</c> (niet <c>MatchProgramOverview</c>, die is traag en hier niet nodig), om
/// vroegtijdig te merken als Sportlink de vorm van de <c>Match</c>-respons wijzigt (stille
/// bundle-release, geen aankondiging — zie docs/SPORTLINK-WEB-EXTENSION.md).
/// <para>
/// Bewust GEEN hergebruik van <c>dbo.SportlinkMutationAudit</c> — die tabel is "één rij per
/// mutatiepoging", dit is een aparte, niet-mutatie-gebonden controle
/// (<c>dbo.SportlinkContractCheck</c>).
/// </para>
/// <para>
/// Alarmering hergebruikt bewust het BESTAANDE noodmail-pad (zelfde
/// <see cref="INoodmailThrottleStore"/> als <c>EmailProcessorFunction</c>) — geen nieuw
/// alarmeringsmechanisme (kostenbeleid: géén betaalde Log Analytics/App Insights-alertregel, geen
/// GitHub-issue-reporter).
/// </para>
/// <para>
/// De beoordeling van het antwoord, de throttle-beslissing, het onderwerp en de mailtekst komen uit
/// <see cref="SportlinkEndpointCore"/> — identiek aan de Postgres-tier. Alleen de tabelnaam en de
/// databasetoegang verschillen.
/// </para>
/// </summary>
public static class SportlinkContractCheckTimerFunction
{
    private const string RolNaam = SportlinkEndpointSupport.RolWedstrijdzaken;
    internal const string NoodmailSleutel = SportlinkEndpointCore.ContractCheckNoodmailSleutel;

    /// <summary>Tabel waarnaar de melding verwijst — het enige verschil in de mailtekst tussen de
    /// twee tiers.</summary>
    private const string Tabelnaam = "dbo.SportlinkContractCheck";

    [Function("SportlinkContractCheck")]
    public static async Task Run(
        [TimerTrigger("0 30 6 * * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("SportlinkContractCheck");

        // Anders dan de Postgres-tier moet deze tier de instellingencache expliciet laten laden
        // vóór de toggle gelezen wordt (SystemUtilities.WaitForDatabaseAsync doet beide). Lukt dat
        // niet, dan is er ook niets weg te schrijven — stil afbreken, geen crashende timer.
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Database niet bereikbaar — contract-check overgeslagen.");
            return;
        }

        var sportlinkClient = SportlinkEndpointSupport.ClientVoorTimer(context, log, "contract-check");
        if (sportlinkClient == null) return;

        var clubCode = ClubScope.Primary;

        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();

        string? publicMatchId;
        using (var cmd = new SqlCommand(@"
            SELECT TOP 1 [PublicMatchId] FROM [dbo].[SportlinkPublicMatchIdCache]
            WHERE [ClubCode] = @ClubCode
            ORDER BY [OpgehaaldOp] DESC", connection))
        {
            cmd.Parameters.AddWithValue("@ClubCode", clubCode);
            publicMatchId = (await cmd.ExecuteScalarAsync()) as string;
        }

        if (publicMatchId == null)
        {
            log.LogInformation("Geen gecachte PublicMatchId gevonden — contract-check overgeslagen (nog geen wedstrijd opgehaald).");
            return;
        }

        var rawResult = await sportlinkClient.GetMatchRawJsonAsync(RolNaam, publicMatchId);
        var uitkomst = SportlinkEndpointCore.BeoordeelContractCheck(rawResult, log);

        using (var insertCmd = new SqlCommand(@"
            INSERT INTO [dbo].[SportlinkContractCheck]
                ([ClubCode], [RolNaam], [UitgevoerdOp], [IsOk], [HttpStatus], [AfwijkendeVelden], [FoutmeldingSamenvatting])
            VALUES
                (@ClubCode, @RolNaam, GETUTCDATE(), @IsOk, @HttpStatus, @AfwijkendeVelden, @FoutmeldingSamenvatting)",
            connection))
        {
            insertCmd.Parameters.AddWithValue("@ClubCode", clubCode);
            insertCmd.Parameters.AddWithValue("@RolNaam", RolNaam);
            insertCmd.Parameters.AddWithValue("@IsOk", uitkomst.IsOk);
            insertCmd.Parameters.AddWithValue("@HttpStatus", (object?)rawResult.HttpStatusCode ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@AfwijkendeVelden", (object?)uitkomst.AfwijkendeVelden ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@FoutmeldingSamenvatting", (object?)uitkomst.FoutmeldingSamenvatting ?? DBNull.Value);
            await insertCmd.ExecuteNonQueryAsync();
        }

        var throttleStore = context.InstanceServices.GetService<INoodmailThrottleStore>();
        if (uitkomst.IsOk)
        {
            log.LogInformation("Sportlink contract-check geslaagd voor rol '{Rol}'.", RolNaam);
            if (throttleStore != null) await throttleStore.WisAsync(NoodmailSleutel);
            return;
        }

        log.LogWarning("Sportlink contract-check gaf een afwijking: {Fout}", uitkomst.FoutmeldingSamenvatting);
        if (throttleStore == null) return;

        var laatsteKeer = await throttleStore.LaatsteKeerVerstuurdAsync(NoodmailSleutel);
        if (!SportlinkEndpointCore.MagContractCheckNoodmailVersturen(laatsteKeer, DateTime.UtcNow))
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

        await StuurContractCheckNoodmailAsync(graphService, uitkomst.FoutmeldingSamenvatting, throttleStore, log);
    }

    internal static async Task StuurContractCheckNoodmailAsync(
        IEmailGraphService graphService, string? foutmelding, INoodmailThrottleStore throttleStore, ILogger log)
    {
        var mailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        var body = SportlinkEndpointCore.BouwContractCheckNoodmailBody(foutmelding, Tabelnaam);

        try
        {
            await graphService.SendReplyAsync(mailbox,
                SportlinkEndpointCore.ContractCheckNoodmailOnderwerp, body, null);
            await throttleStore.RegistreerVerstuurdAsync(NoodmailSleutel, DateTime.UtcNow);
            // Geen ontvangeradres in het log (SECURITY.md: e-mailadressen nooit loggen) — #1107 bevinding 12.
            log.LogWarning("Contract-check-noodmail verstuurd.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon contract-check-noodmail niet versturen");
        }
    }
}
