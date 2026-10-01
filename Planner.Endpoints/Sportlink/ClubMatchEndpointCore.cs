using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Endpoints.Sportlink;

/// <summary>
/// Tier-onafhankelijke kern van <c>POST /api/sportlink/club-match</c> (#997/#1116), verhuisd uit
/// beide <c>SportlinkClubMatchFunction.cs</c>-bestanden bij #1427: invoer-DTO, validatie,
/// omschrijving en het ophalen van Sportlinks aanmaaklijsten; de keuzes zelf maakt
/// <see cref="ClubMatchAanvraagBouwer"/> (Planner.Shared). De tierbestanden houden alleen de
/// databasevraag (team, veldnaam, instelling <c>accommodatie</c>) en de HTTP-aansluiting — zie
/// docs/ARCHITECTUUR-CODEKWALITEIT.md regel 1.
/// </summary>
public static class ClubMatchEndpointCore
{
    internal const int MaxDuurMinuten = 240;

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
        /// geen actief clubteam is; Sportlinks standaardteam levert dan het team-ID.</summary>
        public bool VrijeTekst { get; set; }
    }

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
        string? SubFacilityId,
        string? SportIdTag,
        long? WedstrijdNummer,
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
    /// Omschrijving zoals die naar Sportlink gaat: de eigen tekst van de beheerder, of anders
    /// <c>Oefenwedstrijd [team] - [tegenstander]</c>. Sinds #1427 gaat het veld als
    /// <c>SubFacilityId</c> mee en hoeft het niet meer in de omschrijving.
    /// </summary>
    public static string BouwOmschrijving(string? eigenTekst, string teamNaam, string tegenstander)
        => string.IsNullOrWhiteSpace(eigenTekst) ? $"Oefenwedstrijd {teamNaam} - {tegenstander.Trim()}" : eigenTekst.Trim();

    /// <summary>
    /// Haalt de vier Sportlink-lijsten op en bouwt daarmee de <c>ClubMatch</c>-aanvraag (#1427). Geen
    /// cache: <c>ClubMatchDefaults</c> levert het eerstvolgende wedstrijdnummer en verandert na elke
    /// aanmaak, en een oefenwedstrijd aanmaken is zeldzaam genoeg dat vier read-only GETs per keer
    /// geen kostenpost zijn. Een fout is een <see cref="IActionResult"/> (Sportlink-status of 400).
    /// </summary>
    public static async Task<(SportlinkClubMatchAanvraag? Aanvraag, IActionResult? Fout, IReadOnlyList<string> Waarschuwingen)> BouwAanvraagAsync(
        ISportlinkClubClient client, string rolNaam, OefenwedstrijdAanmakenDto dto, string teamNaam,
        string? leeftijdscategorie, string? veldNaam, string? accommodatie, ILogger log)
    {
        var context = await client.GetClubMatchContextAsync(rolNaam);
        if (context.Status != SportlinkClubCallStatus.Ok || context.Data == null)
        {
            // Alleen de status loggen, nooit de foutmelding-body — die kan Sportlink-details bevatten.
            log.LogWarning("Sportlink-aanmaaklijsten niet beschikbaar (status {Status}).", context.Status);
            return (null, SportlinkEndpointSupportCore.VertaalStatusNaarFout(context.Status)
                ?? new ObjectResult(new { error = "Sportlink-aanmaaklijsten niet beschikbaar." }) { StatusCode = 502 }, Array.Empty<string>());
        }

        var invoer = new ClubMatchInvoer(
            dto.MatchDateTime!.Value, dto.Duration ?? 90, teamNaam, dto.VrijeTekst, leeftijdscategorie,
            dto.Tegenstander!, veldNaam, BouwOmschrijving(dto.Description, teamNaam, dto.Tegenstander!), accommodatie);
        var bouw = ClubMatchAanvraagBouwer.Bouw(invoer, context.Data);
        return bouw.Aanvraag == null
            ? (null, new BadRequestObjectResult(new { error = bouw.Fout }), bouw.Waarschuwingen)
            : (bouw.Aanvraag, null, bouw.Waarschuwingen);
    }

    /// <summary>Respons na de mutatie: het mutatieresultaat plus wat er naar Sportlink ging.</summary>
    public static OefenwedstrijdAanmaakResultaat Resultaat(
        SportlinkMutationResult r, SportlinkClubMatchAanvraag aanvraag, string? veldNaam, IReadOnlyList<string> waarschuwingen) =>
        new(r.IsSuccess, r.Violations, r.IsDryRun, r.IsForcedDryRun, r.PublicMatchId,
            aanvraag.Description, aanvraag.PublicTeamId, aanvraag.AgeClassCode, aanvraag.FacilityId,
            aanvraag.SubFacilityId, aanvraag.SportIdTag, aanvraag.ExternalMatchId, veldNaam, waarschuwingen);
}
