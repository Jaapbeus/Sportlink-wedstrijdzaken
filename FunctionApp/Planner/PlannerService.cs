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
        private static readonly System.Globalization.CultureInfo NL = new("nl-NL");

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
                    response.Reden = $"{request.TeamNaam} heeft al een wedstrijd op {date.ToString("d MMMM", NL)}: " +
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
                    _ => date.ToString("dddd d MMMM", NL)
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
                response.Reden = $"Geen beschikbaar veld gevonden op {date.ToString("dddd d MMMM", NL)}.";
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
                // Aanvangstijd moet binnen het dagdeel vallen, eindtijd binnen de veldbeschikbaarheid
                for (var time = windowStart; time < windowEnd && time.AddMinutes(duurMinuten) <= field.BeschikbaarTot; time = time.AddMinutes(5))
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

            foreach (var occ in fieldOccupations)
            {
                // Controleer bufferregels van bestaand team
                int occBufVoor = StandardBufferMinutes;
                int occBufNa = StandardBufferMinutes;
                if (!string.IsNullOrEmpty(occ.TeamNaam) && allTeamRules.TryGetValue(occ.TeamNaam, out var existingRules))
                {
                    foreach (var rule in existingRules)
                    {
                        if (rule.RegelType == "BufferVoor" && rule.WaardeMinuten.HasValue)
                            occBufVoor = Math.Max(occBufVoor, rule.WaardeMinuten.Value);
                        if (rule.RegelType == "BufferNa" && rule.WaardeMinuten.HasValue)
                            occBufNa = Math.Max(occBufNa, rule.WaardeMinuten.Value);
                    }
                }

                // Daadwerkelijke overlap: spelen beide wedstrijden tegelijkertijd?
                bool gelijktijdig = occ.AanvangsTijd < end && occ.EindTijd > start;

                if (gelijktijdig)
                {
                    // Deelveld-sharing: beide wedstrijden gebruiken een deel van het veld
                    // en overlappen in de tijd. Controleer of de totale capaciteit past.
                    // Alleen toegestaan als GEEN van beide een heel veld (1.00) gebruikt.
                    if (veldFractie < 1.0m && occ.VeldDeelGebruik < 1.0m)
                    {
                        // Controleer totale capaciteit op het drukste moment:
                        // alle wedstrijden die overlappen met het nieuwe tijdvenster
                        decimal maxCapaciteitInGebruik = 0;
                        // Scan per minuut van start tot end om het drukste moment te vinden
                        for (var checkTime = start; checkTime < end; checkTime = checkTime.AddMinutes(5))
                        {
                            var checkEnd = checkTime.AddMinutes(5);
                            decimal capaciteitOpMoment = fieldOccupations
                                .Where(o => o.VeldDeelGebruik < 1.0m && o.AanvangsTijd < checkEnd && o.EindTijd > checkTime)
                                .Sum(o => o.VeldDeelGebruik);
                            maxCapaciteitInGebruik = Math.Max(maxCapaciteitInGebruik, capaciteitOpMoment);
                        }
                        if (maxCapaciteitInGebruik + veldFractie > 1.0m)
                            return false;
                        // Deelveld delen is prima als capaciteit het toelaat
                        continue;
                    }
                    // Overlap met heel-veld wedstrijd: altijd conflict
                    return false;
                }

                // Niet gelijktijdig: controleer buffer (10 min standaard, of teamspecifiek)
                // Nieuwe wedstrijd mag niet in de bufferzone van bestaande wedstrijd vallen
                var occBeschermdeStart = occ.AanvangsTijd.AddMinutes(-occBufVoor);
                var occBeschermdeEinde = occ.EindTijd.AddMinutes(occBufNa);
                if (start < occBeschermdeEinde && end > occBeschermdeStart)
                    return false;

                // Bestaande wedstrijd mag niet in de bufferzone van nieuwe wedstrijd vallen
                var nieuwBeschermdeStart = start.AddMinutes(-bufferVoor);
                var nieuwBeschermdeEinde = end.AddMinutes(bufferNa);
                if (occ.AanvangsTijd < nieuwBeschermdeEinde && occ.EindTijd > nieuwBeschermdeStart)
                    return false;
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
                response.Reden = $"Geen beschikbare vensters op {date.ToString("dddd d MMMM", NL)}.";
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
                    $"{date.ToString("dddd", NL)}: alleen veld 5 beschikbaar (veld 1-4 training).");
            }
        }

        private class CandidateSlot
        {
            public int VeldNummer { get; set; }
            public TimeOnly AanvangsTijd { get; set; }
            public TimeOnly EindTijd { get; set; }
            public decimal VeldFractie { get; set; }
        }

        // ── Optimalisatie logica ──

        public static async Task<OptimaliseerResponse> OptimaliseerAsync(
            OptimaliseerRequest request, ILogger log)
        {
            var nl = new System.Globalization.CultureInfo("nl-NL");
            var response = new OptimaliseerResponse { Datum = request.Datum };

            if (!DateOnly.TryParse(request.Datum, out var date))
            {
                response.HtmlPlanner = "<p>Ongeldige datum</p>";
                return response;
            }

            // Data laden
            var occupations = await PlannerDataAccess.GetFieldOccupationsAsync(date);
            var velden = await PlannerDataAccess.GetVeldenAsync();
            var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date);

            // Dedup bezettingen
            var bezettingen = occupations
                .GroupBy(o => $"{o.VeldNummer}_{o.AanvangsTijd:HH:mm}_{o.Wedstrijd?.Trim()}")
                .Select(g => g.First())
                .OrderBy(o => o.AanvangsTijd).ThenBy(o => o.VeldNummer)
                .ToList();

            // Huidige eindtijd bepalen
            var laatsteWedstrijd = bezettingen.OrderByDescending(o => o.EindTijd).FirstOrDefault();
            response.HuidigeEindtijd = laatsteWedstrijd?.EindTijd.ToString("HH:mm") ?? "—";

            // Team-regels laden voor vaste wedstrijden
            var allTeamRules = new Dictionary<string, List<TeamRegel>>();
            foreach (var teamNaam in bezettingen.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!).Distinct())
            {
                allTeamRules[teamNaam] = await PlannerDataAccess.GetTeamRulesAsync(teamNaam);
            }

            // Vaste wedstrijden identificeren (VRC 1 of wedstrijden met speciale regels)
            var vasteWedstrijden = new HashSet<string>();
            foreach (var b in bezettingen)
            {
                if (b.TeamNaam != null && allTeamRules.TryGetValue(b.TeamNaam, out var regels) && regels.Count > 0)
                    vasteWedstrijden.Add($"{b.VeldNummer}_{b.AanvangsTijd:HH:mm}_{b.Wedstrijd?.Trim()}");
            }

            var suggesties = new List<OptimalisatieSuggestie>();
            var doel = request.Doel?.ToLowerInvariant() ?? "";

            switch (doel)
            {
                case "veld5-ontlasten":
                    suggesties = OptimaliseerVeld5Ontlasten(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules);
                    break;
                case "strakker-plannen":
                    suggesties = OptimaliseerStrakkerPlannen(bezettingen, velden, availableFields, vasteWedstrijden);
                    break;
                default:
                    // Standaard: beide combineren — eerst veld 5 ontlasten, dan strakker plannen
                    suggesties = OptimaliseerVeld5Ontlasten(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules);
                    var strakkerSuggesties = OptimaliseerStrakkerPlannen(bezettingen, velden, availableFields, vasteWedstrijden);
                    // Voeg strakker-suggesties toe die niet al in veld5-suggesties zitten
                    foreach (var s in strakkerSuggesties)
                    {
                        if (!suggesties.Any(bestaand => bestaand.Wedstrijd == s.Wedstrijd))
                            suggesties.Add(s);
                    }
                    break;
            }

            response.Suggesties = suggesties;
            response.AantalVerplaatsingen = suggesties.Count;
            response.AantalVanVeld5Verplaatst = suggesties.Count(s => s.HuidigVeldNummer == 5);

            // HTML genereren
            response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(date, bezettingen, suggesties, velden, request.Doel ?? "veld5-ontlasten");

            return response;
        }

        private static List<OptimalisatieSuggestie> OptimaliseerVeld5Ontlasten(
            List<BestaandeWedstrijd> bezettingen,
            List<VeldInfo> velden,
            List<VeldBeschikbaarheidInfo> beschikbareVelden,
            HashSet<string> vasteWedstrijden,
            Dictionary<string, List<TeamRegel>> allTeamRules)
        {
            var suggesties = new List<OptimalisatieSuggestie>();

            // Wedstrijden op veld 5 die verplaatst mogen worden
            var veld5Wedstrijden = bezettingen
                .Where(b => b.VeldNummer == 5)
                .Where(b => !vasteWedstrijden.Contains($"{b.VeldNummer}_{b.AanvangsTijd:HH:mm}_{b.Wedstrijd?.Trim()}"))
                .OrderBy(b => b.AanvangsTijd)
                .ToList();

            // Werk met een kopie van bezettingen die we aanpassen naarmate we suggesties doen
            var werkBezettingen = bezettingen.ToList();

            foreach (var wedstrijd in veld5Wedstrijden)
            {
                int duur = (int)(wedstrijd.EindTijd - wedstrijd.AanvangsTijd).TotalMinutes;
                decimal fractie = wedstrijd.VeldDeelGebruik;

                // Zoek een plek op veld 1-4 die EERDER is dan huidige tijd op veld 5
                // Alleen verplaatsen als het een verbetering is (eerder of gelijktijdig op kunstgras)
                CandidateSlot? besteSlot = null;
                foreach (var veldBesch in beschikbareVelden.Where(f => f.VeldNummer <= 4).OrderBy(f => f.VeldNummer))
                {
                    var veldBezetting = werkBezettingen.Where(b => b.VeldNummer == veldBesch.VeldNummer).ToList();

                    for (var tijd = veldBesch.BeschikbaarVanaf; tijd.AddMinutes(duur) <= veldBesch.BeschikbaarTot; tijd = tijd.AddMinutes(5))
                    {
                        // Alleen accepteren als het niet later is dan de huidige tijd op veld 5
                        if (tijd > wedstrijd.AanvangsTijd) break;

                        var eindTijd = tijd.AddMinutes(duur);
                        if (CanFitMatch(tijd, eindTijd, fractie, veldBesch.VeldNummer,
                                        veldBezetting, allTeamRules, new List<TeamRegel>()))
                        {
                            besteSlot = new CandidateSlot
                            {
                                VeldNummer = veldBesch.VeldNummer,
                                AanvangsTijd = tijd,
                                EindTijd = eindTijd
                            };
                            break;
                        }
                    }
                    if (besteSlot != null) break;
                }

                if (besteSlot != null)
                {
                    var veldNaam = velden.FirstOrDefault(v => v.VeldNummer == besteSlot.VeldNummer)?.VeldNaam ?? $"veld {besteSlot.VeldNummer}";
                    suggesties.Add(new OptimalisatieSuggestie
                    {
                        Wedstrijd = wedstrijd.Wedstrijd?.Trim() ?? "",
                        HuidigVeldNummer = 5,
                        HuidigVeld = "veld 5",
                        HuidigeTijd = wedstrijd.AanvangsTijd.ToString("HH:mm"),
                        NieuwVeldNummer = besteSlot.VeldNummer,
                        NieuwVeld = veldNaam,
                        NieuweTijd = besteSlot.AanvangsTijd.ToString("HH:mm"),
                        Reden = $"Verplaats van veld 5 (geen kunstlicht) naar {veldNaam}"
                    });

                    // Werkbezetting bijwerken zodat volgende suggesties deze plek als bezet zien
                    werkBezettingen.Remove(wedstrijd);
                    werkBezettingen.Add(new BestaandeWedstrijd
                    {
                        Datum = wedstrijd.Datum,
                        AanvangsTijd = besteSlot.AanvangsTijd,
                        EindTijd = besteSlot.EindTijd,
                        VeldNummer = besteSlot.VeldNummer,
                        VeldDeelGebruik = fractie,
                        TeamNaam = wedstrijd.TeamNaam,
                        Wedstrijd = wedstrijd.Wedstrijd,
                        Bron = "Suggestie"
                    });
                }
            }

            return suggesties;
        }

        private static List<OptimalisatieSuggestie> OptimaliseerStrakkerPlannen(
            List<BestaandeWedstrijd> bezettingen,
            List<VeldInfo> velden,
            List<VeldBeschikbaarheidInfo> beschikbareVelden,
            HashSet<string> vasteWedstrijden)
        {
            var suggesties = new List<OptimalisatieSuggestie>();

            // Per veld: zoek wedstrijden die naar voren geschoven kunnen worden
            foreach (var veldBesch in beschikbareVelden.OrderBy(f => f.VeldNummer))
            {
                var veldBezetting = bezettingen
                    .Where(b => b.VeldNummer == veldBesch.VeldNummer && b.VeldDeelGebruik >= 1.0m)
                    .OrderBy(b => b.AanvangsTijd)
                    .ToList();

                var vorigEinde = veldBesch.BeschikbaarVanaf;
                foreach (var wedstrijd in veldBezetting)
                {
                    string key = $"{wedstrijd.VeldNummer}_{wedstrijd.AanvangsTijd:HH:mm}_{wedstrijd.Wedstrijd?.Trim()}";
                    if (vasteWedstrijden.Contains(key))
                    {
                        vorigEinde = wedstrijd.EindTijd.AddMinutes(StandardBufferMinutes);
                        continue;
                    }

                    var vroegstMogelijk = vorigEinde.AddMinutes(StandardBufferMinutes);
                    int duur = (int)(wedstrijd.EindTijd - wedstrijd.AanvangsTijd).TotalMinutes;

                    // Verschil tussen huidige aanvangstijd en vroegst mogelijke
                    var verschilMinuten = (wedstrijd.AanvangsTijd - vroegstMogelijk).TotalMinutes;
                    if (verschilMinuten >= 15) // minstens 15 minuten te winnen
                    {
                        var veldNaam = velden.FirstOrDefault(v => v.VeldNummer == wedstrijd.VeldNummer)?.VeldNaam ?? $"veld {wedstrijd.VeldNummer}";
                        suggesties.Add(new OptimalisatieSuggestie
                        {
                            Wedstrijd = wedstrijd.Wedstrijd?.Trim() ?? "",
                            HuidigVeldNummer = wedstrijd.VeldNummer,
                            HuidigVeld = veldNaam,
                            HuidigeTijd = wedstrijd.AanvangsTijd.ToString("HH:mm"),
                            NieuwVeldNummer = wedstrijd.VeldNummer,
                            NieuwVeld = veldNaam,
                            NieuweTijd = vroegstMogelijk.ToString("HH:mm"),
                            Reden = $"Naar voren schuiven ({(int)verschilMinuten} min eerder)"
                        });
                        vorigEinde = vroegstMogelijk.AddMinutes(duur);
                    }
                    else
                    {
                        vorigEinde = wedstrijd.EindTijd;
                    }
                }
            }

            return suggesties;
        }

        // ── Herplan (herplannen) logica ──

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
                response.Reden = $"Geen alternatieve tijdsloten gevonden op {date.ToString("dddd d MMMM", NL)}.";
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
                waarschuwingen.Add($"{date.ToString("dddd", NL)}: alleen veld 5 beschikbaar (veld 1-4 training).");
            }
        }
    }
}
