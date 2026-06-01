using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner;

/// <summary>
/// Use-case service voor herplan-controles.
/// Extracted uit PlannerService (#475).
/// </summary>
internal static class RescheduleService
{
    public static async Task<HerplanCheckResponse> CheckRescheduleAvailabilityAsync(
        HerplanCheckRequest request, ILogger log)
    {
        var response = new HerplanCheckResponse();

        var match = await PlannerDataAccess.FindMatchByCodeAsync(request.Wedstrijdcode);
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

        var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date);
        if (availableFields.Count == 0)
        {
            response.Reden = "Geen velden beschikbaar op deze dag.";
            return response;
        }

        TimeOnly.TryParse(match.AanvangsTijd, out var matchStart);
        var velden     = await PlannerDataAccess.GetVeldenAsync();
        var matchVeld  = velden.FirstOrDefault(v => match.VeldNaam != null && match.VeldNaam.StartsWith(v.VeldNaam));
        int matchVeldNr = matchVeld?.VeldNummer ?? 0;

        var occupations = await SportlinkApiClient.GetFieldOccupationsExcludingMatchWithApiAsync(
            date, match.Wedstrijd, matchStart, matchVeldNr, log);

        var teamRules    = new List<TeamRegel>();
        var allTeamRules = new Dictionary<string, List<TeamRegel>>();
        foreach (var occ in occupations.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!).Distinct())
            allTeamRules[occ] = await PlannerDataAccess.GetTeamRulesAsync(occ);

        TimeOnly? sunset = await PlannerDataAccess.GetSunsetAsync(date);
        if (sunset == null) sunset = SunsetCalculator.GetSunset(date);
        foreach (var field in availableFields)
            if (field.GebruikZonsondergang && sunset.HasValue && sunset.Value < field.BeschikbaarTot)
                field.BeschikbaarTot = sunset.Value;

        TimeOnly? preferredTime = null;
        if (!string.IsNullOrEmpty(request.VoorkeurTijd) && TimeOnly.TryParse(request.VoorkeurTijd, out var parsed))
            preferredTime = parsed;

        TimeOnly dagdeelVan = new(8, 30);
        TimeOnly dagdeelTot = new(22, 0);
        if (!string.IsNullOrEmpty(request.Dagdeel))
            switch (request.Dagdeel.ToLowerInvariant())
            {
                case "ochtend": dagdeelVan = new(8, 30); dagdeelTot = new(12, 0); break;
                case "middag":  dagdeelVan = new(12, 0); dagdeelTot = new(17, 0); break;
                case "avond":   dagdeelVan = new(17, 0); dagdeelTot = new(22, 0); break;
            }

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

        bool vervroegen = string.Equals(request.Richting, "vervroegen", StringComparison.OrdinalIgnoreCase);
        bool verlaten   = string.Equals(request.Richting, "verlaten",   StringComparison.OrdinalIgnoreCase);
        int neemAantal  = response.Beschikbaar ? 2 : 3;

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

    // ── Privé helper (alleen voor herplan) ──

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
