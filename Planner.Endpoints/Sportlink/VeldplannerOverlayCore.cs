using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;

namespace Planner.Endpoints.Sportlink;

/// <summary>De blokken van de Sportlink-veldplanner voor één dag, of de reden waarom ze er niet zijn (#1774).</summary>
public sealed record VeldplannerOphaalResultaat(IReadOnlyList<SportlinkVeldplannerBlok>? Blokken, SportlinkVeldplannerStatus Status);

/// <summary>
/// Haalt de blokken van de Sportlink-veldplanner op voor de Planning (#1563) en past ze toe op onze regels —
/// de gedeelde orkestratie van beide tiers (zelfde vorm als <see cref="ClubMatchEndpointCore"/>).
/// <para>
/// <b>Een terugval is nooit stil (#1774).</b> Staat de extensie uit, is uitgaand verkeer niet toegestaan, is de
/// koppeling verlopen, is Sportlink onbereikbaar of de accommodatie onbekend, dan geeft <see cref="HaalBlokkenAsync"/>
/// geen blokken maar wél een <see cref="SportlinkVeldplannerStatus"/> met de precieze reden. De Planning houdt dan de
/// eigen berekening (<see cref="Planner.Shared.VeldbezettingDuur"/>), maar het antwoord zegt dat erbij: een Planning die
/// om een Sportlink-storing leeg blijft is erger dan een Planning met een berekende duur, en een Planning die stil de
/// databasekopie als Sportlink presenteert is erger dan beide (WZ-ADR-013). Is Sportlink wél bereikbaar, dan is hij
/// leidend (#1582): <c>SamenvoegAsync</c>.
/// </para>
/// </summary>
public static class VeldplannerOverlayCore
{
    private static readonly TimeSpan FacilityGeldigheid = TimeSpan.FromHours(6);
    private static readonly TimeSpan BlokkenGeldigheid = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (string FacilityId, DateTime Tot)> FacilityCache = new();
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<SportlinkVeldplannerBlok> Blokken, DateTime OpgehaaldOp, DateTime Tot)> BlokkenCache = new();

    /// <returns>De blokken met status <c>Ok</c>, of geen blokken met de terugvalreden.</returns>
    public static async Task<VeldplannerOphaalResultaat> HaalBlokkenAsync(
        Func<SportlinkTimerStatus> toggleEnEgress,
        Func<(ISportlinkClubClient? Client, IActionResult? Fout)> clientOfFout,
        string rolNaam, string? accommodatie, DateOnly datum, ILogger log, Func<DateTime>? nu = null)
    {
        var klok = nu ?? (() => DateTime.UtcNow);
        try
        {
            var toegang = toggleEnEgress();
            if (toegang == SportlinkTimerStatus.ExtensieUit) return Terugval(SportlinkVeldplannerStatus.RedenExtensieUit, log);
            if (toegang != SportlinkTimerStatus.MagDraaien) return Terugval(SportlinkVeldplannerStatus.RedenEgressGeblokkeerd, log);
            if (string.IsNullOrWhiteSpace(accommodatie)) return Terugval(SportlinkVeldplannerStatus.RedenGeenAccommodatie, log);

            var (client, clientFout) = clientOfFout();
            if (clientFout != null || client == null) return Terugval(SportlinkVeldplannerStatus.RedenClientNietGeconfigureerd, log);

            var facility = await ZoekFacilityIdAsync(client, rolNaam, accommodatie, klok(), log);
            if (facility.Status != null) return new VeldplannerOphaalResultaat(null, facility.Status);

            var sleutel = $"{facility.FacilityId}|{datum:yyyy-MM-dd}";
            if (BlokkenCache.TryGetValue(sleutel, out var gecachet) && gecachet.Tot > klok())
                return new VeldplannerOphaalResultaat(gecachet.Blokken, SportlinkVeldplannerStatus.Ok(gecachet.OpgehaaldOp));

            var result = await client.GetVeldplannerAsync(rolNaam, facility.FacilityId!, datum);
            if (result.Status != SportlinkClubCallStatus.Ok || result.Data == null)
            {
                var status = SportlinkVeldplannerStatus.VanAanroepStatus(result.Status);
                log.LogWarning("Veldplanner van Sportlink niet beschikbaar ({Status}); Planning valt terug op de databasekopie ({Reden})", result.Status, status.Reden);
                return new VeldplannerOphaalResultaat(null, status);
            }
            var opgehaaldOp = klok();
            BlokkenCache[sleutel] = (result.Data, opgehaaldOp, opgehaaldOp + BlokkenGeldigheid);
            return new VeldplannerOphaalResultaat(result.Data, SportlinkVeldplannerStatus.Ok(opgehaaldOp));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Veldplanner van Sportlink ophalen mislukt; Planning valt terug op de databasekopie ({Reden})", SportlinkVeldplannerStatus.RedenOnverwacht);
            return new VeldplannerOphaalResultaat(null, SportlinkVeldplannerStatus.Terugval(SportlinkVeldplannerStatus.RedenOnverwacht));
        }
    }

    private static VeldplannerOphaalResultaat Terugval(string reden, ILogger log)
    {
        var status = SportlinkVeldplannerStatus.Terugval(reden);
        // Een terugval zonder Sportlink-aanroep is een configuratietoestand, geen storing: informatie, geen waarschuwing.
        log.LogInformation("Veldplanner van Sportlink niet geraadpleegd ({Reden}); Planning toont de databasekopie", reden);
        return new VeldplannerOphaalResultaat(null, status);
    }

    /// <summary>
    /// De aanroep die beide tiers doen: zelfde toggle-, EgressGuard- en clientregels als elk ander Sportlink-endpoint
    /// (<see cref="SportlinkEndpointSupportCore"/>), met de tier-eigen instellingenlezer en EgressGuard als parameter.
    /// </summary>
    public static Task<VeldbezettingAntwoord<T>> SamenvoegAsync<T>(
        IReadOnlyList<T> eigen, string clubCode, FunctionContext context,
        Func<string, string?> leesInstelling, Func<bool> egressToegestaan, DateOnly datum, ILogger log,
        Func<T, (string Label, string? Starttijd)> sleutel, Func<T, SportlinkVeldplannerBlok, T> overschrijf,
        Func<SportlinkVeldplannerBlok, T> nieuw, Func<T, T> nietInSportlink, Func<T, string?> sorteerTijd)
        => SamenvoegAsync(eigen, clubCode,
            () => SportlinkEndpointCore.BepaalTimerStatus(leesInstelling, egressToegestaan),
            () => SportlinkEndpointSupportCore.ClientOfFout(context),
            SportlinkEndpointSupportCore.RolWedstrijdzaken, leesInstelling("accommodatie"), datum, log,
            sleutel, overschrijf, nieuw, nietInSportlink, sorteerTijd);

    /// <summary>
    /// Haalt de Sportlink-veldplanner op en voegt hem samen met onze regels (#1582): Sportlink is leidend, zie
    /// <see cref="SportlinkVeldbezettingSamenvoeging"/>. Zonder bereikbare Sportlink, en voor de democlub
    /// (<c>ALLSTARS</c> staat niet in Sportlink en mag er nooit om vragen), blijven de eigen regels ongewijzigd —
    /// met de reden in de status (#1774). Ook bij nul eigen regels wordt Sportlink gevraagd: de velden kunnen door
    /// anderen bezet zijn.
    /// </summary>
    public static async Task<VeldbezettingAntwoord<T>> SamenvoegAsync<T>(
        IReadOnlyList<T> eigen, string clubCode,
        Func<SportlinkTimerStatus> toggleEnEgress,
        Func<(ISportlinkClubClient? Client, IActionResult? Fout)> clientOfFout,
        string rolNaam, string? accommodatie, DateOnly datum, ILogger log,
        Func<T, (string Label, string? Starttijd)> sleutel, Func<T, SportlinkVeldplannerBlok, T> overschrijf,
        Func<SportlinkVeldplannerBlok, T> nieuw, Func<T, T> nietInSportlink, Func<T, string?> sorteerTijd)
    {
        if (clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase))
            return new VeldbezettingAntwoord<T>(SportlinkVeldplannerStatus.Terugval(SportlinkVeldplannerStatus.RedenDemoclub), eigen);

        var opgehaald = await HaalBlokkenAsync(toggleEnEgress, clientOfFout, rolNaam, accommodatie, datum, log);
        var samengevoegd = SportlinkVeldbezettingSamenvoeging.Voeg(eigen, opgehaald.Blokken, sleutel, overschrijf, nieuw, nietInSportlink, sorteerTijd);
        if (opgehaald.Blokken != null)
        {
            // #1774: meetbaar maken hoe goed de koppeling op teamlabel werkt — een stille mismatch (bijvoorbeeld een
            // teamnaam-suffix) is anders alleen op de pagina te zien.
            log.LogInformation(
                "Veldplanner samengevoegd voor {Datum}: {Gekoppeld} gekoppeld, {NietInSportlink} niet in Sportlink, {AlleenSportlink} alleen in Sportlink",
                datum, samengevoegd.Gekoppeld, samengevoegd.NietInSportlink, samengevoegd.AlleenInSportlink);
        }
        return new VeldbezettingAntwoord<T>(opgehaald.Status, samengevoegd.Regels);
    }

    /// <summary>Sorteersleutel van de Planning: op aanvangstijd, regels zonder tijd achteraan.</summary>
    public static string SorteerSleutel(string? aanvangsTijd) => string.IsNullOrWhiteSpace(aanvangsTijd) ? "99:99" : aanvangsTijd;

    /// <summary>Facility-ID bij succes, anders de terugvalstatus — een mislukte locatielijst is géén onbekende accommodatie (#1774).</summary>
    private static async Task<(string? FacilityId, SportlinkVeldplannerStatus? Status)> ZoekFacilityIdAsync(
        ISportlinkClubClient client, string rolNaam, string accommodatie, DateTime nu, ILogger log)
    {
        var sleutel = SportlinkVeldplannerKoppeling.Normaliseer(accommodatie);
        if (FacilityCache.TryGetValue(sleutel, out var bekend) && bekend.Tot > nu) return (bekend.FacilityId, null);

        var lijsten = await client.GetClubMatchPickListsAsync(rolNaam);
        if (lijsten.Status != SportlinkClubCallStatus.Ok || lijsten.Data == null)
        {
            var status = SportlinkVeldplannerStatus.VanAanroepStatus(lijsten.Status);
            log.LogWarning("Sportlink-locatielijst niet beschikbaar ({Status}); Planning valt terug op de databasekopie ({Reden})", lijsten.Status, status.Reden);
            return (null, status);
        }

        var locatie = lijsten.Data.Locations.FirstOrDefault(l => l.Id != null && SportlinkVeldplannerKoppeling.Normaliseer(l.Naam) == sleutel);
        if (locatie?.Id == null)
        {
            log.LogWarning("Accommodatie niet gevonden in de Sportlink-locaties; Planning valt terug op de databasekopie ({Reden})", SportlinkVeldplannerStatus.RedenAccommodatieOnbekend);
            return (null, SportlinkVeldplannerStatus.Terugval(SportlinkVeldplannerStatus.RedenAccommodatieOnbekend));
        }
        FacilityCache[sleutel] = (locatie.Id, nu + FacilityGeldigheid);
        return (locatie.Id, null);
    }
}
