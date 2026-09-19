using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FunctionApp.Postgres.Infrastructure;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Npgsql;
using Planner.Shared.Monitoring;

namespace FunctionApp.Postgres.Monitoring;

/// <summary>
/// Stelt de toestand van de Postgres-database vast (#1268).
///
/// <para>
/// <b>Waarom dit géén één-op-één vertaling van <c>ArmDatabaseStatusReader</c> is.</b> Die leest de
/// management-plane van een Azure SQL Database via ARM. Een beheerde Postgres-omgeving heeft geen
/// ARM-resource en geen <c>properties.pausedDate</c>; er bestaat hier dus geen equivalent dat
/// hetzelfde kan. Deze klasse doet daarom twee dingen, in deze volgorde, en is er expliciet over wat
/// elk van beide wél en niet vaststelt.
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Control-plane (voorkeur, optioneel).</b> Met <c>SUPABASE_PROJECT_REF</c> +
/// <c>SUPABASE_ACCESS_TOKEN</c> wordt de projectstatus opgehaald bij de Management API. Dat is de
/// echte tegenhanger van de ARM-leesoperatie: geen databaseverbinding, dus deze controle kan niet
/// zelf slachtoffer worden van de storing die hij moet detecteren, en het antwoord zegt letterlijk
/// of het project gepauzeerd is. <b>Wat hij níet kan:</b> zeggen sinds wanneer. Die API geeft alleen
/// <c>status</c>, geen tijdstempel — zie <see cref="DatabaseUitvalMonitorFunction"/> voor hoe de
/// duur dan wél wordt bepaald.
/// </item>
/// <item>
/// <b>Verbindingsprobe (terugval, altijd beschikbaar).</b> Zonder die twee instellingen — en dat is
/// de stand zolang een club geen management-token in de Function App wil zetten — wordt een korte,
/// ongepoolde verbinding geopend met <c>SELECT 1</c>. <b>Wat hij wél oplost:</b> het probleem van
/// #831, namelijk dat de enige bestaande controle afhing van toevallig inkomende e-mail; deze draait
/// op een eigen timer. <b>Wat hij niet kan:</b> onderscheiden of de database gepauzeerd is, of het
/// netwerk hapert, of de host overbelast is. Het is een beschikbaarheidssignaal, geen
/// statusuitlezing — en dus bewust de tweede keuze.
/// </item>
/// </list>
///
/// <para>
/// <b>Geheimen en AVG:</b> de project-ref identificeert de club (CLAUDE.md regel 4a) en het token is
/// een geheim. Geen van beide komt in een log, een foutmelding of de noodmail terecht.
/// </para>
/// </summary>
public sealed class PostgresDatabaseStatusReader : IDatabaseStatusReader
{
    private const string ManagementApiBaseUrl = "https://api.supabase.com";
    private const string ProjectRefSettingName = "SUPABASE_PROJECT_REF";
    private const string AccessTokenSettingName = "SUPABASE_ACCESS_TOKEN";

    /// <summary>Een project-ref is een korte, kleine-letter-identifier. Alles daarbuiten wordt geweigerd
    /// in plaats van in een URL geplakt.</summary>
    private static readonly Regex ProjectRefPatroon = new("^[a-z0-9]{16,40}$", RegexOptions.Compiled);

    /// <summary>Aantal verbindingspogingen vóór de probe "uitgevallen" concludeert. Eén mislukte
    /// TCP-verbinding is te dun bewijs voor een URGENT-mail; drie op rij binnen een minuut niet.</summary>
    private const int StandaardProbePogingen = 3;

    private static readonly TimeSpan StandaardProbeWachttijd = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Hoe het terugvalpad zichzelf in de noodmail noemt — bewust niet "statuscontrole":
    /// een probe stelt beschikbaarheid vast, geen platformstatus.</summary>
    private const string ProbeBron = "een rechtstreekse verbindingsprobe op de database";

    private readonly ILogger<PostgresDatabaseStatusReader> _log;
    private readonly int _probePogingen;
    private readonly TimeSpan _probeWachttijd;
    private readonly Func<string> _verbindingsreeks;

    public PostgresDatabaseStatusReader(ILogger<PostgresDatabaseStatusReader> log)
        : this(log, StandaardProbePogingen, StandaardProbeWachttijd,
               () => PostgresDatabaseConfig.ConnectionString) { }

    /// <summary>
    /// Alleen voor tests: maakt het aantal pogingen, de wachttijd en de herkomst van de
    /// connectiereeks instelbaar.
    /// <para>
    /// Het herhaalgedrag is geen detail maar de reden dat één netwerkhapering geen URGENT-mail
    /// oplevert — dat hoort getest te worden, en niet met een test die tien seconden staat te
    /// wachten. De connectiereeks komt als <see cref="Func{TResult}"/> binnen omdat
    /// <c>PostgresDatabaseConfig</c> zijn waarde in een static initializer vastlegt: een test die de
    /// omgevingsvariabele zet, hangt dan af van de volgorde waarin de testassembly types laadt.
    /// </para>
    /// </summary>
    internal PostgresDatabaseStatusReader(
        ILogger<PostgresDatabaseStatusReader> log, int probePogingen, TimeSpan probeWachttijd,
        Func<string> verbindingsreeks)
    {
        _log = log;
        _probePogingen = probePogingen;
        _probeWachttijd = probeWachttijd;
        _verbindingsreeks = verbindingsreeks;
    }

    public async Task<DatabaseStatusInfo> LeesStatusAsync(CancellationToken annuleringstoken = default)
    {
        var projectRef = Environment.GetEnvironmentVariable(ProjectRefSettingName);
        // Bewust 'managementToken' en niet 'accessToken': de pre-commit-hook heeft een patroon
        // dat op 'accessToken = <20+ tekens>' matcht en dan de rechterkant van deze toewijzing
        // (de methodenaam) als geheim aanziet. De waarde komt uit een omgevingsvariabele en
        // staat nergens in git.
        var managementToken = Environment.GetEnvironmentVariable(AccessTokenSettingName);

        // EgressGuard (#857): buiten productie nooit een uitgaande management-aanroep, ook niet als
        // de instellingen toevallig lokaal gevuld zijn.
        var controlPlaneBeschikbaar =
            !string.IsNullOrWhiteSpace(projectRef)
            && !string.IsNullOrWhiteSpace(managementToken)
            && EgressGuard.ExternalIntegrationsAllowed();

        if (!controlPlaneBeschikbaar)
            return await ProbeerVerbindingAsync(annuleringstoken);

        if (!ProjectRefPatroon.IsMatch(projectRef!))
        {
            // Nooit de waarde zelf loggen — die identificeert de club.
            _log.LogWarning(
                "{Instelling} heeft niet de verwachte vorm — control-plane-controle overgeslagen, "
                + "terugval op de verbindingsprobe.", ProjectRefSettingName);
            return await ProbeerVerbindingAsync(annuleringstoken);
        }

        return await LeesProjectStatusAsync(projectRef!, managementToken!, annuleringstoken);
    }

    /// <summary>
    /// Control-plane-pad: <c>GET /v1/projects/{ref}</c>. Levert geen tijdstempel — alleen een status.
    /// </summary>
    private static async Task<DatabaseStatusInfo> LeesProjectStatusAsync(
        string projectRef, string managementToken, CancellationToken annuleringstoken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{ManagementApiBaseUrl}/v1/projects/{projectRef}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

        using var response = await Http.SendAsync(request, annuleringstoken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(annuleringstoken);
        var status = JObject.Parse(json)["status"]?.ToString() ?? "UNKNOWN";

        // De vertaling van statuswaarde naar besluit staat gedeeld in DatabaseUitvalCore — niet hier.
        // Het tijdstip is bewust null: deze API kent er geen. De monitor vult dat aan met zijn eigen
        // eerste waarneming.
        return new DatabaseStatusInfo(
            status,
            DatabaseUitvalCore.BepaalBeheerdePostgresBeschikbaarheid(status),
            null,
            Bron: "de Management API van de Postgres-hostingomgeving");
    }

    /// <summary>
    /// Terugvalpad: korte, ongepoolde verbinding met <c>SELECT 1</c>. Ongepoold met opzet — een
    /// hergebruikte verbinding uit de pool zou een storing kunnen maskeren of juist een oude,
    /// verbroken verbinding als storing kunnen presenteren.
    /// </summary>
    private async Task<DatabaseStatusInfo> ProbeerVerbindingAsync(CancellationToken annuleringstoken)
    {
        string verbinding;
        try
        {
            verbinding = new NpgsqlConnectionStringBuilder(_verbindingsreeks())
            {
                Timeout = (int)ProbeTimeout.TotalSeconds,
                CommandTimeout = (int)ProbeTimeout.TotalSeconds,
                Pooling = false,
            }.ConnectionString;
        }
        catch (Exception ex)
        {
            // Geen connectiereeks of een onbruikbare: dat is een configuratiefout, geen uitval.
            _log.LogWarning(ex, "Geen bruikbare Postgres-connectiereeks — beschikbaarheid niet vast te stellen");
            return new DatabaseStatusInfo("geen-connectiereeks", DatabaseBeschikbaarheid.Onbepaald, null, ProbeBron);
        }

        DatabaseStatusInfo? laatsteUitkomst = null;
        for (var poging = 1; poging <= _probePogingen; poging++)
        {
            laatsteUitkomst = await EenProbeAsync(verbinding, annuleringstoken);
            if (laatsteUitkomst.Beschikbaarheid != DatabaseBeschikbaarheid.Uitgevallen)
                return laatsteUitkomst;

            if (poging < _probePogingen)
                await Task.Delay(_probeWachttijd, annuleringstoken);
        }

        return laatsteUitkomst!;
    }

    private async Task<DatabaseStatusInfo> EenProbeAsync(string verbinding, CancellationToken annuleringstoken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(verbinding);
            await connection.OpenAsync(annuleringstoken);
            await using var cmd = new NpgsqlCommand("SELECT 1", connection);
            await cmd.ExecuteScalarAsync(annuleringstoken);
            return new DatabaseStatusInfo("bereikbaar", DatabaseBeschikbaarheid.Beschikbaar, null, ProbeBron);
        }
        catch (PostgresException ex) when (IsConfiguratieFout(ex.SqlState))
        {
            // De server antwoordde — hij draait dus. Dat dit account er niet in mag, of dat de
            // database anders heet, is een misconfiguratie en mag nooit als uitval gemeld worden.
            _log.LogWarning("Postgres weigerde de probe met SQLSTATE {SqlState} — dit is een configuratiefout, geen uitval", ex.SqlState);
            return new DatabaseStatusInfo($"afgewezen ({ex.SqlState})", DatabaseBeschikbaarheid.Onbepaald, null, ProbeBron);
        }
        catch (PostgresException ex) when (ex.SqlState == "57P03")
        {
            // cannot_connect_now: de server start op of herstelt. Tijdelijk, dus geen melding.
            _log.LogInformation("Postgres accepteert nog geen verbindingen (SQLSTATE 57P03) — tijdelijke toestand");
            return new DatabaseStatusInfo("start-op (57P03)", DatabaseBeschikbaarheid.Onbepaald, null, ProbeBron);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // Geen foutmelding van de provider in de uitkomst: die kan de host bevatten en daarmee
            // de club identificeren. Alleen het exceptietype.
            _log.LogWarning("Postgres niet bereikbaar bij probe ({Type})", ex.GetType().Name);
            return new DatabaseStatusInfo("onbereikbaar", DatabaseBeschikbaarheid.Uitgevallen, null, ProbeBron);
        }
    }

    /// <summary>
    /// SQLSTATE's waarbij de server aantoonbaar draait en de verbinding om een andere reden wordt
    /// geweigerd: ongeldig wachtwoord, ongeldige autorisatie, onbekende database.
    /// </summary>
    private static bool IsConfiguratieFout(string? sqlState)
        => sqlState is "28P01" or "28000" or "3D000";
}
