using System.Text.Json;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Stateloze opbouw van requestbodies en resultaatverrijking voor de officials- en clubwedstrijd-mutaties (#1631).</summary>
internal static class SportlinkMutatieBodies
{
    /// <summary>
    /// Bouwt de <c>MatchOfficialsAction</c>-requestbody — losgetrokken van <c>PutMatchOfficialsAsync</c>
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
    /// 200 en <c>Error</c> niet gezet. De generieke <see cref="SportlinkMutatieUitvoerder.PutMutationAsync"/>-parsing (gericht op
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

    /// <summary>URL van de verwijderaanroep: <c>PublicMatchId</c> als queryparameter, zoals Sportlinks
    /// eigen frontend hem meestuurt (<c>params:{PublicMatchId}</c> in de bundle, #1440).</summary>
    internal static string BouwClubMatchDeleteUrl(string publicMatchId)
        => ClubMatchDeleteEndpoint + "?PublicMatchId=" + Uri.EscapeDataString(publicMatchId);

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

    // #1440: verwijderen van een clubwedstrijd — zie DeleteClubMatchAsync.
    private const string ClubMatchDeleteEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/clubmatch/ClubMatchDelete";
}
