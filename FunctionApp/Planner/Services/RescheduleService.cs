using Planner.Shared;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner;

/// <summary>
/// Use-case service voor herplan-controles.
/// Extracted uit PlannerService (#475).
/// </summary>
internal static class RescheduleService
{
    public static async Task<HerplanCheckResponse> CheckRescheduleAvailabilityAsync(
        HerplanCheckRequest request, ILogger log, string? clubCode = null)
    {
        var response = new HerplanCheckResponse();
        clubCode = ClubScope.Resolve(clubCode);

        var match = await PlannerDataAccess.FindMatchByCodeAsync(request.Wedstrijdcode, clubCode);
        if (match == null)
        {
            response.Reden = $"Wedstrijd met code {request.Wedstrijdcode} niet gevonden.";
            return response;
        }
        response.HuidigeWedstrijd = match;

        if (!DateOnly.TryParse(match.Datum, out var date))
        {
            response.Reden = "Kan datum van wedstrijd niet verwerken.";
            return response;
        }

        int duurMinuten = match.DuurMinuten;
        decimal veldFractie = match.VeldDeelGebruik;

        var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date, clubCode);
        if (availableFields.Count == 0)
        {
            response.Reden = "Geen velden beschikbaar op deze dag.";
            return response;
        }

        TimeOnly.TryParse(match.AanvangsTijd, out var matchStart);
        var velden     = await PlannerDataAccess.GetVeldenAsync(clubCode);
        // Alleen nodig om het eigen slot niet als "alternatief" terug te geven — niet om de
        // eigen wedstrijd uit de bezetting te filteren. Dat gebeurt op wedstrijdcode (#707).
        int matchVeldNr = PlannerShared.VindVeldNummer(match.VeldNaam, velden);

        var occupations = await SportlinkApiClient.GetFieldOccupationsExcludingWedstrijdcodeWithApiAsync(
            date, match.Wedstrijdcode, log, clubCode);

        var teamRules = new List<TeamRegel>();
        // Één bulkquery voor alle bezette teams i.p.v. één query per team (#575)
        var allTeamRules = await PlannerDataAccess.GetTeamRulesForTeamsAsync(
            occupations.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!), clubCode);

        var sunset = await AvailabilityService.BepaalEnPasZonsondergangToeAsync(date, availableFields);

        TimeOnly? preferredTime = null;
        if (!string.IsNullOrEmpty(request.VoorkeurTijd) && TimeOnly.TryParse(request.VoorkeurTijd, out var parsed))
            preferredTime = parsed;

        (TimeOnly dagdeelVan, TimeOnly dagdeelTot) = AvailabilityService.BepaalDagdeelVenster(request.Dagdeel, new TimeOnly(8, 30), new TimeOnly(22, 0));

        if (preferredTime.HasValue)
        {
            var exactMatch = PlannerShared.TryExactTime(preferredTime.Value, availableFields, occupations, velden,
                                           allTeamRules, teamRules, veldFractie, duurMinuten, sunset);
            if (exactMatch != null)
            {
                response.Beschikbaar = true;
                response.Alternatieven.Add(PlannerShared.ToSlotToewijzing(date, exactMatch, duurMinuten, velden));
            }
        }

        var candidates = PlannerShared.FindAllSlots(availableFields, occupations, velden, allTeamRules, teamRules,
                                      veldFractie, duurMinuten, dagdeelVan, dagdeelTot, sunset);
        candidates = candidates.Where(c => !(c.VeldNummer == matchVeldNr && c.AanvangsTijd == matchStart)).ToList();

        int neemAantal;
        (candidates, neemAantal) = BepaalKandidatenVoorRichting(request.Richting, preferredTime, candidates,
            availableFields, occupations, allTeamRules, teamRules, veldFractie, duurMinuten, matchStart,
            matchVeldNr, dagdeelVan, response.Beschikbaar);

        foreach (var c in candidates.Take(neemAantal))
        {
            var slot = PlannerShared.ToSlotToewijzing(date, c, duurMinuten, velden);
            if (!response.Alternatieven.Any(a => a.AanvangsTijd == slot.AanvangsTijd && a.VeldNummer == slot.VeldNummer))
                response.Alternatieven.Add(slot);
        }

        if (response.Alternatieven.Count == 0)
            response.Reden = $"Geen alternatieve tijdsloten gevonden op {date.ToString("dddd d MMMM", PlannerShared.NL)}.";
        else
            response.Beschikbaar = true;

        PlannerShared.AddWeekdayWarning(response.Waarschuwingen, date);
        return response;
    }

    // ── Privé helpers (alleen voor herplan) ──

    /// <summary>
    /// Bepaalt de kandidatenlijst en het aantal alternatieven dat wordt getoond, afhankelijk van
    /// <paramref name="richting"/>: "vervroegen" zoekt het laatst passende slot per veld vóór de
    /// huidige aanvangstijd, "verlaten" houdt per veld alleen het eerstvolgende slot ná de huidige
    /// aanvangstijd over, en zonder richting sorteert een expliciete voorkeurstijd de bestaande
    /// kandidaten op afstand daartoe. Puur een berekening — geen response-mutatie of early-return.
    /// </summary>
    private static (List<CandidateSlot> Candidates, int NeemAantal) BepaalKandidatenVoorRichting(
        string? richting, TimeOnly? preferredTime, List<CandidateSlot> candidates,
        List<VeldBeschikbaarheidInfo> availableFields, List<BestaandeWedstrijd> occupations,
        Dictionary<string, List<TeamRegel>> allTeamRules, List<TeamRegel> teamRules,
        decimal veldFractie, int duurMinuten, TimeOnly matchStart, int matchVeldNr,
        TimeOnly dagdeelVan, bool responseAlBeschikbaar)
    {
        int neemAantal = responseAlBeschikbaar ? 2 : 3;

        bool vervroegen = string.Equals(richting, "vervroegen", StringComparison.OrdinalIgnoreCase);
        bool verlaten   = string.Equals(richting, "verlaten",   StringComparison.OrdinalIgnoreCase);

        if (vervroegen)
        {
            candidates = FindLatestFitPerField(availableFields, occupations, allTeamRules, teamRules,
                veldFractie, duurMinuten, upperBound: matchStart, windowStart: dagdeelVan);
            candidates = candidates
                .Where(c => !(c.VeldNummer == matchVeldNr && c.AanvangsTijd == matchStart))
                .OrderByDescending(c => c.AanvangsTijd).ToList();
            neemAantal = candidates.Count;
        }
        else if (verlaten)
        {
            candidates = candidates
                .Where(c => c.AanvangsTijd > matchStart)
                .GroupBy(c => c.VeldNummer).Select(g => g.OrderBy(c => c.AanvangsTijd).First())
                .OrderBy(c => c.AanvangsTijd).ToList();
            neemAantal = candidates.Count;
        }
        else if (preferredTime.HasValue)
        {
            candidates = candidates
                .OrderBy(c => Math.Abs(c.AanvangsTijd.ToTimeSpan().TotalMinutes - preferredTime.Value.ToTimeSpan().TotalMinutes))
                .ToList();
        }

        return (candidates, neemAantal);
    }

    private static List<CandidateSlot> FindLatestFitPerField(
        List<VeldBeschikbaarheidInfo> availableFields,
        List<BestaandeWedstrijd> occupations,
        Dictionary<string, List<TeamRegel>> allTeamRules,
        List<TeamRegel> requestingTeamRules,
        decimal veldFractie, int duurMinuten,
        TimeOnly upperBound, TimeOnly windowStart)
    {
        var result = new List<CandidateSlot>();
        foreach (var field in availableFields)
        {
            var fieldOccs = occupations.Where(o => o.VeldNummer == field.VeldNummer).ToList();
            var effStart  = windowStart < field.BeschikbaarVanaf ? field.BeschikbaarVanaf : windowStart;
            var effUpper  = upperBound  > field.BeschikbaarTot   ? field.BeschikbaarTot   : upperBound;
            var nextOcc = fieldOccs
                .Where(o => o.AanvangsTijd >= effStart && o.AanvangsTijd < effUpper)
                .OrderBy(o => o.AanvangsTijd).FirstOrDefault();
            var hardEnd = nextOcc != null && nextOcc.AanvangsTijd < effUpper ? nextOcc.AanvangsTijd : effUpper;
            var latestStart = hardEnd.AddMinutes(-duurMinuten);
            if (latestStart < effStart) continue;
            for (var time = latestStart; time >= effStart; time = time.AddMinutes(-5))
            {
                var endTime = time.AddMinutes(duurMinuten);
                if (PlannerShared.CanFitMatch(time, endTime, veldFractie, field.VeldNummer,
                                fieldOccs, allTeamRules, requestingTeamRules))
                {
                    result.Add(new CandidateSlot { VeldNummer = field.VeldNummer, AanvangsTijd = time, EindTijd = endTime });
                    break;
                }
            }
        }
        return result;
    }
}
