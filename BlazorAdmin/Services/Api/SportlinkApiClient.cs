using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>Sportlink-endpoints: Web Extension (rol-koppeling, autologin, health), wijzigingsverzoeken en oefenwedstrijden (#988-#998, #1427, #1437, #1440). HTTP, status- en foutafhandeling zit in <see cref="AdminApiTransport"/> (#1630).</summary>
public sealed class SportlinkApiClient
{
    private readonly AdminApiTransport _t;

    public SportlinkApiClient(AdminApiTransport transport) => _t = transport;

    // ── Sportlink Web Extension — rol↔serviceaccount-koppelingsstatus (#988) ──

    public async Task<ApiResult<List<SportlinkExtensieRolDto>>> GetSportlinkExtensieRollenAsync()
        => await _t.GetAsync<List<SportlinkExtensieRolDto>>("api/beheer/sportlink-extensie/rollen");

    public async Task<ApiResult<object>> RegistreerSportlinkKoppelingAsync(string rolNaam, string? sportlinkAccountNaam)
        => await _t.PutAsync<object>($"api/beheer/sportlink-extensie/rollen/{Uri.EscapeDataString(rolNaam)}",
            new { SportlinkAccountNaam = sportlinkAccountNaam });

    // #990: write-only automatische login-instellingen; GET retourneert uitsluitend statusmetadata.
    public async Task<ApiResult<SportlinkAutoLoginStatusDto>> GetSportlinkAutoLoginStatusAsync(string rolNaam)
        => await _t.GetAsync<SportlinkAutoLoginStatusDto>(
            $"api/beheer/sportlink-extensie/rollen/{Uri.EscapeDataString(rolNaam)}/autologin");

    public async Task<ApiResult<SportlinkAutoLoginStatusDto>> SetSportlinkAutoLoginAsync(
        string rolNaam, SportlinkAutoLoginRequestDto request)
        => await _t.PutAsync<SportlinkAutoLoginStatusDto>(
            $"api/beheer/sportlink-extensie/rollen/{Uri.EscapeDataString(rolNaam)}/autologin", request);

    public async Task<ApiResult<SportlinkAutoLoginStatusDto>> DeleteSportlinkAutoLoginAsync(string rolNaam)
        => await _t.DeleteAsync<SportlinkAutoLoginStatusDto>(
            $"api/beheer/sportlink-extensie/rollen/{Uri.EscapeDataString(rolNaam)}/autologin");

    // #991: read-only Sportlink-paneel per wedstrijd in Dagplanning.
    public async Task<ApiResult<SportlinkMatchInfoDto>> GetSportlinkMatchInfoAsync(string wedstrijdcode)
        => await _t.GetAsync<SportlinkMatchInfoDto>($"api/sportlink/match/{Uri.EscapeDataString(wedstrijdcode)}");

    // #989: lichtgewicht variant voor de deep-link-knop — alleen PublicMatchId, geen volledige Match-aanroep.
    public async Task<ApiResult<SportlinkPublicMatchIdDto>> GetSportlinkPublicMatchIdAsync(string wedstrijdcode)
        => await _t.GetAsync<SportlinkPublicMatchIdDto>($"api/sportlink/match/{Uri.EscapeDataString(wedstrijdcode)}/public-match-id");

    // #992: eerste echte Sportlink-mutatie — kleedkamers toewijzen.
    public async Task<ApiResult<SportlinkMutatieResultaatDto>> PutSportlinkDressingRoomsAsync(
        string wedstrijdcode, string? homeDressingRoomId, string? awayDressingRoomId, string? officialDressingRoomId)
        => await _t.PutAsync<SportlinkMutatieResultaatDto>(
            $"api/sportlink/match/{Uri.EscapeDataString(wedstrijdcode)}/dressingrooms",
            new { HomeDressingRoomId = homeDressingRoomId, AwayDressingRoomId = awayDressingRoomId, OfficialDressingRoomId = officialDressingRoomId });

    // #993: veld(deel) wijzigen. IsForceUpdate wordt server-side altijd op false gehouden
    // (semantiek niet bevestigd) — hier dus bewust niet als parameter.
    public async Task<ApiResult<SportlinkMutatieResultaatDto>> PutSportlinkFieldAsync(
        string wedstrijdcode, string? fieldId, string? fieldSize, int? fieldOffset)
        => await _t.PutAsync<SportlinkMutatieResultaatDto>(
            $"api/sportlink/match/{Uri.EscapeDataString(wedstrijdcode)}/field",
            new { FieldId = fieldId, FieldSize = fieldSize, FieldOffset = fieldOffset });

    // #994: officials (scheidsrechter/assistenten) toewijzen. ONBEVESTIGD/altijd code-gelockt
    // (forceDryRun) — zie SportlinkClubClient.AssignOfficialsAsync. positie+persoonId per regel,
    // nooit een naam of zoekfunctie (AVG).
    public async Task<ApiResult<SportlinkMutatieResultaatDto>> PutSportlinkOfficialsAsync(
        string wedstrijdcode, IReadOnlyList<(string OfficialPosition, string PersoonId)> officials)
        => await _t.PutAsync<SportlinkMutatieResultaatDto>(
            $"api/sportlink/match/{Uri.EscapeDataString(wedstrijdcode)}/officials",
            new
            {
                Officials = officials
                    .Select(o => new { OfficialPosition = o.OfficialPosition, PersoonId = o.PersoonId })
                    .ToList()
            });

    // #995: wijzigingsverzoek datum/tijd/accommodatie — ALLEEN stap 1 (valideren). ONBEVESTIGD en
    // altijd code-gelockt (forceDryRun) — zie SportlinkClubClient.RequestMatchChangeAsync. Bewust
    // GEEN methode voor een bevestigstap: die bestaat hier niet.
    // NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995).
    // #1320: retourneert sinds de productieproef-trace een SportlinkMatchWijzigingsverzoekTraceDto
    // (Mutatie+Validatie plus HTTP-status/endpoint/AuditId) in plaats van kaal het resultaat.
    public async Task<ApiResult<SportlinkMatchWijzigingsverzoekTraceDto>> PutSportlinkMatchChangeRequestAsync(
        string wedstrijdcode, string? nieuweDatum, string? nieuweStartTijd, string? nieuweFacilityId, string toelichting)
        => await _t.PutAsync<SportlinkMatchWijzigingsverzoekTraceDto>(
            $"api/sportlink/match/{Uri.EscapeDataString(wedstrijdcode)}/change-request",
            new { NieuweDatum = nieuweDatum, NieuweStartTijd = nieuweStartTijd, NieuweFacilityId = nieuweFacilityId, Toelichting = toelichting });

    // #1320: testnotitie koppelen aan de trace van een #995-stap-1-poging — na een live-testpoging
    // kan de eigenaar vastleggen wat er in Sportlink Club zelf zichtbaar was.
    public async Task<ApiResult<object>> PutSportlinkAuditNotitieAsync(long auditId, string notitie)
        => await _t.PutAsync<object>($"api/sportlink/audit/{auditId}/notitie", new { Notitie = notitie });

    // #996: inkomende wijzigingsverzoeken van tegenstanders.
    public async Task<ApiResult<List<SportlinkChangeRequestDto>>> GetSportlinkChangeRequestsAsync()
        => await _t.GetAsync<List<SportlinkChangeRequestDto>>("api/sportlink/change-requests");

    public async Task<ApiResult<SportlinkMutatieResultaatDto>> PutSportlinkChangeRequestActionAsync(
        string publicRequestId, string publicMatchId, string actie, string? remarks)
        => await _t.PutAsync<SportlinkMutatieResultaatDto>(
            $"api/sportlink/change-requests/{Uri.EscapeDataString(publicRequestId)}/action",
            new { PublicMatchId = publicMatchId, Actie = actie, Remarks = remarks });

    // #998: statussectie op Instellingen. live=true doet een echte tokenverversing + 1 read-call —
    // alleen op expliciete gebruikersklik, nooit automatisch (zie SportlinkExtensieHealthFunction).
    public async Task<ApiResult<SportlinkExtensieHealthDto>> GetSportlinkExtensieHealthAsync(bool live = false)
        => await _t.GetAsync<SportlinkExtensieHealthDto>($"api/beheer/sportlink-extensie/health?live={(live ? "true" : "false")}");

    // #997/#1116: oefenwedstrijd ("clubwedstrijd") aanmaken — volgt sinds #1319 de dry-run-instelling
    // van de club, zie SportlinkClubClient.CreateClubMatchAsync. Het formulier stuurt alleen
    // teamnaam/tegenstander/veld; de server leidt de Sportlink-ID's af. vrijeTekst (#1427): de
    // teamnaam komt niet uit de dropdown, dus geen controle op een actief clubteam.
    public async Task<ApiResult<OefenwedstrijdResultaatDto>> PostOefenwedstrijdAsync(
        DateTime matchDateTime, int duration, string teamNaam, bool vrijeTekst, string tegenstander, int? veldNummer, string? description,
        string? velddeel = null, string? ageClassCode = null)
        => await _t.PostAsync<OefenwedstrijdResultaatDto>("api/sportlink/club-match",
            new
            {
                MatchDateTime = matchDateTime,
                Duration = duration,
                TeamNaam = teamNaam,
                VrijeTekst = vrijeTekst,
                Tegenstander = tegenstander,
                VeldNummer = veldNummer,
                Description = description,
                Velddeel = velddeel,
                AgeClassCode = string.IsNullOrWhiteSpace(ageClassCode) ? null : ageClassCode
            });

    // #1440/#1458: een zojuist aangemaakte oefenwedstrijd weer verwijderen; live, volgt de
    // club-instelling sportlinkDryRun.
    public async Task<ApiResult<SportlinkMutatieResultaatDto>> DeleteOefenwedstrijdAsync(string publicMatchId)
        => await _t.DeleteAsync<SportlinkMutatieResultaatDto>($"api/sportlink/club-match/{Uri.EscapeDataString(publicMatchId)}");

    // #1437: voorinvulling per team (Sportlink-leeftijdscategorie, duur, velddeel) + Sportlinks
    // leeftijdscategorielijst — achter de Wedstrijdzaken-poort, want de speeltijden-API is admin-only.
    public async Task<ApiResult<OefenwedstrijdFormulierDto>> GetOefenwedstrijdFormulierAsync()
        => await _t.GetAsync<OefenwedstrijdFormulierDto>("api/sportlink/club-match/formulier");

    // #1427: actuele dry-run-stand voor de banner op "Wedstrijd aanmaken" (Wedstrijdzaken-poort,
    // geen Sportlink-aanroep).
    public async Task<ApiResult<SportlinkDryRunStatusDto>> GetSportlinkDryRunStatusAsync()
        => await _t.GetAsync<SportlinkDryRunStatusDto>("api/sportlink/club-match/dryrun-status");
}
