using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Mutaties op wedstrijddetails en wijzigingsverzoek-bouwers (#1493) — zelfde klasse, alleen verplaatst. Geen gedragswijziging.</summary>
public partial class SportlinkClubClient
{
    private Task<SportlinkClubResponse<SportlinkMutationResult>> PutDressingRoomsAsync(
        string publicMatchId, string? homeDressingRoomId, string? awayDressingRoomId, string? officialDressingRoomId,
        string token, CancellationToken cancellationToken)
    {
        var body = new
        {
            PublicMatchId = publicMatchId,
            HomeDressingRoomId = homeDressingRoomId,
            AwayDressingRoomId = awayDressingRoomId,
            OfficialDressingRoomId = officialDressingRoomId
        };
        return PutMutationAsync(
            UpdateMatchDressingRoomsEndpoint, "competition/match/UpdateMatchDressingRooms", body, token, cancellationToken);
    }

    /// <summary>
    /// Bouwt de volledige <c>UpdateMatchDetails</c>-envelope door <paramref name="snapshot"/> (het
    /// net opgehaalde, huidige wedstrijdrecord) terug te sturen met alleen het veld/velddeel
    /// overschreven — exact het patroon dat Sportlinks eigen UI gebruikt (live vastgesteld,
    /// #1047). Elk ander veld in <c>MatchData</c> komt dus altijd van de server zelf, nooit van
    /// een aanname hier — voorkomt dat een verouderd of onvolledig lokaal model een echt
    /// wedstrijdveld (teamnaam, uitslag, ...) zou overschrijven.
    /// </summary>
    private Task<SportlinkClubResponse<SportlinkMutationResult>> PutMatchDetailsAsync(
        string publicMatchId, string publicApplicantId, SportlinkMatchDetailsSnapshot snapshot,
        string? fieldId, string? fieldSize, int? fieldOffset, bool isForceUpdate,
        string token, CancellationToken cancellationToken)
    {
        var matchData = new MatchDataBody(
            AgeClassCode: snapshot.AgeClassCode,
            AssemblyTime: snapshot.MatchDetails?.MatchDetailsHome?.AssemblyTime?.Value,
            AwayScore: snapshot.Result?.AwayScore,
            AwayTeam: snapshot.Teams?.Away?.TeamName,
            DepartureTime: null,
            Description: snapshot.MatchDetails?.Description?.Value,
            Drivers: "",
            Duration: snapshot.Duration,
            ExternalMatchId: snapshot.ExternalMatchId,
            FacilityId: snapshot.MatchField?.FacilityId,
            FieldId: fieldId ?? snapshot.Field?.FieldId,
            FieldOffset: fieldOffset ?? snapshot.Field?.FieldOffset,
            FieldSize: fieldSize ?? snapshot.Field?.FieldSize,
            HomeScore: snapshot.Result?.HomeScore,
            HomeTeam: snapshot.Teams?.Home?.TeamName,
            MatchChangeRequestRemarks: "",
            MatchDate: snapshot.MatchDate?.Date,
            MatchStatus: snapshot.MatchStatus,
            PublicAwayTeamId: snapshot.Teams?.Away?.PublicTeamId,
            PublicHomeTeamId: snapshot.Teams?.Home?.PublicTeamId,
            SportIdTag: snapshot.Sport?.IdTag,
            StartTime: snapshot.MatchDate?.StartTime);

        var body = new UpdateMatchDetailsBody(
            ConfirmationNeeded: null,
            IsForceUpdate: isForceUpdate,
            IsMatchChangeRequestMandatory: false,
            IsOwnFacility: true,
            IsPlannableByClub: false,
            IsSuccess: false,
            PublicApplicantId: publicApplicantId,
            PublicMatchId: publicMatchId,
            MatchData: matchData);

        return PutMutationAsync(
            UpdateMatchDetailsEndpoint, "competition/match/UpdateMatchDetails", body, token, cancellationToken);
    }

    // #995, Aanpak-stap 1: de eigenaar heeft de body van beide PUT's en de bevestigingsvlag
    // live vastgelegd (#1319) — dit blijft niettemin uitsluitend stap 1 (valideren); stap 2
    // (bevestigen) is een bewuste, aparte scope-beslissing en wordt hier niet gebouwd.
    /// <summary>
    /// Bouwt en verstuurt de <c>UpdateMatchDetails</c>-envelope voor een datum/tijd/accommodatie-
    /// wijzigingsverzoek (#995) — bewust GEEN parametrisering van <see cref="PutMatchDetailsAsync"/>
    /// (#993's live-bevestigde veld-wijzigingspad), zie de doc-comment op
    /// <see cref="RequestMatchChangeAsync"/>. Sinds #1319 volgt deze aanroep de gewone
    /// club-instelling <c>sportlinkDryRun</c> (zie <see cref="UpdateMatchDetailsChangeRequestLiveBevestigd"/>):
    /// bij een echte PUT loopt de respons door <see cref="ParseMatchChangeValidatie"/> via de
    /// <c>verrijkResultaat</c>-hook; bij een gesimuleerde (dry-run) aanroep gebeurt dat niet, want
    /// die hook loopt pas ná een echte HTTP-respons.
    /// </summary>
    private async Task<SportlinkClubResponse<SportlinkMatchChangeRequestResult>> PutMatchDetailsChangeRequestAsync(
        string publicMatchId, SportlinkMatchDetailsSnapshot snapshot,
        DateOnly? nieuweDatum, TimeOnly? nieuweStartTijd, string? nieuweFacilityId, string toelichting,
        string token, CancellationToken cancellationToken)
    {
        var body = BuildMatchChangeRequestBody(publicMatchId, snapshot, nieuweDatum, nieuweStartTijd, nieuweFacilityId, toelichting);

        SportlinkMatchChangeValidatie? validatie = null;
        var mutationResponse = await PutMutationAsync(
            UpdateMatchDetailsEndpoint, "competition/match/UpdateMatchDetails", body, token, cancellationToken,
            new MutatieOpties(
                ForceDryRun: !UpdateMatchDetailsChangeRequestLiveBevestigd,
                VerrijkResultaat: (json, result) =>
                {
                    validatie = ParseMatchChangeValidatie(json);
                    return result;
                }));

        if (mutationResponse.Status != SportlinkClubCallStatus.Ok || mutationResponse.Data == null)
            return new SportlinkClubResponse<SportlinkMatchChangeRequestResult>(
                mutationResponse.Status, null, mutationResponse.FoutmeldingVoorLog, mutationResponse.HttpStatusCode);

        return new SportlinkClubResponse<SportlinkMatchChangeRequestResult>(
            SportlinkClubCallStatus.Ok,
            new SportlinkMatchChangeRequestResult(mutationResponse.Data, validatie),
            null,
            mutationResponse.HttpStatusCode);
    }

    /// <summary>
    /// Bouwt de <c>UpdateMatchDetails</c>-envelope voor #995 — zelfde patroon als
    /// <see cref="PutMatchDetailsAsync"/> (snapshot terugsturen met alléén het gewijzigde deel
    /// overschreven), maar met <c>MatchDate</c>/<c>StartTime</c>/<c>FacilityId</c>/
    /// <c>MatchChangeRequestRemarks</c> als overschrijfbare velden in plaats van het veld. Hergebruikt
    /// bewust de bestaande <see cref="MatchDataBody"/>/<see cref="UpdateMatchDetailsBody"/>-records
    /// (pure databehouders, geen logica) — alleen de BOUW-methode is een eigen kopie.
    /// <para>
    /// <b>ONBEVESTIGD (#995):</b> <c>IsMatchChangeRequestMandatory: true</c> en een lege
    /// <c>PublicApplicantId</c> zijn aannames — #993's veld-wijziging gebruikt <c>false</c> resp. een
    /// live-bevestigde lege string voor een EIGEN-veld-wijziging, geen wijzigingsverzoek aan een
    /// tegenstander. Beide zijn nooit met een netwerktrace gezien voor dit specifieke pad.
    /// </para>
    /// </summary>
    internal static object BuildMatchChangeRequestBody(
        string publicMatchId, SportlinkMatchDetailsSnapshot snapshot,
        DateOnly? nieuweDatum, TimeOnly? nieuweStartTijd, string? nieuweFacilityId, string toelichting)
    {
        var matchData = new MatchDataBody(
            AgeClassCode: snapshot.AgeClassCode,
            AssemblyTime: snapshot.MatchDetails?.MatchDetailsHome?.AssemblyTime?.Value,
            AwayScore: snapshot.Result?.AwayScore,
            AwayTeam: snapshot.Teams?.Away?.TeamName,
            DepartureTime: null,
            Description: snapshot.MatchDetails?.Description?.Value,
            Drivers: "",
            Duration: snapshot.Duration,
            ExternalMatchId: snapshot.ExternalMatchId,
            FacilityId: nieuweFacilityId ?? snapshot.MatchField?.FacilityId,
            FieldId: snapshot.Field?.FieldId,
            FieldOffset: snapshot.Field?.FieldOffset,
            FieldSize: snapshot.Field?.FieldSize,
            HomeScore: snapshot.Result?.HomeScore,
            HomeTeam: snapshot.Teams?.Home?.TeamName,
            MatchChangeRequestRemarks: toelichting,
            MatchDate: nieuweDatum?.ToString("yyyy-MM-dd") ?? snapshot.MatchDate?.Date,
            MatchStatus: snapshot.MatchStatus,
            PublicAwayTeamId: snapshot.Teams?.Away?.PublicTeamId,
            PublicHomeTeamId: snapshot.Teams?.Home?.PublicTeamId,
            SportIdTag: snapshot.Sport?.IdTag,
            StartTime: nieuweStartTijd?.ToString("HH:mm:ss") ?? snapshot.MatchDate?.StartTime);

        return new UpdateMatchDetailsBody(
            ConfirmationNeeded: null,
            IsForceUpdate: false,
            IsMatchChangeRequestMandatory: true,
            IsOwnFacility: true,
            IsPlannableByClub: false,
            IsSuccess: false,
            PublicApplicantId: "",
            PublicMatchId: publicMatchId,
            MatchData: matchData);
    }

    /// <summary>
    /// Parseert Sportlinks <c>ConfirmationNeeded</c>-veld uit een <c>UpdateMatchDetails</c>-respons
    /// (#995) naar <see cref="SportlinkMatchChangeValidatie"/>. <b>ONBEVESTIGD:</b> de exacte vorm is
    /// nooit met een netwerktrace gezien — <c>ValidationResultMessages</c>-elementen kunnen kale
    /// strings zijn óf objecten met een <c>Message</c>- of <c>Description</c>-veld (issue #995:
    /// "elementvorm onbekend"), en <c>HasBlockingMessages</c> kan zowel genest onder
    /// <c>ConfirmationNeeded</c> als op het toplevel staan — deze parser probeert beide, zelfde
    /// defensieve stijl als <see cref="VerrijkOfficialsResultaat"/>/<see cref="UnwrapArrayEnvelope"/>.
    /// Geeft <c>null</c> terug bij een onherkenbare JSON-vorm — nooit een gok naar de aanroeper.
    /// </summary>
    internal static SportlinkMatchChangeValidatie? ParseMatchChangeValidatie(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            // #1320: vier toplevel booleans, gezien bij de oefenwedstrijd-trace van 2026-09-26 —
            // GEEN bewijs voor de verplichte-wijzigingsverzoek-vorm, zie de doc-comment op
            // SportlinkMatchChangeValidatie. Ontbrekend of geen boolean -> null, nooit gokken.
            bool? LeesToplevelBool(string naam) =>
                doc.RootElement.TryGetProperty(naam, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? el.GetBoolean()
                    : null;

            var isSuccess = LeesToplevelBool("IsSuccess");
            var isMatchChangeRequestMandatory = LeesToplevelBool("IsMatchChangeRequestMandatory");
            var isOwnFacility = LeesToplevelBool("IsOwnFacility");
            var isForceUpdate = LeesToplevelBool("IsForceUpdate");

            if (!doc.RootElement.TryGetProperty("ConfirmationNeeded", out var confirmationElement) ||
                confirmationElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new SportlinkMatchChangeValidatie(
                    false, Array.Empty<string>(), false,
                    isSuccess, isMatchChangeRequestMandatory, isOwnFacility, isForceUpdate);

            var meldingen = LeesValidatieMeldingen(confirmationElement);

            // Positie van HasBlockingMessages niet bevestigd — genest onder ConfirmationNeeded en
            // toplevel allebei proberen, genest weegt zwaarder (issue-tekst noemt ze als één geheel).
            var hasBlocking =
                (confirmationElement.ValueKind == JsonValueKind.Object &&
                 confirmationElement.TryGetProperty("HasBlockingMessages", out var nestedBlocking) &&
                 nestedBlocking.ValueKind == JsonValueKind.True)
                || (doc.RootElement.TryGetProperty("HasBlockingMessages", out var rootBlocking) &&
                    rootBlocking.ValueKind == JsonValueKind.True);

            return new SportlinkMatchChangeValidatie(
                true, meldingen, hasBlocking,
                isSuccess, isMatchChangeRequestMandatory, isOwnFacility, isForceUpdate);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Leest <c>ValidationResultMessages</c>: kale strings of objecten met <c>Message</c>/<c>Description</c>.</summary>
    private static List<string> LeesValidatieMeldingen(JsonElement confirmationElement)
    {
        var meldingen = new List<string>();
        if (confirmationElement.ValueKind != JsonValueKind.Object ||
            !confirmationElement.TryGetProperty("ValidationResultMessages", out var messagesElement) ||
            messagesElement.ValueKind != JsonValueKind.Array)
            return meldingen;

        foreach (var item in messagesElement.EnumerateArray())
        {
            string? tekst = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString(),
                JsonValueKind.Object when item.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String => d.GetString(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(tekst))
                meldingen.Add(tekst);
        }
        return meldingen;
    }

    private async Task<SportlinkClubResponse<SportlinkMatchDetailsSnapshot>> FetchMatchDetailsSnapshotAsync(
        string publicMatchId, string token, CancellationToken cancellationToken)
    {
        try
        {
            var response = await GetMatchEndpointResponseAsync(publicMatchId, token, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(
                    SportlinkClubCallStatus.SportlinkFout, null, "Unauthorized bij match endpoint", 401);

            if (!response.IsSuccessStatusCode)
                return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"Match endpoint gaf {response.StatusCode}", (int)response.StatusCode);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var snapshot = JsonSerializer.Deserialize<SportlinkMatchDetailsSnapshot>(json, JsonOptions);
            if (snapshot == null)
                return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(
                    SportlinkClubCallStatus.SportlinkFout, null, "Match data onvolledig in respons", (int)response.StatusCode);

            return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(SportlinkClubCallStatus.Ok, snapshot, null, (int)response.StatusCode);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialisatie fout voor match-details-snapshot");
            return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(SportlinkClubCallStatus.SportlinkFout, null, "JSON deserialisatie fout", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onverwachte fout bij match-details-snapshot");
            return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(SportlinkClubCallStatus.NetwerkFout, null, "Netwerk fout bij match-details-snapshot", null);
        }
    }

    // ── Rauwe modellen voor UpdateMatchDetails — uitsluitend intern, nooit publiek. Elk veld hier
    // is live bevestigd aanwezig in Sportlinks eigen Match-GET-respons (2026-09-06, #1047),
    // op naam gecontroleerd zonder ooit persoonsgegevens (MatchOfficials) te loggen.
    // Zichtbaarheid `internal` (niet `private`, sinds #995) zodat Planner.Shared.Tests een snapshot
    // rechtstreeks kan opbouwen voor een body-test op BuildMatchChangeRequestBody — dezelfde reden
    // als BuildMatchOfficialsBody hierboven al `internal` is. ──

    internal sealed record SportlinkMatchDetailsSnapshot(
        string? AgeClassCode,
        int? Duration,
        string? MatchStatus,
        [property: JsonConverter(typeof(FlexibleLongJsonConverter))] long? ExternalMatchId,
        SportlinkMatchDateRaw? MatchDate,
        SportlinkFieldRaw? Field,
        SportlinkMatchField? MatchField,
        SportlinkSportRaw? Sport,
        SportlinkTeamsRaw? Teams,
        SportlinkResultRaw? Result,
        SportlinkMatchDetailsFieldsRaw? MatchDetails);

    internal sealed record SportlinkMatchDateRaw(string? Date, string? StartTime);
    // Live vastgesteld (2026-09-06, #1047-vervolg): Field.FieldSize komt in de Match-GET-respons
    // als JSON-getal terug, terwijl UpdateMatchDetails' eigen MatchData.FieldSize als string
    // verwacht wordt (zie de netwerktrace) — zelfde wisselvallige-veldtype-patroon als #1036.
    internal sealed record SportlinkFieldRaw(
        string? FieldId,
        [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? FieldSize,
        int? FieldOffset);
    internal sealed record SportlinkSportRaw(string? IdTag);
    internal sealed record SportlinkTeamRaw(string? TeamName, string? PublicTeamId);
    internal sealed record SportlinkTeamsRaw(SportlinkTeamRaw? Home, SportlinkTeamRaw? Away);
    internal sealed record SportlinkResultRaw(int? HomeScore, int? AwayScore);
    internal sealed record SportlinkEditableFieldRaw(string? Value);
    internal sealed record SportlinkMatchDetailsHomeRaw(SportlinkEditableFieldRaw? AssemblyTime);
    internal sealed record SportlinkMatchDetailsFieldsRaw(
        SportlinkEditableFieldRaw? Description, SportlinkMatchDetailsHomeRaw? MatchDetailsHome);

    // ── Uitgaande envelope voor UpdateMatchDetails (PascalCase = de wire-vorm, geen
    // JsonPropertyName nodig — zelfde patroon als de bestaande PutDressingRoomsAsync/-body's). ──

    private sealed record UpdateMatchDetailsBody(
        object? ConfirmationNeeded,
        bool IsForceUpdate,
        bool IsMatchChangeRequestMandatory,
        bool IsOwnFacility,
        bool IsPlannableByClub,
        bool IsSuccess,
        string PublicApplicantId,
        string PublicMatchId,
        MatchDataBody MatchData);

    private sealed record MatchDataBody(
        string? AgeClassCode,
        string? AssemblyTime,
        int? AwayScore,
        string? AwayTeam,
        string? DepartureTime,
        string? Description,
        string? Drivers,
        int? Duration,
        long? ExternalMatchId,
        string? FacilityId,
        string? FieldId,
        int? FieldOffset,
        string? FieldSize,
        int? HomeScore,
        string? HomeTeam,
        string? MatchChangeRequestRemarks,
        string? MatchDate,
        string? MatchStatus,
        string? PublicAwayTeamId,
        string? PublicHomeTeamId,
        string? SportIdTag,
        string? StartTime);
}
