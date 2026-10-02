using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Planner.Shared;
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
        /// <summary>#1437: velddeel als <c>FieldSize</c> — "1.0" (heel), "0.5", "0.25" of "0.125"; leeg = heel veld.
        /// Zie <see cref="ClubMatchVelddeel"/> voor de (nog niet live bevestigde) notatie.</summary>
        public string? Velddeel { get; set; }
        /// <summary>#1437: Sportlink-<c>Id</c> van de leeftijdscategorie (bijv. "110"); moet in Sportlinks lijst staan en
        /// wint van de leeftijdscategorie die uit het team volgt. Leeg = afleiden uit het team.</summary>
        public string? AgeClassCode { get; set; }
    }

    /// <summary>Wat de tierbestanden uit de eigen database halen voor <see cref="BouwAanvraagAsync"/> (#1437: gebundeld i.p.v. een lange parameterlijst).</summary>
    public sealed record ClubMatchTierGegevens(
        string TeamNaam, string? Leeftijdscategorie, string? VeldNaam, string? Accommodatie, string? Spelactiviteit);

    /// <summary>Een actief team zoals het formulier-endpoint het uit de database ontvangt.</summary>
    public sealed record ClubMatchFormulierTeamInvoer(string TeamNaam, string? Leeftijdscategorie);

    /// <summary>Een rij uit de speeltijden-tabel (<c>leeftijd</c> = genormaliseerde leeftijdscategorie).</summary>
    public sealed record ClubMatchSpeeltijdInvoer(string Leeftijd, decimal Veldafmeting, int WedstrijdTotaal);

    /// <summary>Per actief team: wat het formulier voorinvult (#1437). <c>Veldafmeting</c> is de velddeel-waarde
    /// ("1.0", "0.5", "0.25", "0.125"), <c>null</c> als de speeltijden hem niet kennen.</summary>
    public sealed record ClubMatchFormulierTeam(
        string TeamNaam, string? Leeftijdscategorie, string? AgeClassCode, int? Duur, string? Veldafmeting);

    public sealed record ClubMatchFormulierAgeClass(string Id, string Description);

    /// <summary>Respons van <c>GET /api/sportlink/club-match/formulier</c>. <c>SportlinkBeschikbaar = false</c>: de
    /// leeftijdscategorielijst kon niet worden opgehaald; de teamgegevens zijn dan wel compleet.</summary>
    public sealed record ClubMatchFormulier(
        IReadOnlyList<ClubMatchFormulierTeam> Teams,
        IReadOnlyList<ClubMatchFormulierAgeClass> AgeClasses,
        bool SportlinkBeschikbaar);

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
        IReadOnlyList<string> Waarschuwingen,
        string? Velddeel);

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
        if (!string.IsNullOrWhiteSpace(dto.Velddeel) && !ClubMatchVelddeel.IsGeldig(dto.Velddeel))
            return new BadRequestObjectResult(new { error = $"Velddeel moet een van {string.Join(", ", ClubMatchVelddeel.Waarden)} zijn." });
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
    /// Zoekt team en veld op in de eigen database (via de tier-eigen delegates) en bundelt wat
    /// <see cref="BouwAanvraagAsync"/> nodig heeft. Een onbekend dropdown-team of veld is een 400;
    /// een vrije teamnaam (#1427) niet. De clubinstelling Spelactiviteit (#1437) komt als delegate mee,
    /// zodat de tierbestanden alleen de databasevraag zelf houden.
    /// </summary>
    public static async Task<(ClubMatchTierGegevens? Gegevens, ClubMatchTeamKoppeling? Team, IActionResult? Fout)> LosGegevensOpAsync(
        OefenwedstrijdAanmakenDto dto,
        Func<string, Task<ClubMatchTeamKoppeling?>> zoekTeam,
        Func<int, Task<string?>> zoekVeldNaam,
        Func<Task<string?>> leesSpelactiviteit,
        string? accommodatie)
    {
        var team = await zoekTeam(dto.TeamNaam!);
        if (team == null && !dto.VrijeTekst)
            return (null, null, new BadRequestObjectResult(new { error = $"Team '{dto.TeamNaam}' is niet bekend als actief clubteam." }));

        string? veldNaam = null;
        if (dto.VeldNummer.HasValue)
        {
            veldNaam = await zoekVeldNaam(dto.VeldNummer.Value);
            if (veldNaam == null)
                return (null, team, new BadRequestObjectResult(new { error = $"Veld {dto.VeldNummer} is niet bekend als actief veld." }));
        }

        var gegevens = new ClubMatchTierGegevens(
            team?.TeamNaam ?? dto.TeamNaam!.Trim(), team?.Leeftijdscategorie, veldNaam, accommodatie, await leesSpelactiviteit());
        return (gegevens, team, null);
    }

    /// <summary>
    /// Het hele <c>GET /api/sportlink/club-match/formulier</c>-endpoint (#1437) achter de tier-poort: de
    /// teams en speeltijden komen via één tier-eigen delegate, de Sportlink-client alleen als toggle en
    /// EgressGuard het toestaan (<paramref name="geefClient"/> geeft anders <c>null</c>).
    /// </summary>
    public static async Task<IActionResult> FormulierAsync(
        Func<Task<(List<ClubMatchFormulierTeamInvoer> Teams, List<ClubMatchSpeeltijdInvoer> Speeltijden)>> leesGegevens,
        Func<ISportlinkClubClient?> geefClient, string rolNaam, ILogger log)
    {
        var (teams, speeltijden) = await leesGegevens();
        return new OkObjectResult(await BouwFormulierAsync(geefClient(), rolNaam, teams, speeltijden, log));
    }

    /// <summary>
    /// Het hele <c>GET /api/sportlink/club-match/picklists</c>-endpoint (diagnostisch, zie de tierbestanden): toggle
    /// en EgressGuard, de client, en de twee Sportlink-picklists. Verhuisd uit beide tierbestanden bij #1437,
    /// waar hij woordelijk identiek stond.
    /// </summary>
    public static async Task<IActionResult> PickListsAsync(
        Func<IActionResult?> controleerToggleEnEgress,
        Func<(ISportlinkClubClient? Client, IActionResult? Fout)> clientOfFout, string rolNaam)
    {
        var toggleFout = controleerToggleEnEgress();
        if (toggleFout != null) return toggleFout;
        var (sportlinkClient, clientFout) = clientOfFout();
        if (clientFout != null) return clientFout;

        var result = await sportlinkClient!.GetClubMatchPickListsAsync(rolNaam);
        var fout = SportlinkEndpointSupportCore.VertaalStatusNaarFout(result.Status);
        if (fout != null) return fout;

        return new OkObjectResult(result.Data ?? new SportlinkClubMatchPickLists(
            Array.Empty<SportlinkPickListItem>(), Array.Empty<SportlinkPickListItem>()));
    }

    /// <summary>Leest alle rijen van een tier-eigen command en vertaalt ze met <paramref name="map"/> (gedeeld door beide tiers: beide readers zijn een <see cref="DbDataReader"/>).</summary>
    public static async Task<List<T>> LeesAlleAsync<T>(DbCommand cmd, Func<DbDataReader, T> map)
    {
        var resultaat = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            resultaat.Add(map(r));
        return resultaat;
    }

    /// <summary>Kolommen: teamnaam, leeftijdscategorie, Sportlink-team-ID (nullable), aantal kandidaat-ID's.</summary>
    public static ClubMatchTeamKoppeling TeamKoppelingRij(DbDataReader r)
        => new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetInt32(3));

    /// <summary>Kolommen: teamnaam, leeftijdscategorie (nullable).</summary>
    public static ClubMatchFormulierTeamInvoer TeamRij(DbDataReader r)
        => new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));

    /// <summary>Kolommen: leeftijd, veldafmeting, wedstrijdtotaal.</summary>
    public static ClubMatchSpeeltijdInvoer SpeeltijdRij(DbDataReader r)
        => new(r.GetString(0), r.GetDecimal(1), r.GetInt32(2));

    /// <summary>
    /// Haalt de vier Sportlink-lijsten op en bouwt daarmee de <c>ClubMatch</c>-aanvraag (#1427). Geen
    /// cache: een oefenwedstrijd aanmaken is zeldzaam genoeg dat vier read-only GETs per keer
    /// geen kostenpost zijn. Een fout is een <see cref="IActionResult"/> (Sportlink-status of 400).
    /// <para>
    /// Sinds #1437 komt het wedstrijdnummer uit de eigen teller: <paramref name="reserveerVolgnummer"/>
    /// (tier-eigen, atomair; <c>null</c> = die dag zit vol) wordt pas aangeroepen als validatie en
    /// bouw geslaagd zijn, zodat een mislukte aanvraag geen nummer verbruikt.
    /// </para>
    /// </summary>
    public static async Task<(SportlinkClubMatchAanvraag? Aanvraag, IActionResult? Fout, IReadOnlyList<string> Waarschuwingen)> BouwAanvraagAsync(
        ISportlinkClubClient client, string rolNaam, OefenwedstrijdAanmakenDto dto, ClubMatchTierGegevens gegevens,
        Func<DateOnly, Task<int?>> reserveerVolgnummer, ILogger log)
    {
        var context = await client.GetClubMatchContextAsync(rolNaam);
        if (context.Status != SportlinkClubCallStatus.Ok || context.Data == null)
        {
            // Alleen de status loggen, nooit de foutmelding-body — die kan Sportlink-details bevatten.
            log.LogWarning("Sportlink-aanmaaklijsten niet beschikbaar (status {Status}).", context.Status);
            return (null, SportlinkEndpointSupportCore.VertaalStatusNaarFout(context.Status)
                ?? new ObjectResult(new { error = "Sportlink-aanmaaklijsten niet beschikbaar." }) { StatusCode = 502 }, Array.Empty<string>());
        }

        var gekozenAgeClass = string.IsNullOrWhiteSpace(dto.AgeClassCode) ? null : dto.AgeClassCode.Trim();
        if (gekozenAgeClass != null && !context.Data.AgeClasses.Any(a => string.Equals(a.Id, gekozenAgeClass, StringComparison.Ordinal)))
            return (null, new BadRequestObjectResult(new { error = $"Leeftijdscategorie '{gekozenAgeClass}' staat niet in de lijst van Sportlink." }), Array.Empty<string>());

        var invoer = new ClubMatchInvoer(
            dto.MatchDateTime!.Value, dto.Duration ?? 90, gegevens.TeamNaam, dto.VrijeTekst, gegevens.Leeftijdscategorie,
            dto.Tegenstander!, gegevens.VeldNaam, BouwOmschrijving(dto.Description, gegevens.TeamNaam, dto.Tegenstander!),
            gegevens.Accommodatie, gegevens.Spelactiviteit,
            string.IsNullOrWhiteSpace(dto.Velddeel) ? ClubMatchVelddeel.Heel : dto.Velddeel,
            gekozenAgeClass);
        var bouw = ClubMatchAanvraagBouwer.Bouw(invoer, context.Data);
        if (bouw.Aanvraag == null)
            return (null, new BadRequestObjectResult(new { error = bouw.Fout }), bouw.Waarschuwingen);

        var datum = bouw.Aanvraag.MatchDate;
        var volgnummer = await reserveerVolgnummer(datum);
        if (volgnummer == null)
            return (null, new BadRequestObjectResult(new { error = ClubMatchWedstrijdNummer.TeVeelOpEenDagMelding(datum) }), bouw.Waarschuwingen);

        return (bouw.Aanvraag with { ExternalMatchId = ClubMatchWedstrijdNummer.Formatteer(datum, volgnummer.Value) }, null, bouw.Waarschuwingen);
    }

    /// <summary>
    /// Bouwt de respons van <c>GET /api/sportlink/club-match/formulier</c> (#1437): per actief team de
    /// voorinvulling (Sportlink-leeftijdscategorie, duur en velddeel uit de speeltijden) plus Sportlinks
    /// leeftijdscategorielijst. Is Sportlink niet bereikbaar (of <paramref name="client"/> <c>null</c>
    /// door toggle/egress), dan komen de teamgegevens zonder lijst terug zodat de pagina blijft werken.
    /// </summary>
    public static async Task<ClubMatchFormulier> BouwFormulierAsync(
        ISportlinkClubClient? client, string rolNaam, IReadOnlyList<ClubMatchFormulierTeamInvoer> teams,
        IReadOnlyList<ClubMatchSpeeltijdInvoer> speeltijden, ILogger log)
    {
        IReadOnlyList<SportlinkClubAgeClass> ageClasses = Array.Empty<SportlinkClubAgeClass>();
        var beschikbaar = false;
        if (client != null)
        {
            var context = await client.GetClubMatchContextAsync(rolNaam);
            if (context.Status == SportlinkClubCallStatus.Ok && context.Data != null)
            {
                ageClasses = context.Data.AgeClasses;
                beschikbaar = true;
            }
            else
            {
                log.LogWarning("Sportlink-aanmaaklijsten niet beschikbaar voor het formulier (status {Status}).", context.Status);
            }
        }

        var rijen = teams.Select(t =>
        {
            var speeltijd = ZoekSpeeltijd(speeltijden, t.Leeftijdscategorie);
            return new ClubMatchFormulierTeam(
                t.TeamNaam, t.Leeftijdscategorie,
                ClubMatchAanvraagBouwer.ZoekAgeClassId(t.Leeftijdscategorie, ageClasses),
                speeltijd?.WedstrijdTotaal,
                ClubMatchVelddeel.VanAfmeting(speeltijd?.Veldafmeting));
        }).ToList();
        var lijst = ageClasses
            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
            .Select(a => new ClubMatchFormulierAgeClass(a.Id!, a.Description ?? a.Id!))
            .ToList();
        return new ClubMatchFormulier(rijen, lijst, beschikbaar);
    }

    /// <summary>De speeltijdenrij van een leeftijdscategorie: de speeltijden-sleutel is de genormaliseerde vorm ("JO10", "MO15", "1-99", "VR").</summary>
    private static ClubMatchSpeeltijdInvoer? ZoekSpeeltijd(IReadOnlyList<ClubMatchSpeeltijdInvoer> speeltijden, string? leeftijdscategorie)
    {
        if (string.IsNullOrWhiteSpace(leeftijdscategorie)) return null;
        var sleutel = LeeftijdNormalisatie.Normaliseer(leeftijdscategorie);
        return speeltijden.FirstOrDefault(s => string.Equals(s.Leeftijd.Trim(), sleutel, StringComparison.OrdinalIgnoreCase))
            ?? speeltijden.FirstOrDefault(s => string.Equals(s.Leeftijd.Trim(), leeftijdscategorie.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Respons na de mutatie: het mutatieresultaat plus wat er naar Sportlink ging.</summary>
    public static OefenwedstrijdAanmaakResultaat Resultaat(
        SportlinkMutationResult r, SportlinkClubMatchAanvraag aanvraag, string? veldNaam, IReadOnlyList<string> waarschuwingen) =>
        new(r.IsSuccess, r.Violations, r.IsDryRun, r.IsForcedDryRun, r.PublicMatchId,
            aanvraag.Description, aanvraag.PublicTeamId, aanvraag.AgeClassCode, aanvraag.FacilityId,
            aanvraag.SubFacilityId, aanvraag.SportIdTag, aanvraag.ExternalMatchId, veldNaam, waarschuwingen, aanvraag.FieldSize);
}
