using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SportlinkFunction.Email;

namespace SportlinkFunction.Monitoring;

/// <summary>
/// Onafhankelijke, dagelijkse controle van de management-plane status van de Azure SQL Database
/// (#831).
///
/// <para>
/// De bestaande noodmail in <c>EmailProcessorFunction</c> wordt alleen verstuurd als de
/// databaseverbinding wordt geopend vanuit fase 2 van de e-mailverwerking — en die fase wordt
/// overgeslagen zodra er geen (of alleen buiten-scope) e-mail binnenkomt. Tijdens de 5+ dagen durende
/// uitval van 25-30 augustus 2026 (#799/#808) bleek dát de eigenlijke oorzaak van "geen enkele
/// melding": zonder relevante inkomende e-mail werd de databaseverbinding nooit geprobeerd, dus werd de
/// uitval ook nooit gedetecteerd — los van de cold-start-gevoeligheid van de throttle zelf (ook
/// gefixt, zie <see cref="INoodmailThrottleStore"/>).
/// </para>
///
/// <para>
/// Deze functie maakt zich daarom bewust los van de e-mailpoller: hij leest de status rechtstreeks via
/// een ARM-leesoperatie (<see cref="IDatabaseStatusReader"/>), wat geen databaseverbinding vereist en
/// dus niet zelf slachtoffer kan worden van dezelfde storing. Dit kost niets extra: Function-executies
/// vallen ruim binnen de Consumption-plan-limiet (1x per dag) en ARM-managementaanroepen worden niet
/// gefactureerd als database-compute.
/// </para>
///
/// <para>
/// Optioneel: zonder <c>AzureSubscriptionId</c>/<c>AzureResourceGroupName</c>/<c>AzureSqlServerName</c>/
/// <c>AzureSqlDatabaseName</c> slaat deze functie zichzelf over — hetzelfde graceful-fallbackpatroon als
/// <c>AdminSettingsFunction.TriggerFunctionAppRestartAsync</c>. Een club kan dus zonder extra
/// Azure-configuratie blijven draaien op alleen de bestaande, e-mail-pipeline-afhankelijke noodmail.
/// </para>
/// </summary>
public class DatabaseUitvalMonitorFunction
{
    /// <summary>Gedeelde throttle-sleutel met <c>EmailProcessorFunction</c>'s database-noodmail: welke
    /// van de twee paden ook het eerst een melding verstuurt, onderdrukt de ander voor dezelfde uitval.</summary>
    internal const string ThrottleSleutel = "database-noodmail";

    /// <summary>
    /// Een normale serverless auto-pause herstelt doorgaans binnen enkele minuten bij de eerstvolgende
    /// toegang. Een pauze die langer aanhoudt dan deze marge duidt op een structureel probleem (bijv.
    /// de maandelijkse gratis vCore-limiet bereikt) in plaats van routine-gedrag — en voorkomt ruis op
    /// elke normale nachtelijke auto-pause (exact het bezwaar tegen een kale Activity Log Alert, zie
    /// issue #831).
    /// </summary>
    internal static readonly TimeSpan MinimaleUitvalVoorMelding = TimeSpan.FromHours(6);

    /// <summary>
    /// Geen herhaalde melding binnen dit venster. De dagelijkse schedule zorgt al voor een natuurlijke
    /// maximale herhalingsfrequentie tijdens een langdurige uitval (~1x per dag); dit voorkomt alleen
    /// dubbele mails als de functie een keer vaker dan gepland binnen één dag draait.
    /// </summary>
    internal static readonly TimeSpan MinimaleHerhalingsinterval = TimeSpan.FromHours(20);

    [Function("DatabaseUitvalMonitor")]
    public async Task Run(
        [TimerTrigger("%DATABASE_STATUS_MONITOR_SCHEDULE%")] TimerInfo timer,
        FunctionContext context)
    {
        var log = context.GetLogger("DatabaseUitvalMonitor");

        var subscriptionId = Environment.GetEnvironmentVariable("AzureSubscriptionId");
        var resourceGroup = Environment.GetEnvironmentVariable("AzureResourceGroupName");
        var sqlServerName = Environment.GetEnvironmentVariable("AzureSqlServerName");
        var sqlDatabaseName = Environment.GetEnvironmentVariable("AzureSqlDatabaseName");

        if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(resourceGroup)
            || string.IsNullOrWhiteSpace(sqlServerName) || string.IsNullOrWhiteSpace(sqlDatabaseName))
        {
            log.LogInformation(
                "Azure Management env vars niet volledig geconfigureerd (AzureSubscriptionId / " +
                "AzureResourceGroupName / AzureSqlServerName / AzureSqlDatabaseName) — onafhankelijke " +
                "database-uitvalmonitor overgeslagen. De bestaande, e-mail-pipeline-afhankelijke " +
                "noodmail blijft de enige melding zolang dit niet is geconfigureerd.");
            return;
        }

        var graphService = context.InstanceServices.GetService<IEmailGraphService>();
        if (graphService is null)
        {
            log.LogWarning("GraphServiceClient niet beschikbaar — database-uitvalmonitor kan geen melding versturen");
            return;
        }

        var throttleStore = context.InstanceServices.GetRequiredService<INoodmailThrottleStore>();
        var statusReader = context.InstanceServices.GetRequiredService<IDatabaseStatusReader>();

        await VerwerkStatusAsync(
            statusReader, throttleStore, graphService,
            subscriptionId, resourceGroup, sqlServerName, sqlDatabaseName,
            DateTime.UtcNow, log);
    }

    /// <summary>
    /// Kernlogica, los van de Functions-runtime zodat dit zonder een echte Azure-omgeving unit-testbaar
    /// is (#831).
    /// </summary>
    internal static async Task VerwerkStatusAsync(
        IDatabaseStatusReader statusReader,
        INoodmailThrottleStore throttleStore,
        IEmailGraphService graphService,
        string subscriptionId, string resourceGroup, string sqlServerName, string sqlDatabaseName,
        DateTime nuUtc,
        ILogger log)
    {
        DatabaseStatusInfo status;
        try
        {
            status = await statusReader.LeesStatusAsync(subscriptionId, resourceGroup, sqlServerName, sqlDatabaseName);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon databasestatus niet ophalen via de Azure Management API");
            return;
        }

        if (!string.Equals(status.Status, "Paused", StringComparison.OrdinalIgnoreCase))
        {
            if (await throttleStore.LaatsteKeerVerstuurdAsync(ThrottleSleutel) is not null)
            {
                await throttleStore.WisAsync(ThrottleSleutel);
                log.LogInformation("Database-status is '{Status}' — eerdere uitvalmelding-registratie gewist", status.Status);
            }

            return;
        }

        // Zonder pausedDate kan de duur niet betrouwbaar bepaald worden — dan liever niets melden dan
        // een fout-positief op een normale, korte auto-pause.
        var uitvalDuur = status.PausedSinceUtc is { } pausedSinds ? nuUtc - pausedSinds : (TimeSpan?)null;
        if (uitvalDuur is null || uitvalDuur.Value < MinimaleUitvalVoorMelding)
        {
            log.LogInformation(
                "Database gepauzeerd sinds {PausedSinds:o} — binnen normale auto-pause marge, geen melding",
                status.PausedSinceUtc);
            return;
        }

        var laatsteMelding = await throttleStore.LaatsteKeerVerstuurdAsync(ThrottleSleutel);
        if (laatsteMelding is not null && (nuUtc - laatsteMelding.Value) < MinimaleHerhalingsinterval)
        {
            log.LogInformation(
                "Uitvalmelding al verstuurd binnen de laatste {Uren} uur — geen herhaling",
                MinimaleHerhalingsinterval.TotalHours);
            return;
        }

        var verzonden = await StuurUitvalMeldingAsync(graphService, uitvalDuur.Value, log);
        if (verzonden)
            await throttleStore.RegistreerVerstuurdAsync(ThrottleSleutel, nuUtc);
    }

    private static async Task<bool> StuurUitvalMeldingAsync(IEmailGraphService graphService, TimeSpan uitvalDuur, ILogger log)
    {
        var mailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone);

        var body = $"URGENT: De database staat al circa {uitvalDuur.TotalHours:F0} uur gepauzeerd.\n\n"
                 + $"Tijdstip van deze controle: {nlTijd:dd-MM-yyyy HH:mm}\n\n"
                 + "Deze melding komt van de onafhankelijke, dagelijkse database-uitvalmonitor. Die "
                 + "controleert de status rechtstreeks via de Azure Management API, los van de "
                 + "e-mailverwerking. Komt er geen (of geen relevante) e-mail binnen terwijl de database "
                 + "gepauzeerd staat, dan zou de eerdere, e-mail-pipeline-afhankelijke noodmail dit nooit "
                 + "signaleren.\n\n"
                 + "Meest waarschijnlijke oorzaak: de maandelijkse gratis vCore-limiet is bereikt.\n\n"
                 + "Controleer in Azure Portal:\n"
                 + "  • Azure SQL Server → Database → Overzicht → Status (moet 'Online' zijn)\n"
                 + "  • Compute + storage → Free monthly vCore amount (maandlimiet bereikt?)\n\n"
                 + "Als de maandlimiet bereikt is: Azure Portal → SQL database → Compute and Storage → "
                 + "\"Continue using database with additional charges\"";

        try
        {
            await graphService.SendReplyAsync(mailbox,
                "URGENT: Database staat langdurig gepauzeerd", body, null);
            log.LogWarning("Onafhankelijke database-uitvalmelding verstuurd naar {Mailbox}", mailbox);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon onafhankelijke database-uitvalmelding niet versturen");
            return false;
        }
    }
}
