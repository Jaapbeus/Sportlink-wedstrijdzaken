using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;

namespace Planner.Endpoints.Sportlink;

/// <summary>
/// Haalt de blokken van de Sportlink-veldplanner op voor de Planning (#1563) en past ze toe op onze regels —
/// de gedeelde orkestratie van beide tiers (zelfde vorm als <see cref="ClubMatchEndpointCore"/>).
/// <para>
/// <b>Altijd een terugval, nooit een fout.</b> Staat de extensie uit, is uitgaand verkeer niet toegestaan, is Sportlink
/// onbereikbaar of onbekend de accommodatie, dan geeft <see cref="HaalBlokkenAsync"/> <c>null</c> en houdt de
/// Planning de eigen berekening (<see cref="Planner.Shared.VeldbezettingDuur"/>). Een Planning die om een
/// Sportlink-storing leeg blijft is erger dan een Planning met een berekende duur.
/// </para>
/// </summary>
public static class VeldplannerOverlayCore
{
    private static readonly TimeSpan FacilityGeldigheid = TimeSpan.FromHours(6);
    private static readonly TimeSpan BlokkenGeldigheid = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (string FacilityId, DateTime Tot)> FacilityCache = new();
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<SportlinkVeldplannerBlok> Blokken, DateTime Tot)> BlokkenCache = new();

    /// <returns>De blokken, of <c>null</c> als de terugval geldt.</returns>
    public static async Task<IReadOnlyList<SportlinkVeldplannerBlok>?> HaalBlokkenAsync(
        Func<IActionResult?> controleerToggleEnEgress,
        Func<(ISportlinkClubClient? Client, IActionResult? Fout)> clientOfFout,
        string rolNaam, string? accommodatie, DateOnly datum, ILogger log, Func<DateTime>? nu = null)
    {
        if (string.IsNullOrWhiteSpace(accommodatie)) return null;
        var klok = nu ?? (() => DateTime.UtcNow);
        try
        {
            if (controleerToggleEnEgress() != null) return null;
            var (client, clientFout) = clientOfFout();
            if (clientFout != null || client == null) return null;

            var facilityId = await ZoekFacilityIdAsync(client, rolNaam, accommodatie, klok(), log);
            if (facilityId == null) return null;

            var sleutel = $"{facilityId}|{datum:yyyy-MM-dd}";
            if (BlokkenCache.TryGetValue(sleutel, out var gecachet) && gecachet.Tot > klok()) return gecachet.Blokken;

            var result = await client.GetVeldplannerAsync(rolNaam, facilityId, datum);
            if (result.Status != SportlinkClubCallStatus.Ok || result.Data == null)
            {
                log.LogWarning("Veldplanner van Sportlink niet beschikbaar ({Status}); Planning valt terug op berekende duur", result.Status);
                return null;
            }
            BlokkenCache[sleutel] = (result.Data, klok() + BlokkenGeldigheid);
            return result.Data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Veldplanner van Sportlink ophalen mislukt; Planning valt terug op berekende duur");
            return null;
        }
    }

    /// <summary>
    /// De aanroep die beide tiers doen: zelfde toggle-, EgressGuard- en clientregels als elk ander Sportlink-endpoint
    /// (<see cref="SportlinkEndpointSupportCore"/>), met de tier-eigen instellingenlezer en EgressGuard als parameter.
    /// </summary>
    public static Task<IReadOnlyDictionary<int, SportlinkVeldplannerBlok>> KoppelAsync(
        IReadOnlyList<(string Label, string? Starttijd)> eigen, string clubCode, FunctionContext context,
        Func<string, string?> leesInstelling, Func<bool> egressToegestaan, DateOnly datum, ILogger log)
        => KoppelAsync(eigen, clubCode,
            () => SportlinkEndpointSupportCore.ControleerToggleEnEgress(leesInstelling, egressToegestaan),
            () => SportlinkEndpointSupportCore.ClientOfFout(context),
            SportlinkEndpointSupportCore.RolWedstrijdzaken, leesInstelling("accommodatie"), datum, log);

    /// <summary>
    /// Haalt de blokken op en koppelt ze aan onze regels; per index het blok dat de regel overschrijft. Leeg als de
    /// terugval geldt, ook voor de democlub (<c>ALLSTARS</c> staat niet in Sportlink en mag er nooit om vragen).
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, SportlinkVeldplannerBlok>> KoppelAsync(
        IReadOnlyList<(string Label, string? Starttijd)> eigen, string clubCode,
        Func<IActionResult?> controleerToggleEnEgress,
        Func<(ISportlinkClubClient? Client, IActionResult? Fout)> clientOfFout,
        string rolNaam, string? accommodatie, DateOnly datum, ILogger log)
    {
        if (eigen.Count == 0 || clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<int, SportlinkVeldplannerBlok>();
        var blokken = await HaalBlokkenAsync(controleerToggleEnEgress, clientOfFout, rolNaam, accommodatie, datum, log);
        return Koppel(eigen, blokken);
    }

    /// <summary>Sorteersleutel van de Planning: op aanvangstijd, regels zonder tijd achteraan.</summary>
    public static string SorteerSleutel(string? aanvangsTijd) => string.IsNullOrWhiteSpace(aanvangsTijd) ? "99:99" : aanvangsTijd;

    internal static IReadOnlyDictionary<int, SportlinkVeldplannerBlok> Koppel(
        IReadOnlyList<(string Label, string? Starttijd)> eigen, IReadOnlyList<SportlinkVeldplannerBlok>? blokken)
        => blokken == null || blokken.Count == 0
            ? new Dictionary<int, SportlinkVeldplannerBlok>()
            : SportlinkVeldplannerKoppeling.Koppel(eigen, blokken);

    private static async Task<string?> ZoekFacilityIdAsync(
        ISportlinkClubClient client, string rolNaam, string accommodatie, DateTime nu, ILogger log)
    {
        var sleutel = SportlinkVeldplannerKoppeling.Normaliseer(accommodatie);
        if (FacilityCache.TryGetValue(sleutel, out var bekend) && bekend.Tot > nu) return bekend.FacilityId;

        var lijsten = await client.GetClubMatchPickListsAsync(rolNaam);
        var locatie = lijsten.Data?.Locations
            .FirstOrDefault(l => l.Id != null && SportlinkVeldplannerKoppeling.Normaliseer(l.Naam) == sleutel);
        if (locatie?.Id == null)
        {
            log.LogWarning("Accommodatie niet gevonden in de Sportlink-locaties; Planning valt terug op berekende duur");
            return null;
        }
        FacilityCache[sleutel] = (locatie.Id, nu + FacilityGeldigheid);
        return locatie.Id;
    }
}
