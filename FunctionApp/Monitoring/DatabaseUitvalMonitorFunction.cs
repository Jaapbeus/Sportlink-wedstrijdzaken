using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Shared.Monitoring;
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
///
/// <para>
/// <b>#1268:</b> de beslisregels — wanneer telt iets als uitval, hoe lang moet die duren, hoe vaak mag
/// dezelfde uitval gemeld worden, hoe luidt de melding — staan in <see cref="DatabaseUitvalCore"/> en
/// zijn daar getest. De Postgres-tier gebruikt exact dezelfde regels; hier blijft alleen het
/// ARM-specifieke deel over.
/// </para>
/// </summary>
public class DatabaseUitvalMonitorFunction
{
    /// <summary>Zie <see cref="DatabaseUitvalCore.NoodmailSleutel"/>.</summary>
    internal const string ThrottleSleutel = DatabaseUitvalCore.NoodmailSleutel;

    /// <summary>
    /// Azure SQL serverless kent een routinematige auto-pause; zie
    /// <see cref="DatabaseUitvalCore.MinimaleUitvalVoorMelding"/> voor waarom die drempel bestaat.
    /// </summary>
    internal static readonly TimeSpan MinimaleUitvalVoorMelding = DatabaseUitvalCore.MinimaleUitvalVoorMelding;

    /// <summary>Zie <see cref="DatabaseUitvalCore.MinimaleHerhalingsinterval"/>.</summary>
    internal static readonly TimeSpan MinimaleHerhalingsinterval = DatabaseUitvalCore.MinimaleHerhalingsinterval;

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
    /// is (#831). Het besluit zelf komt uit <see cref="DatabaseUitvalCore.Beoordeel"/> (#1268); hier
    /// blijft alleen het uitvoeren ervan over.
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

        var laatsteMelding = await throttleStore.LaatsteKeerVerstuurdAsync(ThrottleSleutel);
        var besluit = DatabaseUitvalCore.Beoordeel(
            status, laatsteMelding, nuUtc, DatabaseUitvalCore.MinimaleUitvalVoorMelding);

        if (besluit.Actie == DatabaseUitvalActie.WisRegistratie)
        {
            await throttleStore.WisAsync(ThrottleSleutel);
            log.LogInformation("Database-uitvalmonitor: {Reden}", besluit.Reden);
            return;
        }

        if (besluit.Actie != DatabaseUitvalActie.Melden)
        {
            log.LogInformation("Database-uitvalmonitor: {Reden}", besluit.Reden);
            return;
        }

        if (await StuurUitvalMeldingAsync(graphService, besluit.UitvalDuur!.Value, nuUtc, log))
            await throttleStore.RegistreerVerstuurdAsync(ThrottleSleutel, nuUtc);
    }

    private static async Task<bool> StuurUitvalMeldingAsync(
        IEmailGraphService graphService, TimeSpan uitvalDuur, DateTime nuUtc, ILogger log)
    {
        var mailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(nuUtc, nlZone);

        var body = DatabaseUitvalCore.BouwNoodmailBody(new DatabaseUitvalMeldingContext(
            UitvalDuur: uitvalDuur,
            TijdstipControleLokaal: nlTijd,
            StatusOmschrijving: "gepauzeerd",
            DuurHerkomst: "het tijdstip waarop het platform de database heeft gepauzeerd (properties.pausedDate)",
            BronOmschrijving: "de Azure Management API",
            VermoedelijkeOorzaak: "de maandelijkse gratis vCore-limiet is bereikt",
            ControleStappen:
            [
                "Azure Portal -> SQL-database -> Overzicht -> Status (moet 'Online' zijn)",
                "Compute + storage -> Free monthly vCore amount (maandlimiet bereikt?)",
                "Bij een bereikte maandlimiet: Compute and Storage -> \"Continue using database with additional charges\"",
            ]));

        try
        {
            await graphService.SendReplyAsync(mailbox, DatabaseUitvalCore.NoodmailOnderwerp, body, null);
            // Geen ontvangeradres in het log (SECURITY.md Laag 5: e-mailadressen nooit loggen) — #1201.
            // De uitvalduur is veilige, technische metadata en blijft wél zichtbaar: zonder die waarde
            // is uit het log niet af te leiden hoe ernstig de gemelde uitval was.
            log.LogWarning(
                "Onafhankelijke database-uitvalmelding verstuurd naar de geconfigureerde GraphMailbox " +
                "(uitvalduur {Uren:F0} uur)", uitvalDuur.TotalHours);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon onafhankelijke database-uitvalmelding niet versturen");
            return false;
        }
    }
}
