namespace Planner.Shared.Monitoring;

/// <summary>
/// Wat de statuscontrole over de database heeft vastgesteld. Bewust drie waarden en niet een
/// <c>bool</c>: "ik weet het niet" is een eigen uitkomst die iets anders moet doen dan "hij draait".
/// </summary>
public enum DatabaseBeschikbaarheid
{
    /// <summary>De database draait — een eventuele openstaande uitvalmelding mag gewist worden.</summary>
    Beschikbaar,

    /// <summary>De database ligt eruit — kandidaat voor een melding.</summary>
    Uitgevallen,

    /// <summary>
    /// Tijdelijke of onbekende toestand (opstarten, herstarten, upgraden, een statuswaarde die dit
    /// programma niet kent). Niets doen en de volgende run afwachten: meldt hier niet op, maar wist
    /// ook geen openstaande registratie — anders zou één herstart een lopende uitvalmelding
    /// stilzwijgend resetten.
    /// </summary>
    Onbepaald,
}

/// <summary>
/// Uitkomst van één statuscontrole, tier-onafhankelijk.
/// </summary>
/// <param name="RuweStatus">
/// De onbewerkte statuswaarde van het platform (bijv. <c>Online</c>/<c>Paused</c> van de Azure SQL
/// Database REST API, of <c>ACTIVE_HEALTHY</c>/<c>INACTIVE</c> van de Supabase Management API).
/// Uitsluitend voor logging en de meldingstekst — beslissingen gaan via
/// <paramref name="Beschikbaarheid"/>.
/// </param>
/// <param name="Beschikbaarheid">De vertaling van <paramref name="RuweStatus"/> naar een besluitbare waarde.</param>
/// <param name="UitgevallenSindsUtc">
/// Sinds wanneer de uitval loopt (UTC), of <c>null</c> als dat niet vast te stellen is. Let op de
/// herkomst: het ene platform levert dit zelf (Azure SQL: <c>properties.pausedDate</c>), het andere
/// niet (Supabase levert géén tijdstempel) — daar is dit de eerste waarneming door de monitor zelf,
/// wat een ondergrens is en niet het werkelijke moment van uitvallen.
/// </param>
/// <param name="Bron">
/// Waar deze uitkomst vandaan komt, in lopende tekst ("de Azure Management API"). Staat letterlijk
/// in de noodmail: de ontvanger moet kunnen zien of het om een statusuitlezing of om een
/// verbindingsprobe gaat, want die twee geven niet hetzelfde soort zekerheid.
/// </param>
public sealed record DatabaseStatusInfo(
    string RuweStatus,
    DatabaseBeschikbaarheid Beschikbaarheid,
    DateTime? UitgevallenSindsUtc,
    string Bron = "de statuscontrole");

/// <summary>Wat de monitor naar aanleiding van één controle moet doen.</summary>
public enum DatabaseUitvalActie
{
    /// <summary>Niets doen.</summary>
    GeenActie,

    /// <summary>Een openstaande uitvalregistratie wissen — de database is weer beschikbaar.</summary>
    WisRegistratie,

    /// <summary>Een noodmail versturen en de verzending registreren.</summary>
    Melden,
}

/// <param name="Actie">Wat er moet gebeuren.</param>
/// <param name="UitvalDuur">Gevuld bij <see cref="DatabaseUitvalActie.Melden"/>, anders <c>null</c>.</param>
/// <param name="Reden">Korte, niet-persoonsgebonden toelichting voor het functielog.</param>
public sealed record DatabaseUitvalBesluit(
    DatabaseUitvalActie Actie,
    TimeSpan? UitvalDuur,
    string Reden);

/// <summary>
/// Gegevens voor de tekst van de uitval-noodmail. De skeletzin is voor beide tiers gelijk; wat
/// verschilt is uitsluitend de invulling (welk platform, welke controlestappen).
/// </summary>
/// <param name="UitvalDuur">Hoe lang de uitval loopt volgens <paramref name="DuurHerkomst"/>.</param>
/// <param name="TijdstipControleLokaal">Tijdstip van deze controle, al omgerekend naar lokale tijd.</param>
/// <param name="StatusOmschrijving">Hoe de toestand in de mail heet, bijv. "gepauzeerd".</param>
/// <param name="DuurHerkomst">
/// Waar <paramref name="UitvalDuur"/> vandaan komt. Dit staat letterlijk in de mail: het verschil
/// tussen "het platform zegt dat hij sinds X uit staat" en "deze monitor zag hem X geleden voor het
/// eerst uit staan" is voor de ontvanger het verschil tussen een feit en een ondergrens.
/// </param>
/// <param name="BronOmschrijving">Waar de status vandaan komt, bijv. "de Azure Management API".</param>
/// <param name="VermoedelijkeOorzaak">De meest waarschijnlijke oorzaak voor dít platform.</param>
/// <param name="ControleStappen">Wat de beheerder moet nakijken, één regel per stap.</param>
public sealed record DatabaseUitvalMeldingContext(
    TimeSpan UitvalDuur,
    DateTime TijdstipControleLokaal,
    string StatusOmschrijving,
    string DuurHerkomst,
    string BronOmschrijving,
    string VermoedelijkeOorzaak,
    IReadOnlyList<string> ControleStappen);

/// <summary>
/// Tier-onafhankelijke kern van de onafhankelijke database-uitvalmonitor (#831, geport naar de
/// Postgres-tier bij #1268).
///
/// <para>
/// <b>Waarom deze monitor bestaat.</b> De noodmail in <c>EmailProcessorFunction</c> wordt alleen
/// verstuurd als de databaseverbinding wordt geopend vanuit fase 2 van de e-mailverwerking — en die
/// fase wordt overgeslagen zodra er geen (of alleen buiten-scope) e-mail binnenkomt. Tijdens de 5+
/// dagen durende uitval van 25-30 augustus 2026 (#799/#808) bleek dát de eigenlijke oorzaak van
/// "geen enkele melding". Deze monitor draait op zijn eigen timer en is dus niet afhankelijk van
/// inkomend e-mailverkeer.
/// </para>
///
/// <para>
/// <b>Waarom dit gedeeld is (#1268).</b> De beslisregels — wanneer telt iets als uitval, hoe lang
/// moet die duren, hoe vaak mag dezelfde uitval gemeld worden, hoe luidt de melding — gaan niet
/// over de database maar over alarmering. Ze horen daarom in één bestand en niet per tier
/// gekopieerd; zelfde grens en zelfde precedent als
/// <c>Planner.Shared/Integrations/SportlinkClub/SportlinkEndpointCore.cs</c> (#1266) en
/// <c>Planner.Shared/Theming/ThemeCore.cs</c> (#1248). Wat per tier verschilt, is uitsluitend het
/// ophalen van de status: een ARM-leesoperatie op Azure SQL versus de Supabase Management API of
/// een verbindingsprobe.
/// </para>
/// </summary>
public static class DatabaseUitvalCore
{
    /// <summary>
    /// Gedeelde throttle-sleutel met de database-noodmail van <c>EmailProcessorFunction</c>: welk van
    /// de twee paden ook het eerst een melding verstuurt, onderdrukt de ander voor dezelfde uitval.
    /// </summary>
    public const string NoodmailSleutel = "database-noodmail";

    /// <summary>
    /// Sleutel waaronder een platform zónder eigen uitval-tijdstempel de eerste waarneming van een
    /// uitval vastlegt (#1268). Bewust dezelfde opslag als de throttle: die staat buiten de database
    /// die hier juist onbereikbaar kan zijn, en buiten het procesgeheugen dat bij elke cold start
    /// reset. Dit is geen verzonden melding en dus bewust een eigen sleutel.
    /// </summary>
    public const string EersteWaarnemingSleutel = "database-uitval-eerste-waarneming";

    /// <summary>Onderwerp van de uitval-noodmail. Gelijk op beide tiers.</summary>
    public const string NoodmailOnderwerp = "URGENT: Database staat langdurig uit";

    /// <summary>
    /// Drempel voor een platform met een <b>routinematige</b> auto-pause, zoals Azure SQL serverless:
    /// zo'n pauze herstelt doorgaans binnen enkele minuten bij de eerstvolgende toegang. Een pauze die
    /// langer aanhoudt dan deze marge duidt op een structureel probleem (bijv. de maandelijkse gratis
    /// vCore-limiet bereikt) in plaats van routine-gedrag — en voorkomt ruis op elke normale
    /// nachtelijke auto-pause, exact het bezwaar tegen een kale Activity Log Alert (#831).
    /// </summary>
    public static readonly TimeSpan MinimaleUitvalVoorMelding = TimeSpan.FromHours(6);

    /// <summary>
    /// Drempel voor een platform <b>zonder</b> routinematige auto-pause, zoals een beheerde
    /// Postgres-hostingomgeving (#1268). Daar bestaat geen "normale, korte pauze die vanzelf
    /// herstelt": een pauze is altijd een echte, aanhoudende storing. De tijdelijke toestanden die
    /// zo'n platform wél kent (opstarten, herstarten, upgraden) vallen al weg als
    /// <see cref="DatabaseBeschikbaarheid.Onbepaald"/> en bereiken deze drempel nooit. Een extra
    /// wachttijd zou hier dus geen ruis filteren maar alleen de melding vertragen — bij een
    /// dagelijkse timer met een volle dag.
    /// </summary>
    public static readonly TimeSpan MinimaleUitvalVoorMeldingBeheerdePostgres = TimeSpan.Zero;

    /// <summary>
    /// Geen herhaalde melding binnen dit venster. Een dagelijkse schedule zorgt al voor een
    /// natuurlijke maximale herhalingsfrequentie tijdens een langdurige uitval (~1x per dag); dit
    /// voorkomt alleen dubbele mails als de functie een keer vaker dan gepland binnen één dag draait.
    /// </summary>
    public static readonly TimeSpan MinimaleHerhalingsinterval = TimeSpan.FromHours(20);

    /// <summary>
    /// Vertaalt <c>properties.status</c> van de Azure SQL Database REST API naar een besluitbare
    /// waarde. Uitsluitend <c>Paused</c> telt als uitval — elke andere waarde, inclusief
    /// <c>Pausing</c> en <c>Resuming</c>, geldt als beschikbaar. Dat is bewust het gedrag van vóór
    /// #1268 en wordt hier niet stilzwijgend aangescherpt: Azure levert bij een echte pauze ook
    /// <c>pausedDate</c>, dus de duurcontrole vangt de tussenstanden al af.
    /// </summary>
    public static DatabaseBeschikbaarheid BepaalAzureSqlBeschikbaarheid(string? ruweStatus)
        => string.Equals(ruweStatus, "Paused", StringComparison.OrdinalIgnoreCase)
            ? DatabaseBeschikbaarheid.Uitgevallen
            : DatabaseBeschikbaarheid.Beschikbaar;

    /// <summary>
    /// Vertaalt de <c>status</c> van een Supabase-project (Management API,
    /// <c>GET /v1/projects/{ref}</c>) naar een besluitbare waarde. De lijst met statuswaarden is
    /// overgenomen uit de OpenAPI-specificatie van die API, niet uit een voorbeeldrespons.
    ///
    /// <para>
    /// Alles wat niet in een van beide lijsten staat — inclusief <c>UNKNOWN</c> en een statuswaarde
    /// die later wordt toegevoegd — is <see cref="DatabaseBeschikbaarheid.Onbepaald"/>. Dat is de
    /// veilige kant op: een onbekende waarde mag geen URGENT-mail veroorzaken en mag evenmin een
    /// lopende uitvalmelding wissen.
    /// </para>
    /// </summary>
    public static DatabaseBeschikbaarheid BepaalBeheerdePostgresBeschikbaarheid(string? ruweStatus)
    {
        if (string.IsNullOrWhiteSpace(ruweStatus)) return DatabaseBeschikbaarheid.Onbepaald;

        var status = ruweStatus.Trim().ToUpperInvariant();

        // Draait. ACTIVE_UNHEALTHY hoort hier bewust NIET bij: het project bestaat en is niet
        // gepauzeerd, maar of de database bruikbaar is, staat niet vast.
        if (status == "ACTIVE_HEALTHY") return DatabaseBeschikbaarheid.Beschikbaar;

        // Ligt eruit en blijft er voorlopig uit — dit is precies het scenario van #799/#808.
        if (status is "INACTIVE" or "PAUSING" or "PAUSE_FAILED"
                   or "GOING_DOWN" or "INIT_FAILED" or "RESTORE_FAILED" or "REMOVED")
            return DatabaseBeschikbaarheid.Uitgevallen;

        // COMING_UP, RESTARTING, RESTORING, UPGRADING, RESIZING, ACTIVE_UNHEALTHY, UNKNOWN en
        // alles wat later bijkomt.
        return DatabaseBeschikbaarheid.Onbepaald;
    }

    /// <summary>
    /// Bepaalt sinds wanneer de uitval loopt op een platform dat dat zelf niet bijhoudt: de eerder
    /// vastgelegde eerste waarneming, of anders nú. Puur, zodat de aanroeper alleen nog hoeft te
    /// beslissen of hij de uitkomst wegschrijft.
    /// </summary>
    /// <returns>
    /// Het starttijdstip, plus of dat nog vastgelegd moet worden (alleen bij de eerste waarneming).
    /// </returns>
    public static (DateTime StartUtc, bool MoetVastleggen) BepaalUitvalStart(
        DateTime? eerdereWaarnemingUtc, DateTime nuUtc)
        => eerdereWaarnemingUtc is { } eerder
            ? (eerder, false)
            : (nuUtc, true);

    /// <summary>
    /// De enige beslisregel van deze monitor: wat moet er gebeuren na één statuscontrole?
    /// </summary>
    /// <param name="status">De uitkomst van de statuscontrole.</param>
    /// <param name="laatsteMeldingUtc">Wanneer voor deze uitval voor het laatst gemeld is, of <c>null</c>.</param>
    /// <param name="nuUtc">Nu, in UTC — nooit lokale tijd (#246).</param>
    /// <param name="minimaleUitvalVoorMelding">
    /// Hoe lang de uitval minstens moet duren. Per platform verschillend; zie
    /// <see cref="MinimaleUitvalVoorMelding"/> en <see cref="MinimaleUitvalVoorMeldingBeheerdePostgres"/>.
    /// </param>
    public static DatabaseUitvalBesluit Beoordeel(
        DatabaseStatusInfo status,
        DateTime? laatsteMeldingUtc,
        DateTime nuUtc,
        TimeSpan minimaleUitvalVoorMelding)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (status.Beschikbaarheid == DatabaseBeschikbaarheid.Onbepaald)
            return new DatabaseUitvalBesluit(DatabaseUitvalActie.GeenActie, null,
                $"Status '{status.RuweStatus}' is een tussentoestand — geen melding, geen wis");

        if (status.Beschikbaarheid == DatabaseBeschikbaarheid.Beschikbaar)
            return laatsteMeldingUtc is null
                ? new DatabaseUitvalBesluit(DatabaseUitvalActie.GeenActie, null,
                    $"Status '{status.RuweStatus}' — database beschikbaar")
                : new DatabaseUitvalBesluit(DatabaseUitvalActie.WisRegistratie, null,
                    $"Status '{status.RuweStatus}' — eerdere uitvalmelding-registratie wissen");

        // Zonder starttijdstip kan de duur niet bepaald worden — dan liever niets melden dan een
        // fout-positief. Bewust ook geen wis: er staat wel degelijk iets uit.
        if (status.UitgevallenSindsUtc is not { } sinds)
            return new DatabaseUitvalBesluit(DatabaseUitvalActie.GeenActie, null,
                $"Status '{status.RuweStatus}' maar geen starttijdstip bekend — duur niet vast te stellen");

        var uitvalDuur = nuUtc - sinds;
        if (uitvalDuur < minimaleUitvalVoorMelding)
            return new DatabaseUitvalBesluit(DatabaseUitvalActie.GeenActie, null,
                $"Uitval duurt {uitvalDuur.TotalHours:F1} uur — onder de drempel van "
                + $"{minimaleUitvalVoorMelding.TotalHours:F1} uur");

        if (laatsteMeldingUtc is { } laatste && (nuUtc - laatste) < MinimaleHerhalingsinterval)
            return new DatabaseUitvalBesluit(DatabaseUitvalActie.GeenActie, uitvalDuur,
                $"Al gemeld binnen de laatste {MinimaleHerhalingsinterval.TotalHours:F0} uur — geen herhaling");

        return new DatabaseUitvalBesluit(DatabaseUitvalActie.Melden, uitvalDuur,
            $"Uitval duurt {uitvalDuur.TotalHours:F1} uur — melding versturen");
    }

    /// <summary>
    /// Tekst van de uitval-noodmail. Eén skelet voor beide tiers; alleen de invulling verschilt —
    /// zelfde vorm als <c>SportlinkEndpointCore.BouwContractCheckNoodmailBody</c>.
    /// </summary>
    public static string BouwNoodmailBody(DatabaseUitvalMeldingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stappen = context.ControleStappen.Count == 0
            ? string.Empty
            : "Controleer:\n"
              + string.Join("\n", context.ControleStappen.Select(stap => $"  - {stap}"))
              + "\n\n";

        return $"URGENT: De database staat {BeschrijfDuur(context.UitvalDuur)} {context.StatusOmschrijving}.\n\n"
             + $"Tijdstip van deze controle: {context.TijdstipControleLokaal:dd-MM-yyyy HH:mm}\n"
             + $"Duur vastgesteld op basis van: {context.DuurHerkomst}\n\n"
             + "Deze melding komt van de onafhankelijke database-uitvalmonitor. Die controleert de "
             + $"status via {context.BronOmschrijving}, los van de e-mailverwerking. Komt er geen (of "
             + "geen relevante) e-mail binnen terwijl de database uit staat, dan zou de "
             + "e-mail-pipeline-afhankelijke noodmail dit nooit signaleren.\n\n"
             + $"Meest waarschijnlijke oorzaak: {context.VermoedelijkeOorzaak}\n\n"
             + stappen
             + $"Deze melding wordt niet binnen {MinimaleHerhalingsinterval.TotalHours:F0} uur herhaald.";
    }

    /// <summary>
    /// "al circa 14 uur" / "sinds kort". Een duur van bijna nul als "al circa 0 uur" schrijven leest
    /// als een fout in de melding zelf, en dat is precies het moment waarop de ontvanger hem niet
    /// serieus neemt.
    /// </summary>
    internal static string BeschrijfDuur(TimeSpan uitvalDuur)
        => uitvalDuur < TimeSpan.FromHours(1)
            ? "sinds kort"
            : $"al circa {uitvalDuur.TotalHours:F0} uur";
}
