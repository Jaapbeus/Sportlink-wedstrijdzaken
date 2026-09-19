using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Planner;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Shared.Monitoring;

namespace FunctionApp.Postgres.Monitoring;

/// <summary>
/// Onafhankelijke, dagelijkse uitvalbewaking van de database op de Postgres-tier (#1268 — port van
/// #831, dat alleen op de SQL Server-tier bestond).
///
/// <para>
/// <b>Waarom dit nodig was.</b> De noodmail in <c>EmailProcessorFunction</c> gaat alleen uit als de
/// databaseverbinding wordt geopend vanuit fase 2 van de e-mailverwerking, en die fase wordt
/// overgeslagen zodra er geen relevante e-mail binnenkomt. Tijdens de 5+ dagen durende uitval van
/// 25-30 augustus 2026 (#799/#808) bleek dát de reden dat er geen enkele melding aankwam. De
/// Postgres-tier is de tier die in productie draait en had deze bewaking niet — een club had dus
/// geen e-mail-onafhankelijke uitvalmelding.
/// </para>
///
/// <para>
/// <b>Twee verschillen met de SQL Server-tier, allebei bewust.</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>Vaste schedule in code in plaats van <c>%DATABASE_STATUS_MONITOR_SCHEDULE%</c>.</b> De
/// Functions-runtime lost zo'n <c>%…%</c>-verwijzing op bij het <i>indexeren</i> van de functie,
/// vóór er ook maar iets van de functiebody draait. Ontbreekt de app setting, dan komt de hele
/// Function App niet omhoog. Op de tier die in productie draait is dat een onaanvaardbaar risico
/// voor een monitoringfunctie, dus hier een letterlijke cron — zelfde keuze als
/// <c>SportlinkContractCheckTimerFunction</c>. Uitzetten kan met de standaard-app-setting
/// <c>AzureWebJobs.DatabaseUitvalMonitor.Disabled</c>.
/// </item>
/// <item>
/// <b>De uitvalduur komt van de monitor zelf.</b> Azure SQL levert <c>properties.pausedDate</c>;
/// een beheerde Postgres-omgeving levert alleen een status, geen tijdstempel (geverifieerd tegen de
/// OpenAPI-specificatie van die Management API). De eerste waarneming wordt daarom zelf vastgelegd,
/// onder <see cref="DatabaseUitvalCore.EersteWaarnemingSleutel"/> in dezelfde opslag als de throttle
/// — buiten de database die hier juist onbereikbaar kan zijn. Gevolg, en dat staat ook in de mail:
/// de gemelde duur is een <i>ondergrens</i>, niet het werkelijke moment van uitvallen.
/// </item>
/// </list>
///
/// <para>
/// Alle beslisregels staan in <see cref="DatabaseUitvalCore"/> en zijn daar getest; de SQL
/// Server-tier gebruikt exact dezelfde. Hier blijft alleen het Postgres-specifieke deel over.
/// </para>
/// </summary>
public static class DatabaseUitvalMonitorFunction
{
    /// <summary>
    /// Een beheerde Postgres-omgeving kent geen routinematige auto-pause die vanzelf herstelt; zie
    /// <see cref="DatabaseUitvalCore.MinimaleUitvalVoorMeldingBeheerdePostgres"/>.
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
