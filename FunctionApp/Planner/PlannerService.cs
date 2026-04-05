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

            // Datum verwerken
            if (!DateOnly.TryParse(request.Datum, out var date))
            {
                response.Reden = $"Ongeldige datum: {request.Datum}";
                return response;
            }

            // Datum moet in de toekomst liggen (niet vandaag, niet in het verleden)
            if (date <= DateOnly.FromDateTime(DateTime.Today))
            {
                response.Reden = $"De gewenste datum {request.Datum} kan niet verwerkt worden. Een datum moet in de toekomst zijn.";
                return response;
            }

            // Stap 1: Wedstrijdparameters bepalen uit Speeltijden
            Speeltijd? speeltijd = null;
            int duurMinuten = 105; // standaard senioren
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

            // Stap 2: Team conflictcontrole
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

            // Stap 3: Beschikbare velden voor deze dag laden
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

            // Stap 4: Alle huidige veldbezettingen laden
            var occupations = await PlannerDataAccess.GetFieldOccupationsAsync(date);
            var velden = await PlannerDataAccess.GetVeldenAsync();

            // Stap 5: Teamspecifieke regels laden
            var teamRules = new List<TeamRegel>();
            if (!string.IsNullOrEmpty(request.TeamNaam))
                teamRules = await PlannerDataAccess.GetTeamRulesAsync(request.TeamNaam);

            // Laad ook regels voor teams die al op het programma staan (hun buffers moeten gerespecteerd worden)
            var allTeamRules = new Dictionary<string, List<TeamRegel>>();
            foreach (var occ in occupations.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!).Distinct())
            {
                allTeamRules[occ] = await PlannerDataAccess.GetTeamRulesAsync(occ);
            }

            // Stap 6: Zonsondergangstijd ophalen
            var sunset = await PlannerDataAccess.GetSunsetAsync(date);
            if (sunset == null)
            {
                // Ter plekke berekenen als niet in tabel
                sunset = SunsetCalculator.GetSunset(date);
            }

            // Zonsondergang-beperking toepassen op beschikbaarheidsvensters
            foreach (var field in availableFields)
            {
                if (field.GebruikZonsondergang && sunset.HasValue)
                {
                    if (sunset.Value < field.BeschikbaarTot)
                        field.BeschikbaarTot = sunset.Value;
                }
            }

            // Modus 2: geen leeftijdsCategorie — beschikbare vensters teruggeven
            if (string.IsNullOrEmpty(request.LeeftijdsCategorie) && !request.WedstrijdDuurMinuten.HasValue)
            {
                return BuildWindowsResponse(date, availableFields, occupations, velden, sunset, request.Dagdeel);
            }

            // Modus 1: specifieke slottoewijzing
            TimeOnly? preferredTime = null;
            if (!string.IsNullOrEmpty(request.AanvangsTijd) && TimeOnly.TryParse(request.AanvangsTijd, out var parsed))
                preferredTime = parsed;

            // Dagdeelfilter toepassen
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

            // Stap 7: Eerst voorkeurstijd proberen (directe controle, voor volledige scan)
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

            // Alternatieve slots zoeken
            var candidates = FindAllSlots(availableFields, occupations, velden, allTeamRules, teamRules,
                                          veldFractie, duurMinuten, dagdeelVan, dagdeelTot, sunset);

            // Stap 8: Beste beschikbaar slot vinden
            if (candidates.Count > 0)
            {
                // Sorteren op nabijheid voorkeurstijd, of op planningsvoorkeur
                var ordered = preferredTime.HasValue
                    ? candidates.OrderBy(c => Math.Abs(c.AanvangsTijd.ToTimeSpan().TotalMinutes - preferredTime.Value.ToTimeSpan().TotalMinutes))
                    : candidates.OrderBy(c => c.AanvangsTijd.ToTimeSpan().TotalMinutes);

                var best = ordered.First();
                var alternatives = ordered.Skip(1).Take(3).ToList();

                if (preferredTime.HasValue)
                {
                    // Voorkeurstijd niet exact beschikbaar — beste als alternatief
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

            // Elk veld proberen in voorkeursvolgorde (veld 1-4 voor veld 5)
            foreach (var field in availableFields.OrderBy(f => f.VeldNummer == 5 ? 1 : 0))
            {
                // Controleer binnen beschikbaarheidsvenster
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

                // Effectief tijdvenster voor dit veld bepalen
                var windowStart = dagdeelVan < field.BeschikbaarVanaf ? field.BeschikbaarVanaf : dagdeelVan;
                var windowEnd = dagdeelTot > field.BeschikbaarTot ? field.BeschikbaarTot : dagdeelTot;

                // Scannen in stappen van 5 minuten
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
                        // Vooruitspringen voorbij dit slot om overlappende kandidaten te vermijden
                        time = time.AddMinutes(duurMinuten + StandardBufferMinutes - 5); // -5 omdat de lus 5 toevoegt
                    }
                }
            }

            // Sorteren: veld 1-4 prefereren boven veld 5
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
            // Buffervereisten voor het aanvragende team ophalen
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
                // Controleer of tijdvensters overlappen
                bool overlaps = occ.AanvangsTijd < bufferedEnd && occ.EindTijd > bufferedStart;
                if (!overlaps) continue;

                // Voor deelveld-wedstrijden, capaciteit controleren tijdens de wedstrijd (niet buffertijd)
                if (veldFractie < 1.0m && occ.VeldDeelGebruik < 1.0m)
                {
                    // Alleen daadwerkelijke overlap controleren (geen buffers) voor deelveld-capaciteit
                    bool actualOverlap = occ.AanvangsTijd < end && occ.EindTijd > start;
                    if (actualOverlap)
                    {
                        // Totale capaciteit controleren voor dit tijdslot
                        decimal usedCapacity = fieldOccupations
                            .Where(o => o.AanvangsTijd < end && o.EindTijd > start)
                            .Sum(o => o.VeldDeelGebruik);
                        if (usedCapacity + veldFractie > 1.0m)
                            return false;
                        continue; // deelveld delen is prima als capaciteit het toelaat
                    }
                    continue;
                }

                // Voor heel-veld wedstrijden, of als bestaande wedstrijd heel veld is: conflict
                if (veldFractie >= 1.0m || occ.VeldDeelGebruik >= 1.0m)
                    return false;

                // Controleer ook bufferregels van bestaand team
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
    }
}
