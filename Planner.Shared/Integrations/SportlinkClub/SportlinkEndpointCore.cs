using Microsoft.Extensions.Logging;

namespace Planner.Shared.Integrations.SportlinkClub;

// Tier-onafhankelijke kern van de Sportlink Web Extension-endpoints en -timers (epic #986, #1122,
// gedeeld gemaakt in #1266). Tot deze klasse bestond stond álle beslislogica in
// FunctionApp.Postgres/Sportlink/SportlinkEndpointSupport.cs; de SQL Server-tier had niets. Een
// tweede kopie maken bij het herstellen van de tier-pariteit zou precies de fout zijn die
// ThemeCore (#1248) en FeedbackCore (#1130) hebben opgeruimd: twee bestanden die niets van elkaar
// weten, niet gedeeld getest worden en dus stilzwijgend uit elkaar lopen.
//
// Zelfde vorm als die twee precedenten: een pure klasse zonder ASP.NET Core- of Azure
// Functions-afhankelijkheid, die statussen en resultaatrecords teruggeeft. De vertaling naar
// IActionResult, de DI-lookup van de client en álle databasetoegang blijven bij de tier-specifieke
// SportlinkEndpointSupport — precies de scheiding die docs/ARCHITECTUUR-DATABASE-TIERS.md §2
// voorschrijft.

/// <summary>
/// Een foutuitkomst van een Sportlink-endpoint: de HTTP-status en de melding die de client ziet.
/// Nooit de onderliggende Sportlink-foutdetails 1-op-1 doorzetten (CISO-regel) — die horen alleen
/// in het log en in het audit-record.
/// </summary>
public sealed record SportlinkEndpointFout(int HttpStatus, string Foutmelding);

/// <summary>Waarom een Sportlink-timer wel of niet mag draaien.</summary>
public enum SportlinkTimerStatus
{
    /// <summary>Toggle staat aan én EgressGuard laat uitgaand verkeer toe.</summary>
    MagDraaien,

    /// <summary>De club heeft de Sportlink Web Extension uitgeschakeld (#988).</summary>
    ExtensieUit,

    /// <summary>EgressGuard blokkeert uitgaande integraties buiten productie (#857).</summary>
    EgressGeblokkeerd
}

/// <summary>
/// De uitkomst van een mutatie, opgesplitst in "wat gaat er in de audit" en "wat ziet de client".
/// De tier voert de audit-afronding uit en vertaalt <see cref="Fout"/>/<see cref="Data"/> naar een
/// HTTP-respons.
/// </summary>
/// <param name="AuditResultaat">Waarde voor het audit-veld <c>Resultaat</c> (#998/#994).</param>
/// <param name="AuditSamenvatting">Korte toelichting voor de audit; <c>null</c> als er niets te melden valt.</param>
/// <param name="Fout">Gevuld als de aanroep zelf mislukte; <c>null</c> als er een inhoudelijk antwoord is.</param>
/// <param name="Data">De respons-data; alleen gevuld als <paramref name="Fout"/> <c>null</c> is.</param>
public sealed record SportlinkMutatieAfronding<T>(
    string AuditResultaat,
    string? AuditSamenvatting,
    SportlinkEndpointFout? Fout,
    T? Data) where T : class;

/// <summary>
/// De beslisregels die élk Sportlink Web Extension-endpoint en élke Sportlink-timer deelt,
/// onafhankelijk van de databasetier.
/// </summary>
public static class SportlinkEndpointCore
{
    /// <summary>De ene functionele rol waarmee deze app in Sportlink Club schrijft (#988).</summary>
    public const string RolWedstrijdzaken = "Wedstrijdzaken";

    /// <summary>Instellingssleutel van de club-toggle voor de Sportlink Web Extension (#988).</summary>
    public const string InstellingExtensieIngeschakeld = "sportlinkExtensionEnabled";

    /// <summary>Instellingssleutel van de dry-run-schakelaar (#998).</summary>
    public const string InstellingDryRun = "sportlinkDryRun";

    /// <summary>Toggle staat uit (HTTP 409).</summary>
    public static SportlinkEndpointFout ExtensieUitFout { get; } =
        new(409, "Sportlink Web Extension staat uit.");

    /// <summary>EgressGuard blokkeert uitgaand verkeer (HTTP 503).</summary>
    public static SportlinkEndpointFout EgressGeblokkeerdFout { get; } =
        new(503, "Uitgaande integraties staan hier niet toe.");

    /// <summary>De client is niet in DI geregistreerd (HTTP 503) — zie <c>Program.cs</c>: registratie
    /// gebeurt alleen als de EgressGuard het toestaat.</summary>
    public static SportlinkEndpointFout ClientNietGeconfigureerdFout { get; } =
        new(503, "Sportlink-client niet geconfigureerd.");

    /// <summary>Log-template voor een timer die wordt overgeslagen omdat de toggle uitstaat.</summary>
    public const string TimerExtensieUitLog = "Sportlink Web Extension staat uit — {Taak} overgeslagen.";

    /// <summary>Log-template voor een timer die wordt overgeslagen door de EgressGuard.</summary>
    public const string TimerEgressGeblokkeerdLog =
        "EgressGuard: uitgaande integraties geblokkeerd buiten productie — {Taak} overgeslagen (#857).";

    /// <summary>Log-template voor een timer zonder geregistreerde client.</summary>
    public const string TimerClientOntbreektLog = "ISportlinkClubClient niet geregistreerd — {Taak} kan niet draaien.";

    /// <summary>
    /// Toggle (#988) + EgressGuard (#857): <c>null</c> als de aanroep door mag, anders de fout die
    /// de client toont.
    /// </summary>
    /// <param name="leesInstelling">Instellingenlezer van de tier (<c>GetSetting</c>).</param>
    /// <param name="egressToegestaan">De EgressGuard van de tier.</param>
    public static SportlinkEndpointFout? ControleerToggleEnEgress(
        Func<string, string?> leesInstelling, Func<bool> egressToegestaan)
        => BepaalTimerStatus(leesInstelling, egressToegestaan) switch
        {
            SportlinkTimerStatus.ExtensieUit => ExtensieUitFout,
            SportlinkTimerStatus.EgressGeblokkeerd => EgressGeblokkeerdFout,
            _ => null
        };

    /// <summary>Dezelfde twee controles, maar als reden — voor timers, die niets teruggeven maar wel
    /// moeten loggen waarom er niets gebeurt.</summary>
    public static SportlinkTimerStatus BepaalTimerStatus(
        Func<string, string?> leesInstelling, Func<bool> egressToegestaan)
    {
        if (leesInstelling(InstellingExtensieIngeschakeld) != "1") return SportlinkTimerStatus.ExtensieUit;
        if (!egressToegestaan()) return SportlinkTimerStatus.EgressGeblokkeerd;
        return SportlinkTimerStatus.MagDraaien;
    }

    /// <summary>
    /// Dry-run-stand van deze club (#998). <b>Fail-safe:</b> alles behalve een expliciet geladen
    /// <c>"0"</c> is dry-run — een nog niet geladen instellingencache (<c>null</c>), een ontbrekende
    /// kolom of een leesfout levert dus dry-run AAN. Met de omgekeerde polariteit
    /// (<c>== "1"</c>) zou een lege cache fail-OPEN zijn: het statuspaneel meldt "dry-run aan"
    /// terwijl een bevestigde mutatie écht naar Sportlink gaat.
    /// </summary>
    /// <param name="leesInstelling">Instellingenlezer van de tier (<c>GetSetting</c>).</param>
    /// <param name="logger">Optioneel — krijgt een waarschuwing als het lezen zelf faalt.</param>
    public static bool IsDryRunActief(Func<string, string?> leesInstelling, ILogger? logger = null)
    {
        try
        {
            return leesInstelling(InstellingDryRun) != "0";
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Dry-run-instelling '{Instelling}' kon niet gelezen worden — dry-run blijft AAN (fail-safe).",
                InstellingDryRun);
            return true;
        }
    }

    /// <summary>Vertaalt <see cref="SportlinkClubCallStatus"/> naar een foutuitkomst; <c>null</c> bij
    /// <see cref="SportlinkClubCallStatus.Ok"/>.</summary>
    public static SportlinkEndpointFout? VertaalStatusNaarFout(SportlinkClubCallStatus status) => status switch
    {
        SportlinkClubCallStatus.Ok => null,
        SportlinkClubCallStatus.RolNietGekoppeld => new SportlinkEndpointFout(409,
            $"Geen Sportlink-koppeling gevonden voor rol '{RolWedstrijdzaken}' — registreer eerst een refresh-token via Instellingen."),
        SportlinkClubCallStatus.HerkoppelingVereist => new SportlinkEndpointFout(409,
            $"De Sportlink-koppeling voor rol '{RolWedstrijdzaken}' is verlopen — registreer een nieuw refresh-token via Instellingen."),
        _ => new SportlinkEndpointFout(502, "Sportlink is momenteel niet bereikbaar."),
    };

    /// <summary>
    /// Audit-<c>resultaat</c> voor een mutatie-uitkomst (#998, uitgebreid #994).
    /// <c>IsForcedDryRun</c> gaat vóór <c>IsDryRun</c>, dat vóór <c>IsSuccess</c>: een code-gelockte,
    /// nog niet live bevestigde mutatie moet in de audit herkenbaar blijven naast een dry-run door
    /// de club-instelling — bij beide is <c>IsSuccess</c> altijd <c>true</c> (gesimuleerd succes).
    /// </summary>
    public static string BepaalAuditResultaat(SportlinkMutationResult r) =>
        r.IsForcedDryRun ? "DryRunLocked" : r.IsDryRun ? "DryRun" : r.IsSuccess ? "Success" : "Failure";

    /// <summary>
    /// De afronding die élke mutatie deelt: transportfout → audit "Failure" + vertaalde fout; lege
    /// respons → audit "Failure" + 502; anders audit met <see cref="BepaalAuditResultaat"/> en de
    /// data. Een inhoudelijke afwijzing door Sportlink is géén HTTP-fout — <c>IsSuccess</c>/
    /// <c>Violations</c> dragen die uitkomst (consistent met AdminApiClient).
    /// </summary>
    public static SportlinkMutatieAfronding<T> BepaalMutatieAfronding<T>(
        SportlinkClubResponse<T> respons,
        Func<T, SportlinkMutationResult> naarMutatieResultaat)
        where T : class
    {
        var fout = VertaalStatusNaarFout(respons.Status);
        if (fout != null)
            return new SportlinkMutatieAfronding<T>("Failure", respons.FoutmeldingVoorLog, fout, null);

        if (respons.Data == null)
            return new SportlinkMutatieAfronding<T>("Failure", "Geen respons-data van Sportlink",
                new SportlinkEndpointFout(502, "Sportlink gaf geen bruikbare respons."), null);

        var resultaat = naarMutatieResultaat(respons.Data);
        var violations = resultaat.Violations is { Count: > 0 } ? string.Join(", ", resultaat.Violations) : null;
        return new SportlinkMutatieAfronding<T>(
            BepaalAuditResultaat(resultaat), violations, null, respons.Data);
    }
}
