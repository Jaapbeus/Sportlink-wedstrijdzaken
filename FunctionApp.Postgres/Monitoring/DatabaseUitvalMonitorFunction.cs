using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Planner;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Shared.Monitoring;

namespace FunctionApp.Postgres.Monitoring;

/// <summary>
/// Onafhankelijke, dagelijkse controle van de status en bereikbaarheid van de beheerde Postgres-database.
///
/// <para>
/// De timer gebruikt <see cref="IDatabaseStatusReader"/> om onafhankelijk van inkomende e-mail te
/// controleren. <see cref="PostgresDatabaseStatusReader"/> gebruikt de management-API als de
/// instellingen beschikbaar zijn en <c>EgressGuard.ExternalIntegrationsAllowed()</c> dit toestaat;
/// anders voert hij een bereikbaarheidsprobe uit. De gedeelde <see cref="DatabaseUitvalCore"/>
/// bepaalt de meldingsbeslissing en het herhaalinterval.
/// </para>
///
/// <para>
/// Deze monitor gebruikt een vaste dagelijkse schedule en kan per Function App worden uitgezet met
/// de standaard-app-setting <c>AzureWebJobs.DatabaseUitvalMonitor.Disabled</c>.
/// </para>
///
/// <para>
/// De statusbron levert geen begintijd voor een storing. Daarom slaat de monitor de eerste waarneming
/// op onder <see cref="DatabaseUitvalCore.EersteWaarnemingSleutel"/> en rapporteert de duur vanaf dat
/// moment als ondergrens. De registratie staat in dezelfde externe throttle-opslag, niet in de
/// bewaakte database.
/// </para>
/// </summary>
public static class DatabaseUitvalMonitorFunction
{
    /// <summary>
    /// Drempel voor een beheerde Postgres-omgeving; anders dan Azure SQL serverless is hier geen
    /// routinematige auto-pause die vanzelf herstelt om uit te filteren.
    /// </summary>
    internal static readonly TimeSpan MinimaleUitval =
        DatabaseUitvalCore.MinimaleUitvalVoorMeldingBeheerdePostgres;

    [Function("DatabaseUitvalMonitor")]
    public static async Task Run(
        [TimerTrigger("0 0 8 * * *")] TimerInfo timer,
        FunctionContext context)
    {
        var log = context.GetLogger("DatabaseUitvalMonitor");

        var statusReader = context.InstanceServices.GetService<IDatabaseStatusReader>();
        var throttleStore = context.InstanceServices.GetService<INoodmailThrottleStore>();
        if (statusReader is null || throttleStore is null)
        {
            log.LogWarning(
                "IDatabaseStatusReader of INoodmailThrottleStore is niet geregistreerd — "
                + "database-uitvalmonitor overgeslagen.");
            return;
        }

        // Mag null zijn: IEmailGraphService wordt alleen geregistreerd bij Graph-configuratie plus
        // EgressGuard. Dan blijft er logging over en wordt er niets geregistreerd, zodat een
        // volgende run het opnieuw probeert.
        var graphService = context.InstanceServices.GetService<IEmailGraphService>();

        await VerwerkStatusAsync(statusReader, throttleStore, graphService, DateTime.UtcNow, log);
    }

    /// <summary>
    /// Kernstroom, los van de Functions-runtime zodat dit zonder netwerk en zonder database
    /// testbaar is.
    /// </summary>
    internal static async Task VerwerkStatusAsync(
        IDatabaseStatusReader statusReader,
        INoodmailThrottleStore throttleStore,
        IEmailGraphService? graphService,
        DateTime nuUtc,
        ILogger log)
    {
        DatabaseStatusInfo status;
        try
        {
            status = await statusReader.LeesStatusAsync();
        }
        catch (Exception ex)
        {
            // Een kapotte controle is geen uitval. Alleen loggen — nooit een URGENT-mail sturen
            // omdat de meetmethode zelf faalde.
            log.LogError(ex, "Kon de databasestatus niet vaststellen — geen melding verstuurd");
            return;
        }

        status = await VulUitvalStartAanAsync(status, throttleStore, nuUtc, log);

        var laatsteMelding = await throttleStore.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.NoodmailSleutel);
        var besluit = DatabaseUitvalCore.Beoordeel(status, laatsteMelding, nuUtc, MinimaleUitval);

        if (besluit.Actie == DatabaseUitvalActie.WisRegistratie)
        {
            await throttleStore.WisAsync(DatabaseUitvalCore.NoodmailSleutel);
            log.LogInformation("Database-uitvalmonitor: {Reden}", besluit.Reden);
            return;
        }

        if (besluit.Actie != DatabaseUitvalActie.Melden)
        {
            log.LogInformation("Database-uitvalmonitor: {Reden}", besluit.Reden);
            return;
        }

        if (graphService is null)
        {
            log.LogWarning(
                "Database ligt eruit ({Status}) maar IEmailGraphService is niet geregistreerd — "
                + "geen melding verstuurd.", status.RuweStatus);
            return;
        }

        if (await StuurMeldingAsync(graphService, status, besluit.UitvalDuur!.Value, nuUtc, log))
            await throttleStore.RegistreerVerstuurdAsync(DatabaseUitvalCore.NoodmailSleutel, nuUtc);
    }

    /// <summary>
    /// Houdt bij sinds wanneer de uitval loopt, omdat het platform dat niet doet. Bij herstel wordt
    /// de waarneming gewist zodat een volgende, nieuwe uitval weer vanaf nul telt.
    /// </summary>
    private static async Task<DatabaseStatusInfo> VulUitvalStartAanAsync(
        DatabaseStatusInfo status, INoodmailThrottleStore throttleStore, DateTime nuUtc, ILogger log)
    {
        if (status.Beschikbaarheid == DatabaseBeschikbaarheid.Beschikbaar)
        {
            if (await throttleStore.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel) is not null)
            {
                await throttleStore.WisAsync(DatabaseUitvalCore.EersteWaarnemingSleutel);
                log.LogInformation("Database weer beschikbaar — eerste-waarnemingregistratie gewist");
            }

            return status;
        }

        if (status.Beschikbaarheid != DatabaseBeschikbaarheid.Uitgevallen || status.UitgevallenSindsUtc is not null)
            return status;

        var eerder = await throttleStore.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel);
        var (startUtc, moetVastleggen) = DatabaseUitvalCore.BepaalUitvalStart(eerder, nuUtc);
        if (moetVastleggen)
        {
            await throttleStore.RegistreerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel, startUtc);
            log.LogWarning("Database voor het eerst als uitgevallen waargenomen (status {Status})", status.RuweStatus);
        }

        return status with { UitgevallenSindsUtc = startUtc };
    }

    private static async Task<bool> StuurMeldingAsync(
        IEmailGraphService graphService, DatabaseStatusInfo status, TimeSpan uitvalDuur,
        DateTime nuUtc, ILogger log)
    {
        var mailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById(BerichtResponseGenerator.NlTijdzoneId);
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(nuUtc, nlZone);

        var body = DatabaseUitvalCore.BouwNoodmailBody(new DatabaseUitvalMeldingContext(
            UitvalDuur: uitvalDuur,
            TijdstipControleLokaal: nlTijd,
            StatusOmschrijving: $"niet beschikbaar (gerapporteerde toestand: {status.RuweStatus})",
            DuurHerkomst: "de eerste waarneming door deze monitor — de hostingomgeving levert geen "
                          + "tijdstip van uitvallen, dus de werkelijke uitval kan eerder zijn begonnen",
            BronOmschrijving: status.Bron,
            VermoedelijkeOorzaak: "het project is gepauzeerd (op een gratis plan gebeurt dat na een "
                                  + "periode zonder databaseactiviteit) of de hostingomgeving heeft een storing",
            ControleStappen:
            [
                "Dashboard van de Postgres-hostingomgeving -> projectstatus (moet actief zijn)",
                "Staat het project gepauzeerd: daar hervatten. Let op de hersteltermijn van het platform",
                "/api/health van deze Function App -> veld 'database'",
            ]));

        try
        {
            await graphService.SendReplyAsync(mailbox, DatabaseUitvalCore.NoodmailOnderwerp, body, null);
            // Geen ontvangeradres in het log (SECURITY.md Laag 5: e-mailadressen nooit loggen) — #1201.
            log.LogWarning(
                "Onafhankelijke database-uitvalmelding verstuurd naar de geconfigureerde GraphMailbox "
                + "(uitvalduur minstens {Uren:F0} uur)", uitvalDuur.TotalHours);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon onafhankelijke database-uitvalmelding niet versturen");
            return false;
        }
    }
}
