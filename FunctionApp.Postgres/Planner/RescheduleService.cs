using FunctionApp.Postgres.Planner.Repositories;
using Microsoft.Extensions.Logging;
using Planner.Shared;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/Services/RescheduleService.cs</c> (issue
/// 888 vervolg, §41). Ontsluit <c>HerplanCheck</c>.
/// <para>
/// Zelfde scope-beslissing als <see cref="AvailabilityService"/>: gebruikt uitsluitend de
/// DB-bezetting (<see cref="PlannerAvailabilityRepository.GetFieldOccupationsExcludingAsync"/>),
/// niet het real-time Sportlink-API-pad van het SQL Server-origineel
/// (<c>SportlinkApiClient.GetFieldOccupationsExcludingWedstrijdcodeWithApiAsync</c>) — die
/// integratie is een aparte eenheid werk, buiten deze slice.
/// </para>
/// </summary>
public static class RescheduleService
{
    public static async Task<HerplanCheckResponse> CheckRescheduleAvailabilityAsync(
        string connectionString, HerplanCheckRequest request, ILogger log, string? clubCode)
    {
        var response = new HerplanCheckResponse();
        var cc = PostgresClubScope.Resolve(clubCode);

        var match = await PlannerMatchRepository.FindMatchByCodeAsync(connectionString, request.Wedstrijdcode, cc);
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

        var availableFields = await PlannerAvailabilityRepository.GetAvailableFieldsAsync(connectionString, date, cc);
        if (availableFields.Count == 0)
        {
            response.Reden = "Geen velden beschikbaar op deze dag.";
            return response;
        }

        TimeOnly.TryParse(match.AanvangsTijd, out var matchStart);
        var velden = await PlannerSettingsRepository.GetVeldenAsync(connectionString, cc);
        // Alleen nodig om het eigen slot niet als "alternatief" terug te geven — niet om de eigen
        // wedstrijd uit de bezetting te filteren. Dat gebeurt op wedstrijdcode (#707).
        int matchVeldNr = PlannerShared.VindVeldNummer(match.VeldNaam, velden);

        var occupations = await PlannerAvailabilityRepository.GetFieldOccupationsExcludingAsync(
            connectionString, date, request.Wedstrijdcode, cc);

        var teamRules = new List<TeamRegel>();
        var allTeamRules = await TeamRulesRepository.GetTeamRulesForTeamsAsync(
            connectionString, occupations.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!), cc);

        TimeOnly? sunset = await PlannerSettingsRepository.ResolveEnPasZonsondergangToeAsync(connectionString, date, availableFields);

        TimeOnly? preferredTime = null;
        if (!string.IsNullOrEmpty(request.VoorkeurTijd) && TimeOnly.TryParse(request.VoorkeurTijd, out var parsed))
            preferredTime = parsed;

        (TimeOnly dagdeelVan, TimeOnly dagdeelTot) = AvailabilityService.ResolveDagdeelVenster(request.Dagdeel, new(8, 30), new(22, 0));

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
        bool verlaten = string.Equals(request.Richting, "verlaten", StringComparison.OrdinalIgnoreCase);
        int neemAantal = response.Beschikbaar ? 2 : 3;

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

    // ── Privé helper (alleen voor herplan) — duplicaat van het SQL Server-origineel, geen
    // gedeelde-locatie-eis: puur een lokaal detail van deze ene use-case (§16). ──

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
            var effStart = windowStart < field.BeschikbaarVanaf ? field.BeschikbaarVanaf : windowStart;
            var effUpper = upperBound > field.BeschikbaarTot ? field.BeschikbaarTot : upperBound;
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
