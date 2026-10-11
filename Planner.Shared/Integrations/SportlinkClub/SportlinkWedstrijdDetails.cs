using Microsoft.Extensions.Logging;
using System.Text.Json;
using static Planner.Shared.Integrations.SportlinkClub.SportlinkClubHttp;
using static Planner.Shared.Integrations.SportlinkClub.SportlinkWedstrijdDetailsBody;
using static Planner.Shared.Integrations.SportlinkClub.SportlinkMatchChangeValidatieLezer;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Mutaties op wedstrijddetails: kleedkamers, veld en wijzigingsverzoek (#1631, verplaatst uit SportlinkClubClient).</summary>
internal sealed class SportlinkWedstrijdDetails
{
    private readonly SportlinkClubTransport _transport;
    private readonly ILogger _logger;
    private readonly SportlinkMutatieUitvoerder _uitvoerder;

    internal SportlinkWedstrijdDetails(SportlinkClubTransport transport, SportlinkMutatieUitvoerder uitvoerder)
    {
        _transport = transport;
        _logger = transport.Logger;
        _uitvoerder = uitvoerder;
    }

    private const string UpdateMatchDressingRoomsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/UpdateMatchDressingRooms";

    // Live vastgesteld (2026-09-06, netwerktrace door de eigenaar, #1047): Sportlinks eigen UI
    // roept voor een veldwijziging niet "UpdateMatchField" aan (dat endpoint bestaat niet — gaf
    // HTTP 602 "no valid entity key found") maar "UpdateMatchDetails", met het VOLLEDIGE
    // wedstrijdrecord als payload. Zie UpdateFieldAsync/PutMatchDetailsAsync hieronder.
    private const string UpdateMatchDetailsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/UpdateMatchDetails";

    // #995: idem, maar voor het datum/tijd/accommodatie-wijzigingsverzoek — hier bovendien de enige
    // mutatie die een ECHTE tegenstander raakt (Sportlink stuurt bij bevestiging een goedkeurings-
    // verzoek naar de tegenstander). Deze app bouwt uitsluitend stap 1 (valideren, zie
    // RequestMatchChangeAsync) — géén stap 2 (bevestigen). Zelfs stap 1 kan in werkelijkheid al het
    // gevaarlijke moment zijn als Sportlinks eerste PUT geen "dry validate" blijkt te zijn maar
    // direct het verzoek verstuurt — deze code-lock is daarom hier extra belangrijk, niet optioneel.
    // NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995, Aanpak-stap 1: body van
    // beide PUT's en de bevestigingsvlag vastleggen).
    // Eigenaar: op true gezet op 27-09-2026 na live-bevestiging buiten agent-sessie om (#1319).
    private const bool UpdateMatchDetailsChangeRequestLiveBevestigd = true;

    public Task<SportlinkClubResponse<SportlinkMutationResult>> UpdateDressingRoomsAsync(
        string functioneleRol,
        string publicMatchId,
        string? homeDressingRoomId,
        string? awayDressingRoomId,
        string? officialDressingRoomId,
        CancellationToken cancellationToken = default)
        => _transport.ExecuteWithTokenRetryAsync(
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
        => _transport.ExecuteWithTokenRetryAsync(
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
    /// <param name="publicMatchId">Zie de TODO(#987)-waarschuwing op <see cref="ISportlinkClubClient.GetMatchAsync"/>.</param>
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
        => _transport.ExecuteWithTokenRetryAsync<SportlinkMatchChangeRequestResult>(
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
        return _uitvoerder.PutMutationAsync(
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
        var matchData = BouwMatchData(snapshot, new MatchDataOverschrijving(
            FieldId: fieldId, FieldSize: fieldSize, FieldOffset: fieldOffset));

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

        return _uitvoerder.PutMutationAsync(
            UpdateMatchDetailsEndpoint, "competition/match/UpdateMatchDetails", body, token, cancellationToken);
    }

    // #995, Aanpak-stap 1: de eigenaar heeft de body van beide PUT's en de bevestigingsvlag
    // live vastgelegd (#1319) — dit blijft niettemin uitsluitend stap 1 (valideren); stap 2
    // (bevestigen) is een bewuste, aparte scope-beslissing en wordt hier niet gebouwd.
    /// <summary>
    /// Bouwt en verstuurt de <c>UpdateMatchDetails</c>-envelope voor een datum/tijd/accommodatie-
    /// wijzigingsverzoek (#995) — bewust GEEN parametrisering van <see cref="PutMatchDetailsAsync"/>
    /// (#993's live-bevestigde veld-wijzigingspad), zie de doc-comment op
    /// <see cref="RequestMatchChangeAsync"/>. Eigenaarsbesluit (survey 2026-10-11, #1626): de twee
    /// publieke paden blijven gescheiden; alleen het interne samenstellen van de <c>MatchData</c>
    /// wordt gedeeld via <see cref="SportlinkWedstrijdDetailsBody.BouwMatchData"/>. Sinds #1319 volgt deze aanroep de gewone
    /// club-instelling <c>sportlinkDryRun</c> (zie <see cref="UpdateMatchDetailsChangeRequestLiveBevestigd"/>):
    /// bij een echte PUT loopt de respons door <see cref="SportlinkMatchChangeValidatieLezer.ParseMatchChangeValidatie"/> via de
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
        var mutationResponse = await _uitvoerder.PutMutationAsync(
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

    private async Task<SportlinkClubResponse<SportlinkMatchDetailsSnapshot>> FetchMatchDetailsSnapshotAsync(
        string publicMatchId, string token, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _transport.GetMatchEndpointResponseAsync(publicMatchId, token, cancellationToken);
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
}
