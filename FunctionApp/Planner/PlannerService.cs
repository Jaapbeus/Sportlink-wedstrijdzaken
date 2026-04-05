using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner
{
    public static class PlannerService
    {
        private const int StandardBufferMinutes = 10;
        private const int SunsetWarningMarginMinutes = 20;

        public static async Task<CheckAvailabilityResponse> CheckAvailabilityAsync(
            CheckAvailabilityRequest request, ILogger log)
        {
            var response = new CheckAvailabilityResponse();

            // Parse date
            if (!DateOnly.TryParse(request.Datum, out var date))
            {
                response.Reden = $"Ongeldige datum: {request.Datum}";
                return response;
            }

            // Date must be in the future (not today, not in the past)
            if (date <= DateOnly.FromDateTime(DateTime.Today))
            {
                response.Reden = $"De gewenste datum {request.Datum} kan niet verwerkt worden. Een datum moet in de toekomst zijn.";
                return response;
            }

            // Step 1: Resolve match parameters from Speeltijden
            Speeltijd? speeltijd = null;
            int duurMinuten = 105; // default senior
            decimal veldFractie = 1.00m;

            if (!string.IsNullOrEmpty(request.LeeftijdsCategorie))
            {
                speeltijd = await PlannerDataAccess.GetSpeeltijdAsync(request.LeeftijdsCategorie);
                if (speeltijd == null)
                {
                    response.Reden = $"Onbekende leeftijdscategorie: {request.LeeftijdsCategorie}";
                    return response;
                }
                duurMinuten = request.WedstrijdDuurMinuten ?? speeltijd.WedstrijdTotaal;
                veldFractie = speeltijd.Veldafmeting;
            }
            else if (request.WedstrijdDuurMinuten.HasValue)
            {
                duurMinuten = request.WedstrijdDuurMinuten.Value;
            }

            // Step 2: Team conflict check
            if (!string.IsNullOrEmpty(request.TeamNaam))
            {
                var teamMatches = await PlannerDataAccess.GetTeamMatchesOnDateAsync(request.TeamNaam, date);
                if (teamMatches.Count > 0)
                {
                    var conflict = teamMatches[0];
                    response.TeamConflict = new TeamConflictInfo
                    {
                        Wedstrijd = conflict.Wedstrijd ?? "",
                        AanvangsTijd = conflict.AanvangsTijd.ToString("HH:mm"),
                        EindTijd = conflict.EindTijd.ToString("HH:mm"),
                        VeldNaam = conflict.VeldNummer > 0 ? $"veld {conflict.VeldNummer}" : "onbekend"
                    };
                    response.Reden = $"{request.TeamNaam} heeft al een wedstrijd op {date:d MMMM}: " +
                                     $"{conflict.Wedstrijd} om {conflict.AanvangsTijd:HH:mm} ({response.TeamConflict.VeldNaam}).";
                    return response;
                }
            }

            // Step 3: Load available fields for this day
            var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date);
            if (availableFields.Count == 0)
            {
                response.Reden = $"Geen wedstrijden mogelijk op {date.DayOfWeek switch
                {
                    DayOfWeek.Friday => "vrijdag",
                    DayOfWeek.Sunday => "zondag",
                    _ => date.ToString("dddd d MMMM")
                }}.";
                return response;
            }

            // Step 4: Load all existing occupations
            var occupations = await PlannerDataAccess.GetFieldOccupationsAsync(date);
            var velden = await PlannerDataAccess.GetVeldenAsync();

            // Step 5: Load team-specific rules
            var teamRules = new List<TeamRegel>();
            if (!string.IsNullOrEmpty(request.TeamNaam))
                teamRules = await PlannerDataAccess.GetTeamRulesAsync(request.TeamNaam);

            // Also load rules for teams already on the schedule (their buffers must be respected)
            var allTeamRules = new Dictionary<string, List<TeamRegel>>();
            foreach (var occ in occupations.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!).Distinct())
            {
                allTeamRules[occ] = await PlannerDataAccess.GetTeamRulesAsync(occ);
            }

            // Step 6: Get sunset time
            var sunset = await PlannerDataAccess.GetSunsetAsync(date);
            if (sunset == null)
            {
                // Compute on-the-fly if not in table
                sunset = SunsetCalculator.GetSunset(date);
            }

            // Apply sunset constraint to field availability windows
            foreach (var field in availableFields)
            {
                if (field.GebruikZonsondergang && sunset.HasValue)
                {
                    if (sunset.Value < field.BeschikbaarTot)
                        field.BeschikbaarTot = sunset.Value;
                }
            }

            // Mode 2: no leeftijdsCategorie — return available windows
            if (string.IsNullOrEmpty(request.LeeftijdsCategorie) && !request.WedstrijdDuurMinuten.HasValue)
            {
                return BuildWindowsResponse(date, availableFields, occupations, velden, sunset, request.Dagdeel);
            }

            // Mode 1: specific slot assignment
            TimeOnly? preferredTime = null;
            if (!string.IsNullOrEmpty(request.AanvangsTijd) && TimeOnly.TryParse(request.AanvangsTijd, out var parsed))
                preferredTime = parsed;

            // Apply dagdeel filter
            TimeOnly dagdeelVan = new(8, 30);
            TimeOnly dagdeelTot = new(22, 0);
            if (!string.IsNullOrEmpty(request.Dagdeel))
            {
                switch (request.Dagdeel.ToLowerInvariant())
                {
                    case "ochtend": dagdeelVan = new(8, 30); dagdeelTot = new(12, 0); break;
                    case "middag": dagdeelVan = new(12, 0); dagdeelTot = new(17, 0); break;
                    case "avond": dagdeelVan = new(17, 0); dagdeelTot = new(22, 0); break;
                }
            }

            // Step 7: Try preferred time first (direct check, before full scan)
            if (preferredTime.HasValue)
            {
                var exactMatch = TryExactTime(preferredTime.Value, availableFields, occupations, velden,
                                               allTeamRules, teamRules, veldFractie, duurMinuten, sunset);
                if (exactMatch != null)
                {
                    response.Beschikbaar = true;
                    response.Toewijzing = ToSlotToewijzing(date, exactMatch, duurMinuten, velden);
                    AddSunsetWarning(response, exactMatch, sunset, velden);
                    AddWeekdayWarning(response, date);
                    return response;
                }
            }

            // Try to find alternative slots
            var candidates = FindAllSlots(availableFields, occupations, velden, allTeamRules, teamRules,
                                          veldFractie, duurMinuten, dagdeelVan, dagdeelTot, sunset);

            // Step 8: Find best available slot
            if (candidates.Count > 0)
            {
                // Sort by proximity to preferred time, or by scheduling preference
                var ordered = preferredTime.HasValue
                    ? candidates.OrderBy(c => Math.Abs(c.AanvangsTijd.ToTimeSpan().TotalMinutes - preferredTime.Value.ToTimeSpan().TotalMinutes))
                    : candidates.OrderBy(c => c.AanvangsTijd.ToTimeSpan().TotalMinutes);

                var best = ordered.First();
                var alternatives = ordered.Skip(1).Take(3).ToList();

                if (preferredTime.HasValue)
                {
                    // Preferred time didn't match exactly — best is an alternative
                    response.Reden = $"Gewenste tijd {preferredTime.Value:HH:mm} is niet beschikbaar.";
                    response.Alternatieven = alternatives.Prepend(best)
                        .Select(c => ToSlotToewijzing(date, c, duurMinuten, velden)).Take(3).ToList();
                    AddWeekdayWarning(response, date);
                }
                else
                {
                    response.Beschikbaar = true;
                    response.Toewijzing = ToSlotToewijzing(date, best, duurMinuten, velden);
                    response.Alternatieven = alternatives
                        .Select(c => ToSlotToewijzing(date, c, duurMinuten, velden)).ToList();
                    AddSunsetWarning(response, best, sunset, velden);
                    AddWeekdayWarning(response, date);
                }
            }
            else
            {
                response.Reden = $"Geen beschikbaar veld gevonden op {date:dddd d MMMM}.";
                AddWeekdayWarning(response, date);
            }

            return response;
        }

        private static CandidateSlot? TryExactTime(
            TimeOnly preferredTime,
            List<VeldBeschikbaarheidInfo> availableFields,
            List<BestaandeWedstrijd> occupations,
            List<VeldInfo> velden,
            Dictionary<string, List<TeamRegel>> allTeamRules,
            List<TeamRegel> requestingTeamRules,
            decimal veldFractie, int duurMinuten, TimeOnly? sunset)
        {
            var endTime = preferredTime.AddMinutes(duurMinuten);

            // Try each field in preference order (veld 1-4 before veld 5)
            foreach (var field in availableFields.OrderBy(f => f.VeldNummer == 5 ? 1 : 0))
            {
                // Check within availability window
                if (preferredTime < field.BeschikbaarVanaf || endTime > field.BeschikbaarTot)
                    continue;

                var fieldOccupations = occupations.Where(o => o.VeldNummer == field.VeldNummer).ToList();
                if (CanFitMatch(preferredTime, endTime, veldFractie, field.VeldNummer,
                                fieldOccupations, allTeamRules, requestingTeamRules))
                {
                    return new CandidateSlot
                    {
                        VeldNummer = field.VeldNummer,
                        AanvangsTijd = preferredTime,
                        EindTijd = endTime
                    };
                }
            }
            return null;
        }

        private static List<CandidateSlot> FindAllSlots(
            List<VeldBeschikbaarheidInfo> availableFields,
            List<BestaandeWedstrijd> occupations,
            List<VeldInfo> velden,
            Dictionary<string, List<TeamRegel>> allTeamRules,
            List<TeamRegel> requestingTeamRules,
            decimal veldFractie, int duurMinuten,
            TimeOnly dagdeelVan, TimeOnly dagdeelTot,
            TimeOnly? sunset)
        {
            var candidates = new List<CandidateSlot>();

            foreach (var field in availableFields)
            {
                var fieldOccupations = occupations.Where(o => o.VeldNummer == field.VeldNummer).ToList();
                var veldInfo = velden.FirstOrDefault(v => v.VeldNummer == field.VeldNummer);

                // Determine the effective time window for this field
                var windowStart = dagdeelVan < field.BeschikbaarVanaf ? field.BeschikbaarVanaf : dagdeelVan;
                var windowEnd = dagdeelTot > field.BeschikbaarTot ? field.BeschikbaarTot : dagdeelTot;

                // Scan in 5-minute increments
                for (var time = windowStart; time.AddMinutes(duurMinuten) <= windowEnd; time = time.AddMinutes(5))
                {
                    var endTime = time.AddMinutes(duurMinuten);

                    if (CanFitMatch(time, endTime, veldFractie, field.VeldNummer,
                                    fieldOccupations, allTeamRules, requestingTeamRules))
                    {
                        candidates.Add(new CandidateSlot
                        {
                            VeldNummer = field.VeldNummer,
                            AanvangsTijd = time,
                            EindTijd = endTime
                        });
                        // Skip ahead past this slot for this field to avoid overlapping candidates
                        time = time.AddMinutes(duurMinuten + StandardBufferMinutes - 5); // -5 because loop adds 5
                    }
                }
            }

            // Sort: prefer veld 1-4 over veld 5
            return candidates.OrderBy(c => c.VeldNummer == 5 ? 1 : 0)
                             .ThenBy(c => c.AanvangsTijd.ToTimeSpan().TotalMinutes)
                             .ToList();
        }

        private static bool CanFitMatch(
            TimeOnly start, TimeOnly end, decimal veldFractie, int veldNummer,
            List<BestaandeWedstrijd> fieldOccupations,
            Dictionary<string, List<TeamRegel>> allTeamRules,
            List<TeamRegel> requestingTeamRules)
        {
            // Get buffer requirements for the requesting team
            int bufferVoor = StandardBufferMinutes;
            int bufferNa = StandardBufferMinutes;
            foreach (var rule in requestingTeamRules)
            {
                if (rule.RegelType == "BufferVoor" && rule.WaardeMinuten.HasValue)
                    bufferVoor = Math.Max(bufferVoor, rule.WaardeMinuten.Value);
                if (rule.RegelType == "BufferNa" && rule.WaardeMinuten.HasValue)
                    bufferNa = Math.Max(bufferNa, rule.WaardeMinuten.Value);
            }

            var bufferedStart = start.AddMinutes(-bufferVoor);
            var bufferedEnd = end.AddMinutes(bufferNa);

            foreach (var occ in fieldOccupations)
            {
                // Check if time windows overlap
                bool overlaps = occ.AanvangsTijd < bufferedEnd && occ.EindTijd > bufferedStart;
                if (!overlaps) continue;

                // For sub-field matches, check capacity during actual match time (not buffer time)
                if (veldFractie < 1.0m && occ.VeldDeelGebruik < 1.0m)
                {
                    // Only check actual overlap (not buffers) for sub-field capacity
                    bool actualOverlap = occ.AanvangsTijd < end && occ.EindTijd > start;
                    if (actualOverlap)
                    {
                        // Check total capacity at this time slot
                        decimal usedCapacity = fieldOccupations
                            .Where(o => o.AanvangsTijd < end && o.EindTijd > start)
                            .Sum(o => o.VeldDeelGebruik);
                        if (usedCapacity + veldFractie > 1.0m)
                            return false;
                        continue; // sub-field sharing is fine if capacity allows
                    }
                    continue;
                }

                // For full-field matches, or if existing match is full-field: conflict
                if (veldFractie >= 1.0m || occ.VeldDeelGebruik >= 1.0m)
                    return false;

                // Also check existing team's buffer rules
                if (!string.IsNullOrEmpty(occ.TeamNaam) && allTeamRules.TryGetValue(occ.TeamNaam, out var existingRules))
                {
                    int existingBufVoor = StandardBufferMinutes;
                    int existingBufNa = StandardBufferMinutes;
                    foreach (var rule in existingRules)
                    {
                        if (rule.RegelType == "BufferVoor" && rule.WaardeMinuten.HasValue)
                            existingBufVoor = Math.Max(existingBufVoor, rule.WaardeMinuten.Value);
                        if (rule.RegelType == "BufferNa" && rule.WaardeMinuten.HasValue)
                            existingBufNa = Math.Max(existingBufNa, rule.WaardeMinuten.Value);
                    }

                    var existingBufferedStart = occ.AanvangsTijd.AddMinutes(-existingBufVoor);
                    var existingBufferedEnd = occ.EindTijd.AddMinutes(existingBufNa);

                    if (start < existingBufferedEnd && end > existingBufferedStart)
                        return false;
                }
            }

            return true;
        }

        private static CheckAvailabilityResponse BuildWindowsResponse(
            DateOnly date, List<VeldBeschikbaarheidInfo> fields,
            List<BestaandeWedstrijd> occupations, List<VeldInfo> velden,
            TimeOnly? sunset, string? dagdeel)
        {
            var response = new CheckAvailabilityResponse();
            var windows = new List<BeschikbaarVenster>();

            TimeOnly filterVan = new(0, 0);
            TimeOnly filterTot = new(23, 59);
            if (!string.IsNullOrEmpty(dagdeel))
            {
                switch (dagdeel.ToLowerInvariant())
                {
                    case "ochtend": filterVan = new(8, 30); filterTot = new(12, 0); break;
                    case "middag": filterVan = new(12, 0); filterTot = new(17, 0); break;
                    case "avond": filterVan = new(17, 0); filterTot = new(22, 0); break;
                }
            }

            foreach (var field in fields)
            {
                var veldInfo = velden.FirstOrDefault(v => v.VeldNummer == field.VeldNummer);
                var fieldOccs = occupations.Where(o => o.VeldNummer == field.VeldNummer)
                                           .OrderBy(o => o.AanvangsTijd).ToList();

                var effectiveStart = field.BeschikbaarVanaf < filterVan ? filterVan : field.BeschikbaarVanaf;
                var effectiveEnd = field.BeschikbaarTot > filterTot ? filterTot : field.BeschikbaarTot;

                // Find gaps
                var gapStart = effectiveStart;
                foreach (var occ in fieldOccs)
                {
                    var occStart = occ.AanvangsTijd.AddMinutes(-StandardBufferMinutes);
                    if (occStart > gapStart)
                    {
                        var gapMinutes = (int)(occStart.ToTimeSpan() - gapStart.ToTimeSpan()).TotalMinutes;
                        if (gapMinutes >= 30) // only show windows of at least 30 min
                        {
                            windows.Add(new BeschikbaarVenster
                            {
                                VeldNummer = field.VeldNummer,
                                VeldNaam = veldInfo?.VeldNaam ?? $"veld {field.VeldNummer}",
                                Van = gapStart.ToString("HH:mm"),
                                Tot = occStart.ToString("HH:mm"),
                                MaxDuurMinuten = gapMinutes,
                                Opmerking = !field.GebruikZonsondergang ? null :
                                    $"Zonsondergang {sunset:HH:mm}, geen kunstlicht"
                            });
                        }
                    }
                    gapStart = occ.EindTijd.AddMinutes(StandardBufferMinutes);
                }

                // Gap after last match
                if (gapStart < effectiveEnd)
                {
                    var gapMinutes = (int)(effectiveEnd.ToTimeSpan() - gapStart.ToTimeSpan()).TotalMinutes;
                    if (gapMinutes >= 30)
                    {
                        windows.Add(new BeschikbaarVenster
                        {
                            VeldNummer = field.VeldNummer,
                            VeldNaam = veldInfo?.VeldNaam ?? $"veld {field.VeldNummer}",
                            Van = gapStart.ToString("HH:mm"),
                            Tot = effectiveEnd.ToString("HH:mm"),
                            MaxDuurMinuten = gapMinutes,
                            Opmerking = !field.GebruikZonsondergang ? null :
                                $"Zonsondergang {sunset:HH:mm}, geen kunstlicht"
                        });
                    }
                }
            }

            response.Beschikbaar = windows.Count > 0;
            response.BeschikbareVensters = windows;
            if (!response.Beschikbaar)
                response.Reden = $"Geen beschikbare vensters op {date:dddd d MMMM}.";
            AddWeekdayWarning(response, date);
            return response;
        }

        private static SlotToewijzing ToSlotToewijzing(
            DateOnly date, CandidateSlot slot, int duurMinuten, List<VeldInfo> velden)
        {
            var veld = velden.FirstOrDefault(v => v.VeldNummer == slot.VeldNummer);
            return new SlotToewijzing
            {
                Datum = date.ToString("yyyy-MM-dd"),
                AanvangsTijd = slot.AanvangsTijd.ToString("HH:mm"),
                EindTijd = slot.EindTijd.ToString("HH:mm"),
                VeldNummer = slot.VeldNummer,
                VeldNaam = veld?.VeldNaam ?? $"veld {slot.VeldNummer}",
                VeldDeelGebruik = slot.VeldFractie > 0 ? slot.VeldFractie : 1.00m,
                WedstrijdDuurMinuten = duurMinuten
            };
        }

        private static void AddSunsetWarning(
            CheckAvailabilityResponse response, CandidateSlot slot,
            TimeOnly? sunset, List<VeldInfo> velden)
        {
            if (!sunset.HasValue) return;
            var veld = velden.FirstOrDefault(v => v.VeldNummer == slot.VeldNummer);
            if (veld == null || veld.HeeftKunstlicht) return;

            var margin = (sunset.Value.ToTimeSpan() - slot.EindTijd.ToTimeSpan()).TotalMinutes;
            if (margin < SunsetWarningMarginMinutes)
            {
                response.Waarschuwingen.Add(
                    $"Geen kunstlicht op {veld.VeldNaam}. Wedstrijd eindigt om {slot.EindTijd:HH:mm}, " +
                    $"zonsondergang {sunset.Value:HH:mm} ({(int)margin} min marge).");
            }
        }

        private static void AddWeekdayWarning(CheckAvailabilityResponse response, DateOnly date)
        {
            if (date.DayOfWeek >= DayOfWeek.Monday && date.DayOfWeek <= DayOfWeek.Thursday)
            {
                response.Waarschuwingen.Add(
                    $"{date.DayOfWeek}: alleen veld 5 beschikbaar (veld 1-4 training).");
            }
        }

        private class CandidateSlot
        {
            public int VeldNummer { get; set; }
            public TimeOnly AanvangsTijd { get; set; }
            public TimeOnly EindTijd { get; set; }
            public decimal VeldFractie { get; set; }
        }

        // ── Herplan (reschedule) logic ──

        public static async Task<HerplanCheckResponse> CheckRescheduleAvailabilityAsync(
            HerplanCheckRequest request, ILogger log)
        {
            var response = new HerplanCheckResponse();

            // Step 1: Find the match by wedstrijdcode
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

            // Step 2: Resolve match parameters
            int duurMinuten = match.DuurMinuten;
            decimal veldFractie = match.VeldDeelGebruik;

            // Step 3: Load available fields for this day
            var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date);
            if (availableFields.Count == 0)
            {
                response.Reden = "Geen velden beschikbaar op deze dag.";
                return response;
            }

            // Step 4: Load occupations EXCLUDING the match being rescheduled
            TimeOnly.TryParse(match.AanvangsTijd, out var matchStart);
            var velden = await PlannerDataAccess.GetVeldenAsync();
            var matchVeld = velden.FirstOrDefault(v => match.VeldNaam != null && match.VeldNaam.StartsWith(v.VeldNaam));
            int matchVeldNummer = matchVeld?.VeldNummer ?? 0;

            var occupations = await PlannerDataAccess.GetFieldOccupationsExcludingMatchAsync(
                date, match.Wedstrijd, matchStart, matchVeldNummer);

            // Step 5: Load team rules (empty for reschedule — we don't know the requesting team's rules)
            var teamRules = new List<TeamRegel>();
            var allTeamRules = new Dictionary<string, List<TeamRegel>>();
            foreach (var occ in occupations.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!).Distinct())
            {
                allTeamRules[occ] = await PlannerDataAccess.GetTeamRulesAsync(occ);
            }

            // Step 6: Sunset
            var sunset = await PlannerDataAccess.GetSunsetAsync(date);
            if (sunset == null) sunset = SunsetCalculator.GetSunset(date);

            foreach (var field in availableFields)
            {
                if (field.GebruikZonsondergang && sunset.HasValue && sunset.Value < field.BeschikbaarTot)
                    field.BeschikbaarTot = sunset.Value;
            }

            // Step 7: Parse preferred time and dagdeel
            TimeOnly? preferredTime = null;
            if (!string.IsNullOrEmpty(request.VoorkeurTijd) && TimeOnly.TryParse(request.VoorkeurTijd, out var parsed))
                preferredTime = parsed;

            TimeOnly dagdeelVan = new(8, 30);
            TimeOnly dagdeelTot = new(22, 0);
            if (!string.IsNullOrEmpty(request.Dagdeel))
            {
                switch (request.Dagdeel.ToLowerInvariant())
                {
                    case "ochtend": dagdeelVan = new(8, 30); dagdeelTot = new(12, 0); break;
                    case "middag": dagdeelVan = new(12, 0); dagdeelTot = new(17, 0); break;
                    case "avond": dagdeelVan = new(17, 0); dagdeelTot = new(22, 0); break;
                }
            }

            // Step 8: Find alternative slots (reusing existing logic)
            if (preferredTime.HasValue)
            {
                var exactMatch = TryExactTime(preferredTime.Value, availableFields, occupations, velden,
                                               allTeamRules, teamRules, veldFractie, duurMinuten, sunset);
                if (exactMatch != null)
                {
                    response.Beschikbaar = true;
                    response.Alternatieven.Add(ToSlotToewijzing(date, exactMatch, duurMinuten, velden));
                }
            }

            var candidates = FindAllSlots(availableFields, occupations, velden, allTeamRules, teamRules,
                                          veldFractie, duurMinuten, dagdeelVan, dagdeelTot, sunset);

            // Exclude the current time slot from alternatives
            candidates = candidates.Where(c =>
                !(c.VeldNummer == matchVeldNummer && c.AanvangsTijd == matchStart)).ToList();

            if (preferredTime.HasValue)
            {
                candidates = candidates
                    .OrderBy(c => Math.Abs(c.AanvangsTijd.ToTimeSpan().TotalMinutes - preferredTime.Value.ToTimeSpan().TotalMinutes))
                    .ToList();
            }

            foreach (var c in candidates.Take(response.Beschikbaar ? 2 : 3))
            {
                var slot = ToSlotToewijzing(date, c, duurMinuten, velden);
                if (!response.Alternatieven.Any(a => a.AanvangsTijd == slot.AanvangsTijd && a.VeldNummer == slot.VeldNummer))
                    response.Alternatieven.Add(slot);
            }

            if (response.Alternatieven.Count == 0)
            {
                response.Reden = $"Geen alternatieve tijdsloten gevonden op {date:dddd d MMMM}.";
            }
            else
            {
                response.Beschikbaar = true;
            }

            AddWeekdayWarning(response.Waarschuwingen, date);
            return response;
        }

        private static void AddWeekdayWarning(List<string> waarschuwingen, DateOnly date)
        {
            if (date.DayOfWeek >= DayOfWeek.Monday && date.DayOfWeek <= DayOfWeek.Thursday)
            {
                waarschuwingen.Add($"{date.DayOfWeek}: alleen veld 5 beschikbaar (veld 1-4 training).");
            }
        }
    }
}
