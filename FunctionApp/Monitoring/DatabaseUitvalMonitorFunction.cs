using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Shared.Monitoring;
using SportlinkFunction.Email;

namespace SportlinkFunction.Monitoring;

/// <summary>
/// Onafhankelijke, dagelijkse controle van de management-plane status van de Azure SQL Database
/// via <see cref="IDatabaseStatusReader"/>.
///
/// <para>
/// De noodmail tijdens e-mailverwerking hangt af van een databaseverbinding die in die verwerking
/// wordt geopend. Deze timer controleert de status onafhankelijk van inkomende e-mail en gebruikt
/// voor de herhaalbeperking dezelfde <see cref="INoodmailThrottleStore"/>.
/// </para>
///
/// <para>
/// De status komt uit een ARM-leesoperatie, die geen verbinding met de bewaakte database vereist.
/// </para>
///
/// <para>
/// De monitor slaat zichzelf over wanneer een van de vereiste Azure Management-instellingen ontbreekt;
/// de e-mail-pipeline-noodmail blijft dan beschikbaar.
/// </para>
///
/// <para>
/// De gedeelde <see cref="DatabaseUitvalCore"/> bepaalt wanneer een status als uitval telt, wanneer
/// er wordt gemeld en wanneer een eerdere melding opnieuw mag worden verstuurd. Deze functie vertaalt
/// de SQL Server-status naar die gedeelde beslissing.
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

    // Letterlijke cron: %…% wordt bij indexeren opgelost; een ontbrekende setting faalt vóór de functiebody. Uitzetten: zie docs/MONITORING.md.
    internal const string Schedule = "0 0 8 * * *";

    [Function("DatabaseUitvalMonitor")]
    public async Task Run(
        [TimerTrigger(Schedule)] TimerInfo timer,
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
