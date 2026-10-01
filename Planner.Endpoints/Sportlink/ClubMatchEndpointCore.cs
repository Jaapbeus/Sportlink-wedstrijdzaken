using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Endpoints.Sportlink;

/// <summary>
/// Tier-onafhankelijke kern van <c>POST /api/sportlink/club-match</c> (#997/#1116), verhuisd uit
/// beide <c>SportlinkClubMatchFunction.cs</c>-bestanden bij #1427: invoer-DTO, validatie,
/// omschrijving, de vrije-tekst-koppeling en het opzoeken van de <c>FacilityId</c> in de
/// Sportlink-locatielijst. De tierbestanden houden alleen de databasevraag (teamkoppeling,
/// veldnaam, instelling <c>accommodatie</c>) en de HTTP-aansluiting — zie
/// docs/ARCHITECTUUR-CODEKWALITEIT.md regel 1.
/// </summary>
public static class ClubMatchEndpointCore
{
    internal const int MaxDuurMinuten = 240;

    /// <summary>
    /// De Sportlink-locatiepicklist verandert praktisch nooit (accommodaties van de club). Eén keer
    /// per uur per club ophalen is ruim genoeg en voorkomt twee extra Sportlink-GETs per ingevoerde
    /// oefenwedstrijd. Bewust in-memory: de Consumption-host recyclet toch, en een miss kost alleen
    /// één read-only aanroep.
    /// </summary>
    private static readonly TimeSpan LocatieCacheDuur = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, (DateTime OpgehaaldUtc, IReadOnlyList<SportlinkPickListItem> Locaties)> LocatieCache = new();

    /// <summary>Invoer van het formulier (#1116) — alleen wat een mens snel kan intikken; de Sportlink-ID's leidt de server af.</summary>
    public sealed class OefenwedstrijdAanmakenDto
    {
        public DateTime? MatchDateTime { get; set; }
        public int? Duration { get; set; }
        public string? TeamNaam { get; set; }
        public string? Tegenstander { get; set; }
        public int? VeldNummer { get; set; }
        public string? Description { get; set; }
        /// <summary>#1427: de teamnaam is vrije tekst ("Vrije tekst invoeren…", #1396) — geen 400 als hij
        /// geen actief clubteam is; Sportlink-team-ID en leeftijdscategorie blijven dan leeg.</summary>
        public bool VrijeTekst { get; set; }
    }

    /// <summary>#1427: koppeling zonder Sportlink-gegevens voor een vrije teamnaam; <c>null</c> als de
    /// naam uit de dropdown kwam (dan blijft "niet bekend als actief clubteam" een 400).</summary>
    public static ClubMatchTeamKoppeling? VrijeTekstKoppeling(OefenwedstrijdAanmakenDto dto)
        => dto.VrijeTekst ? new ClubMatchTeamKoppeling(dto.TeamNaam!.Trim(), null, null, 0) : null;

    /// <summary>
    /// Respons van <c>POST /api/sportlink/club-match</c>: het generieke mutatieresultaat plus wat de
    /// server uit teamnaam en instellingen heeft afgeleid, zodat de beheerder ziet wat er
    /// (gesimuleerd) naar Sportlink zou gaan. Spiegelt <c>BlazorAdmin.Models.OefenwedstrijdResultaatDto</c>.
    /// </summary>
    public sealed record OefenwedstrijdAanmaakResultaat(
        bool IsSuccess,
        IReadOnlyList<string>? Violations,
        bool IsDryRun,
        bool IsForcedDryRun,
        string? PublicMatchId,
        string Omschrijving,
        string? SportlinkTeamId,
        string? AgeClassCode,
        string? FacilityId,
        string? VeldNaam,
        IReadOnlyList<string> Waarschuwingen);

    /// <summary>Invoervalidatie — <c>null</c> als de aanvraag bruikbaar is, anders een 400 met de reden.</summary>
    public static IActionResult? Valideer(OefenwedstrijdAanmakenDto? dto)
    {
        if (dto?.MatchDateTime == null)
            return new BadRequestObjectResult(new { error = "MatchDateTime is verplicht." });
        if (string.IsNullOrWhiteSpace(dto.TeamNaam))
            return new BadRequestObjectResult(new { error = "TeamNaam is verplicht." });
        if (string.IsNullOrWhiteSpace(dto.Tegenstander))
            return new BadRequestObjectResult(new { error = "Tegenstander is verplicht." });
        if (dto.Duration is < 1 or > MaxDuurMinuten)
            return new BadRequestObjectResult(new { error = $"Duration moet tussen 1 en {MaxDuurMinuten} minuten liggen." });
        return null;
    }

    /// <summary>
    /// Omschrijving zoals die naar Sportlink gaat: de eigen tekst van de beheerder, of anders een
    /// standaardtekst met team, tegenstander en — zolang <c>FieldId</c> nog niet wordt meegestuurd —
    /// het gekozen veld, zodat dat in Sportlink Club in ieder geval leesbaar is.
    /// </summary>
    public static string BouwOmschrijving(string? eigenTekst, string teamNaam, string tegenstander, string? veldNaam)
    {
        if (!string.IsNullOrWhiteSpace(eigenTekst)) return eigenTekst.Trim();
        var basis = $"Oefenwedstrijd {teamNaam} - {tegenstander.Trim()}";
        return string.IsNullOrWhiteSpace(veldNaam) ? basis : $"{basis} ({veldNaam})";
    }

    /// <summary>
    /// Zoekt de eigen accommodatie (club-instelling <c>accommodatie</c>) op naam in de Sportlink-
    /// locatiepicklist. Eerst exact (hoofdletter- en spatie-ongevoelig); lukt dat niet, dan één
    /// unieke gedeeltelijke match (de ene naam bevat de andere). Meerdere of geen treffers → <c>null</c>:
    /// beter leeg dan de verkeerde locatie.
    /// </summary>
    public static string? ZoekFacilityId(IEnumerable<SportlinkPickListItem> locaties, string? accommodatie)
    {
        if (string.IsNullOrWhiteSpace(accommodatie)) return null;
        var gezocht = accommodatie.Trim();
        var kandidaten = locaties
            .Where(l => !string.IsNullOrWhiteSpace(l.Id) && !string.IsNullOrWhiteSpace(l.Naam))
            .Select(l => (l.Id!, Naam: l.Naam!.Trim()))
            .ToList();

        var exact = kandidaten.Where(k => string.Equals(k.Naam, gezocht, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0].Item1;
        if (exact.Count > 1) return null;

        var gedeeltelijk = kandidaten
            .Where(k => k.Naam.Contains(gezocht, StringComparison.OrdinalIgnoreCase)
                     || gezocht.Contains(k.Naam, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return gedeeltelijk.Count == 1 ? gedeeltelijk[0].Item1 : null;
    }

    public static async Task<string?> BepaalFacilityIdAsync(
        ISportlinkClubClient client, string rolNaam, string clubCode, string? accommodatie, List<string> waarschuwingen, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(accommodatie))
        {
            waarschuwingen.Add("Club-instelling 'accommodatie' is leeg — FacilityId blijft leeg.");
            return null;
        }

        var locaties = await HaalLocatiesAsync(client, rolNaam, clubCode, log);
        if (locaties == null)
        {
            waarschuwingen.Add("Sportlink-locatielijst kon niet worden opgehaald — FacilityId blijft leeg.");
            return null;
        }

        var facilityId = ZoekFacilityId(locaties, accommodatie);
        if (facilityId == null)
            waarschuwingen.Add($"Accommodatie '{accommodatie}' niet (eenduidig) gevonden in de Sportlink-locatielijst — FacilityId blijft leeg.");
        return facilityId;
    }

    private static async Task<IReadOnlyList<SportlinkPickListItem>?> HaalLocatiesAsync(ISportlinkClubClient client, string rolNaam, string clubCode, ILogger log)
    {
        if (LocatieCache.TryGetValue(clubCode, out var cached) && DateTime.UtcNow - cached.OpgehaaldUtc < LocatieCacheDuur)
            return cached.Locaties;

        var result = await client.GetClubMatchPickListsAsync(rolNaam);
        if (result.Status != SportlinkClubCallStatus.Ok || result.Data == null)
        {
            // Alleen de status loggen, nooit de foutmelding-body — die kan Sportlink-details bevatten.
            log.LogWarning("Sportlink-locatiepicklist niet beschikbaar (status {Status}); FacilityId blijft leeg.", result.Status);
            return null;
        }

        LocatieCache[clubCode] = (DateTime.UtcNow, result.Data.Locations);
        return result.Data.Locations;
    }
}
