using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Schrijvende (mutatie-)aanroepen van <see cref="SportlinkClubClient"/> (#1493) — zelfde klasse,
/// alleen verplaatst uit het hoofdbestand. Geen gedragswijziging.
/// </summary>
public partial class SportlinkClubClient
{
    public Task<SportlinkClubResponse<SportlinkMutationResult>> UpdateDressingRoomsAsync(
        string functioneleRol,
        string publicMatchId,
        string? homeDressingRoomId,
        string? awayDressingRoomId,
        string? officialDressingRoomId,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            (token, ct) => PutDressingRoomsAsync(publicMatchId, homeDressingRoomId, awayDressingRoomId, officialDressingRoomId, token, ct),
            cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMutationResult>> UpdateFieldAsync(
        string functioneleRol,
        string publicMatchId,
        string? fieldId,
        string? fieldSize,
        int? fieldOffset,
        bool isForceUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            async (token, ct) =>
            {
                // Live vastgesteld (2026-09-06, #1047): UpdateMatchDetails verwacht het VOLLEDIGE
                // wedstrijdrecord, niet een klein veld-only patch — Sportlinks eigen UI stuurt
                // gewoon het al opgehaalde match-object terug met het gewijzigde veld erin. Daarom
                // hier eerst een verse snapshot ophalen (dezelfde Match-GET, rijker gemodelleerd)
                // in plaats van de aanroeper de volledige payload te laten opgeven.
                var snapshot = await FetchMatchDetailsSnapshotAsync(publicMatchId, token, ct);
                if (snapshot.Status != SportlinkClubCallStatus.Ok || snapshot.Data == null)
                    return new SportlinkClubResponse<SportlinkMutationResult>(
                        snapshot.Status == SportlinkClubCallStatus.Ok ? SportlinkClubCallStatus.SportlinkFout : snapshot.Status,
                        null, snapshot.FoutmeldingVoorLog ?? "Kon wedstrijdgegevens niet ophalen voor veldwijziging", snapshot.HttpStatusCode);

                // Live vastgesteld (2026-09-06, #1048): een lege PublicApplicantId wordt door
                // Sportlink geaccepteerd voor een eigen-veld-wijziging (bevestigd: isSuccess:true,
                // audit-log Success). Dat veld is kennelijk alleen relevant voor #996's change-
                // request-actie (waar een externe partij "aanvraagt"), niet voor een directe
                // wijziging door de club zelf aan haar eigen wedstrijd — dus geen aparte
                // UserInfo-aanroep (en de daar nog openstaande bug, #1048) nodig voor dit pad.
                return await PutMatchDetailsAsync(
                    publicMatchId, "", snapshot.Data,
                    fieldId, fieldSize, fieldOffset, isForceUpdate, token, ct);
            },
            cancellationToken);

    // NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995, Aanpak-stap 1: body van
    // beide PUT's en de bevestigingsvlag vastleggen). Dit is uitsluitend stap 1 (valideren) — er
    // bestaat bewust geen stap 2 (bevestigen) in deze client, geen endpoint en geen UI-knop ervoor.
    /// <summary>
    /// Vraagt een wijziging van datum/tijd/accommodatie aan (#995, epic #986) — stap 1 (valideren)
    /// van Sportlinks tweestaps flow, via hetzelfde endpoint als #993's veld-wijziging:
    /// <c>PUT competition/match/UpdateMatchDetails</c>. Dit is de enige Sportlink-mutatie die een
    /// ECHTE tegenstander raakt. <b>Sinds #1319</b> heeft de eigenaar
    /// <see cref="UpdateMatchDetailsChangeRequestLiveBevestigd"/> op <c>true</c> gezet na een live
    /// netwerktrace — de aanroep volgt vanaf nu de gewone club-instelling <c>sportlinkDryRun</c>,
    /// net als elke andere bevestigde mutatie.
    /// <para>
    /// Bewust GEEN gedeelde refactor van <see cref="PutMatchDetailsAsync"/>: die methode hoort bij
    /// #993's live-bevestigde, werkende productiepad. Deze methode kopieert de structuur (verse
    /// snapshot ophalen → envelope met alléén het gewijzigde veld overschreven) in plaats van de
    /// bestaande methode te parametriseren — code-duplicatie is hier de veiligere keuze.
    /// </para>
    /// <b>De aanroeper controleert VOORAF</b> <c>SportlinkMutationGuard.MagMuteren(match,
    /// SportlinkMutationSoort.DatumTijdAccommodatie)</c>.
    /// </summary>
    /// <param name="functioneleRol">Functionele rol voor token-lookup.</param>
    /// <param name="publicMatchId">Zie de TODO(#987)-waarschuwing op <see cref="GetMatchAsync"/>.</param>
    /// <param name="nieuweDatum">Nieuwe wedstrijddatum, of <c>null</c> om de datum ongewijzigd te laten.</param>
    /// <param name="nieuweStartTijd">Nieuwe starttijd, of <c>null</c> om de tijd ongewijzigd te laten.</param>
    /// <param name="nieuweFacilityId">Nieuwe accommodatie-ID, of <c>null</c> om de accommodatie ongewijzigd te laten.</param>
    /// <param name="toelichting">Verplichte toelichting bij het verzoek — validatie hiervan is aan de aanroeper.</param>
    /// <returns>
    /// Bij <c>Status=Ok</c>: <c>Data.Mutatie.IsDryRun</c> volgt de club-instelling
    /// <c>sportlinkDryRun</c>. Alleen als die simuleert is <c>Data.Validatie</c> <c>null</c> (geen
    /// echte Sportlink-respons om te parsen); anders bevat het de geparste <c>ConfirmationNeeded</c>-
    /// envelope van de echte respons.
    /// </returns>
    public Task<SportlinkClubResponse<SportlinkMatchChangeRequestResult>> RequestMatchChangeAsync(
        string functioneleRol,
        string publicMatchId,
        DateOnly? nieuweDatum,
        TimeOnly? nieuweStartTijd,
        string? nieuweFacilityId,
        string toelichting,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync<SportlinkMatchChangeRequestResult>(
            functioneleRol,
            RetryBeleid.Mutatie,
            async (token, ct) =>
            {
                // Zelfde reden als UpdateFieldAsync hierboven: UpdateMatchDetails verwacht het
                // volledige wedstrijdrecord, dus eerst een verse snapshot ophalen.
                var snapshot = await FetchMatchDetailsSnapshotAsync(publicMatchId, token, ct);
                if (snapshot.Status != SportlinkClubCallStatus.Ok || snapshot.Data == null)
                    return new SportlinkClubResponse<SportlinkMatchChangeRequestResult>(
                        snapshot.Status == SportlinkClubCallStatus.Ok ? SportlinkClubCallStatus.SportlinkFout : snapshot.Status,
                        null, snapshot.FoutmeldingVoorLog ?? "Kon wedstrijdgegevens niet ophalen voor wijzigingsverzoek", snapshot.HttpStatusCode);

                return await PutMatchDetailsChangeRequestAsync(
                    publicMatchId, snapshot.Data, nieuweDatum, nieuweStartTijd, nieuweFacilityId, toelichting, token, ct);
            },
            cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMutationResult>> ActOnChangeRequestAsync(
        string functioneleRol,
        string actie,
        string publicMatchId,
        string publicRequestId,
        string? remarks,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            async (token, ct) =>
            {
                // PublicPersonId van de ingelogde (service-)gebruiker is verplicht in de
                // actie-body — niet apart gecached (naast de token-cache): dit endpoint wordt
                // zelden aangeroepen (een admin keurt af en toe een verzoek goed/af), dus één
                // extra GET per poging weegt niet op tegen nóg een cache-laag.
                var userInfo = await FetchUserInfoAsync(token, ct);
                if (userInfo.Status != SportlinkClubCallStatus.Ok || string.IsNullOrWhiteSpace(userInfo.Data?.PublicPersonId))
                    return new SportlinkClubResponse<SportlinkMutationResult>(
                        userInfo.Status == SportlinkClubCallStatus.Ok ? SportlinkClubCallStatus.SportlinkFout : userInfo.Status,
                        null, userInfo.FoutmeldingVoorLog ?? "Kon PublicPersonId niet ophalen via UserInfo", userInfo.HttpStatusCode);

                return await PutChangeRequestActionAsync(actie, publicMatchId, userInfo.Data.PublicPersonId, publicRequestId, remarks, token, ct);
            },
            cancellationToken);

    private Task<SportlinkClubResponse<SportlinkMutationResult>> PutChangeRequestActionAsync(
        string actie, string publicMatchId, string publicPersonId, string publicRequestId, string? remarks,
        string token, CancellationToken cancellationToken)
    {
        var body = new
        {
            Action = actie,
            PublicMatchId = publicMatchId,
            PublicPersonId = publicPersonId,
            PublicRequestId = publicRequestId,
            Remarks = remarks
        };
        return PutMutationAsync(
            MatchChangeRequestActionEndpoint, "competition/match/changerequest/MatchChangeRequestAction", body, token, cancellationToken);
    }

    /// <summary>
    /// Wijst officials (scheidsrechter/assistenten) toe aan een wedstrijd (#994, epic #986) —
    /// <c>PUT competition/match/official/MatchOfficialsAction</c>. <b>Sinds #1319</b> heeft de
    /// eigenaar <see cref="MatchOfficialsActionLiveBevestigd"/> op <c>true</c> gezet na een live
    /// netwerktrace — de aanroep volgt vanaf nu de gewone club-instelling <c>sportlinkDryRun</c>,
    /// net als elke andere bevestigde mutatie. Zie <see cref="SportlinkOfficialToewijzing"/> voor de
    /// aannames op elementniveau.
    /// </summary>
    public Task<SportlinkClubResponse<SportlinkMutationResult>> AssignOfficialsAsync(
        string functioneleRol,
        string publicMatchId,
        IReadOnlyList<SportlinkOfficialToewijzing> officials,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            (token, ct) => PutMatchOfficialsAsync(publicMatchId, officials, token, ct),
            cancellationToken);

    private Task<SportlinkClubResponse<SportlinkMutationResult>> PutMatchOfficialsAsync(
        string publicMatchId, IReadOnlyList<SportlinkOfficialToewijzing> officials, string token, CancellationToken cancellationToken)
    {
        return PutMutationAsync(
            UpdateMatchOfficialsEndpoint,
            "competition/match/official/MatchOfficialsAction",
            BuildMatchOfficialsBody(publicMatchId, officials),
            token,
            cancellationToken,
            new MutatieOpties(ForceDryRun: !MatchOfficialsActionLiveBevestigd, VerrijkResultaat: VerrijkOfficialsResultaat));
    }

    /// <summary>
    /// Bouwt de <c>MatchOfficialsAction</c>-requestbody — losgetrokken van <see cref="PutMatchOfficialsAsync"/>
    /// zodat de vorm (#994: "OfficialPosition"/"PersoonId" als veldnamen binnen elk element van
    /// <c>OfficialsToBeAssigned</c> — zie <see cref="SportlinkOfficialToewijzing"/>, sinds #1319 live
    /// bevestigd) direct getest kan worden zonder een echte PUT te versturen.
    /// </summary>
    internal static object BuildMatchOfficialsBody(string publicMatchId, IReadOnlyList<SportlinkOfficialToewijzing> officials) =>
        new
        {
            PublicMatchId = publicMatchId,
            OfficialsToBeAssigned = officials
                .Select(o => new { OfficialPosition = o.OfficialPosition, PersoonId = o.PersoonId })
                .ToList()
        };

    /// <summary>
    /// Taakspecifieke uitbreiding op de generieke responsparser (#994): Sportlink toont "opgeslagen
    /// met fouten" als één official een <c>ValidationDescription</c> heeft — ook al is de HTTP-status
    /// 200 en <c>Error</c> niet gezet. De generieke <see cref="PutMutationAsync"/>-parsing (gericht op
    /// het `{Error, ViolationCodes, Violations}`-afwijzingspatroon) ziet dit niet, dus wordt het
    /// resultaat hier ná die generieke parsing alsnog gecorrigeerd. Nooit persoonsgegevens loggen —
    /// deze methode logt niets, geeft alleen de omschrijvingstekst door als violation.
    /// </summary>
    internal static SportlinkMutationResult VerrijkOfficialsResultaat(string json, SportlinkMutationResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Officials", out var officialsElement) ||
                officialsElement.ValueKind != JsonValueKind.Array)
                return result;

            var validatieMeldingen = new List<string>();
            foreach (var official in officialsElement.EnumerateArray())
            {
                if (official.ValueKind == JsonValueKind.Object &&
                    official.TryGetProperty("ValidationDescription", out var validationElement) &&
                    validationElement.ValueKind == JsonValueKind.String)
                {
                    var melding = validationElement.GetString();
                    if (!string.IsNullOrWhiteSpace(melding))
                        validatieMeldingen.Add(melding);
                }
            }

            if (validatieMeldingen.Count == 0)
                return result;

            // Sportlink toont "opgeslagen met fouten" (IS_SAVED_WITH_ERRORS) — dat is inhoudelijk
            // geen succes, ook al was de HTTP-status 200/Error niet gezet.
            return result with { IsSuccess = false, Violations = validatieMeldingen };
        }
        catch (JsonException)
        {
            // Onherkenbare respons-vorm: geen extra fout hierboven op stapelen, laat het generieke
            // resultaat (op basis van HTTP-status/Error) ongewijzigd.
            return result;
        }
    }

    private sealed record SportlinkUserInfo(string? PublicPersonId);

    /// <summary>
    /// Maakt een nieuwe oefenwedstrijd aan (#997) — zie <see cref="ISportlinkClubClient.CreateClubMatchAsync"/>.
    /// </summary>
    public Task<SportlinkClubResponse<SportlinkMutationResult>> CreateClubMatchAsync(
        string functioneleRol,
        SportlinkClubMatchAanvraag aanvraag,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            (token, ct) => PostClubMatchAsync(aanvraag, token, ct),
            cancellationToken);

    private Task<SportlinkClubResponse<SportlinkMutationResult>> PostClubMatchAsync(
        SportlinkClubMatchAanvraag aanvraag, string token, CancellationToken cancellationToken)
    {
        return PutMutationAsync(
            ClubMatchEndpoint,
            "competition/match/clubmatch/ClubMatch",
            BuildClubMatchBody(aanvraag),
            token,
            cancellationToken,
            new MutatieOpties(ForceDryRun: !ClubMatchLiveBevestigd, Method: HttpMethod.Post));
    }

    /// <summary>
    /// Verwijdert een clubwedstrijd (#1440) — zie <see cref="ISportlinkClubClient.DeleteClubMatchAsync"/>.
    /// Sinds #1458 live (<see cref="ClubMatchDeleteLiveBevestigd"/> = <c>true</c>): de aanroep volgt de
    /// club-instelling <c>sportlinkDryRun</c>; bij dry-run verlaat er geen DELETE deze client.
    /// </summary>
    public Task<SportlinkClubResponse<SportlinkMutationResult>> DeleteClubMatchAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            (token, ct) => PutMutationAsync(
                BouwClubMatchDeleteUrl(publicMatchId),
                "competition/match/clubmatch/ClubMatchDelete",
                body: null,
                token,
                ct,
                new MutatieOpties(
                    ForceDryRun: !ClubMatchDeleteLiveBevestigd,
                    Method: HttpMethod.Delete,
                    LegeSuccesBodyIsSucces: true)),
            cancellationToken);

    /// <summary>URL van de verwijderaanroep: <c>PublicMatchId</c> als queryparameter, zoals Sportlinks
    /// eigen frontend hem meestuurt (<c>params:{PublicMatchId}</c> in de bundle, #1440).</summary>
    internal static string BouwClubMatchDeleteUrl(string publicMatchId)
        => ClubMatchDeleteEndpoint + "?PublicMatchId=" + Uri.EscapeDataString(publicMatchId);

    /// <summary>
    /// #1440: een 2xx zonder body is voor een verwijderaanroep een geslaagde mutatie. Sportlinks eigen
    /// frontend leest de succesbody van <c>ClubMatchDelete</c> niet; zonder deze uitzondering zou een
    /// lege respons als "onherkenbare respons" (fout) gelden terwijl de wedstrijd wél weg is.
    /// </summary>
    internal static bool IsLegeSuccesRespons(System.Net.HttpStatusCode status, string body)
        => (int)status is >= 200 and <= 299 && string.IsNullOrWhiteSpace(body);

    /// <summary>
    /// Bouwt de <c>ClubMatch</c>-requestbody. <b>Sinds #1427 live bevestigd</b>: veld voor veld
    /// gelijk aan wat Sportlink Clubs eigen formulier verstuurt (console-trace 01-10-2026, HTTP 200
    /// met nieuw <c>PublicMatchId</c>). Datum en tijd gaan apart, het eigen team staat als thuis- én
    /// uit-ID, en de teamnamen zijn vrije tekst.
    /// </summary>
    internal static object BuildClubMatchBody(SportlinkClubMatchAanvraag aanvraag) =>
        new
        {
            AwayTeam = aanvraag.AwayTeam,
            HomeTeam = aanvraag.HomeTeam,
            PublicAwayTeamId = aanvraag.PublicTeamId,
            PublicHomeTeamId = aanvraag.PublicTeamId,
            AgeClassCode = aanvraag.AgeClassCode,
            AwayResult = -1,
            Description = aanvraag.Description,
            Duration = aanvraag.Duration,
            ExternalMatchId = aanvraag.ExternalMatchId,
            HomeResult = -1,
            MatchDate = aanvraag.MatchDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            SportIdTag = aanvraag.SportIdTag,
            StartTime = aanvraag.StartTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            IsHomeMatch = aanvraag.IsHomeMatch,
            FieldOffset = aanvraag.FieldOffset,
            FacilityId = aanvraag.FacilityId,
            SubFacilityId = aanvraag.SubFacilityId,
            FieldSize = aanvraag.FieldSize
        };

}
