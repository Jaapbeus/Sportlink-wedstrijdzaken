using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner
{
    public static class PlannerService
    {
        private const int StandardBufferMinutes = 15; // Standaard 15 min, kan per club verlaagd worden via dbo.AppSettings
        private const double MaxBezettingsPercentageVoorOverslaan = 50.0;

        /// <summary>
        /// Rond aanvangstijd naar boven af op 5 minuten.
        /// Bijv. 09:58 → 10:00, 13:04 → 13:05, 10:30 → 10:30
        /// </summary>
        private static TimeOnly RondAfOp5Min(TimeOnly tijd)
        {
            int minuten = tijd.Hour * 60 + tijd.Minute;
            int rest = minuten % 5;
            if (rest > 0) minuten += (5 - rest);
            return new TimeOnly(minuten / 60, minuten % 60);
        }
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

            // Modus 2: geen specifieke aanvangstijd — beschikbare vensters teruggeven
            if (string.IsNullOrEmpty(request.AanvangsTijd))
            {
                var windowsResponse = BuildWindowsResponse(date, availableFields, occupations, velden, sunset, request.Dagdeel);

                // Filter vensters op minimale duur als de wedstrijdduur bekend is
                if (windowsResponse.BeschikbareVensters != null && duurMinuten > 0)
                {
                    windowsResponse.BeschikbareVensters = windowsResponse.BeschikbareVensters
                        .Where(w => w.MaxDuurMinuten >= duurMinuten)
                        .ToList();
                    windowsResponse.Beschikbaar = windowsResponse.BeschikbareVensters.Count > 0;
                    if (!windowsResponse.Beschikbaar)
                        windowsResponse.Reden = $"Geen venster van minimaal {duurMinuten} minuten beschikbaar op {date.ToString("dddd d MMMM", NL)}.";
                }

                return windowsResponse;
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

            // Vensters altijd berekenen (voor contextinformatie in email responses)
            var venstersResponse = BuildWindowsResponse(date, availableFields, occupations, velden, sunset, request.Dagdeel);
            if (duurMinuten > 0 && venstersResponse.BeschikbareVensters != null)
            {
                venstersResponse.BeschikbareVensters = venstersResponse.BeschikbareVensters
                    .Where(w => w.MaxDuurMinuten >= duurMinuten).ToList();
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
                    response.BeschikbareVensters = venstersResponse.BeschikbareVensters;
                    AddSunsetWarning(response, exactMatch, sunset, velden);
                    AddNabijeWedstrijdWaarschuwing(response, exactMatch, duurMinuten, occupations, velden);
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
                    // Voorkeurstijd niet exact beschikbaar — toon vensters als alternatief
                    response.Reden = $"Gewenste tijd {preferredTime.Value:HH:mm} is niet beschikbaar.";
                    response.Alternatieven = alternatives.Prepend(best)
                        .Select(c => ToSlotToewijzing(date, c, duurMinuten, velden)).Take(3).ToList();
                    response.BeschikbareVensters = venstersResponse.BeschikbareVensters;
                    AddWeekdayWarning(response, date);
                }
                else
                {
                    response.Beschikbaar = true;
                    response.Toewijzing = ToSlotToewijzing(date, best, duurMinuten, velden);
                    AddNabijeWedstrijdWaarschuwing(response, best, duurMinuten, occupations, velden);
                    response.Alternatieven = alternatives
                        .Select(c => ToSlotToewijzing(date, c, duurMinuten, velden)).ToList();
                    response.BeschikbareVensters = venstersResponse.BeschikbareVensters;
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

        /// <summary>
        /// Latest-fit scan per veld: zoekt het slot met de uiterst mogelijke aanvangstijd
        /// die nog vóór <paramref name="upperBound"/> eindigt (eindtijd ≤ upperBound). Anders dan
        /// FindAllSlots, dat van vroeg naar laat scant en het vroegste slot per gap pakt, scant
        /// dit van laat naar vroeg in 5-minuten stappen — geschikt voor 'vervroegen' waarbij we
        /// zo dicht mogelijk tegen de oorspronkelijke aanvangstijd of een volgende bezetting
        /// willen plannen. Retourneert maximaal één slot per veld.
        /// </summary>
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
                var fieldOccupations = occupations.Where(o => o.VeldNummer == field.VeldNummer).ToList();
                var effWindowStart = windowStart < field.BeschikbaarVanaf ? field.BeschikbaarVanaf : windowStart;
                var effUpper = upperBound > field.BeschikbaarTot ? field.BeschikbaarTot : upperBound;

                // Eerste bezetting op dit veld die start binnen [effWindowStart, effUpper).
                // Onze eindtijd moet daarvóór vallen (buffer wordt door CanFitMatch afgedwongen).
                var nextOcc = fieldOccupations
                    .Where(o => o.AanvangsTijd >= effWindowStart && o.AanvangsTijd < effUpper)
                    .OrderBy(o => o.AanvangsTijd)
                    .FirstOrDefault();
                var hardEnd = nextOcc != null && nextOcc.AanvangsTijd < effUpper ? nextOcc.AanvangsTijd : effUpper;

                // Scan achterwaarts: laatste startwaarde waarbij start + duur ≤ hardEnd, dan terug
                // in stappen van 5 min totdat een geldige fit gevonden wordt.
                var latestStartCandidate = hardEnd.AddMinutes(-duurMinuten);
                if (latestStartCandidate < effWindowStart) continue;

                for (var time = latestStartCandidate; time >= effWindowStart; time = time.AddMinutes(-5))
                {
                    var endTime = time.AddMinutes(duurMinuten);
                    if (CanFitMatch(time, endTime, veldFractie, field.VeldNummer,
                                    fieldOccupations, allTeamRules, requestingTeamRules))
                    {
                        result.Add(new CandidateSlot
                        {
                            VeldNummer = field.VeldNummer,
                            AanvangsTijd = time,
                            EindTijd = endTime
                        });
                        break;
                    }
                }
            }

            return result;
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

        /// <summary>
        /// Waarschuw als er een wedstrijd direct voor of na het toegewezen slot zit
        /// met minimale buffer (precies op de grens).
        /// </summary>
        private static void AddNabijeWedstrijdWaarschuwing(
            CheckAvailabilityResponse response, CandidateSlot slot, int duurMinuten,
            List<BestaandeWedstrijd> occupations, List<VeldInfo> velden)
        {
            var slotStart = slot.AanvangsTijd;
            var slotEinde = slot.AanvangsTijd.AddMinutes(duurMinuten);
            var veldOccupations = occupations.Where(o => o.VeldNummer == slot.VeldNummer).ToList();

            // Zoek wedstrijd direct erna (binnen 20 min na einde + buffer)
            var directErna = veldOccupations
                .Where(o => o.AanvangsTijd >= slotEinde && o.AanvangsTijd <= slotEinde.AddMinutes(StandardBufferMinutes + 5))
                .OrderBy(o => o.AanvangsTijd)
                .FirstOrDefault();

            if (directErna != null)
            {
                int marge = (int)(directErna.AanvangsTijd - slotEinde).TotalMinutes;
                var wedstrijdNaam = directErna.Wedstrijd?.Trim() ?? "";
                response.Waarschuwingen.Add(
                    $"Let op: {wedstrijdNaam} begint om {directErna.AanvangsTijd:HH:mm} op hetzelfde veld ({marge} min na einde).");
            }

            // Zoek wedstrijd direct ervoor (binnen 20 min voor start)
            var directErvoor = veldOccupations
                .Where(o => o.EindTijd <= slotStart && o.EindTijd >= slotStart.AddMinutes(-(StandardBufferMinutes + 5)))
                .OrderByDescending(o => o.EindTijd)
                .FirstOrDefault();

            if (directErvoor != null)
            {
                int marge = (int)(slotStart - directErvoor.EindTijd).TotalMinutes;
                var wedstrijdNaam = directErvoor.Wedstrijd?.Trim() ?? "";
                response.Waarschuwingen.Add(
                    $"Let op: {wedstrijdNaam} eindigt om {directErvoor.EindTijd:HH:mm} op hetzelfde veld ({marge} min voor aanvang).");
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

        // ── Doordeweekse beschikbaarheid ──

        public static async Task<DoordeweeksBeschikbaarResponse> CheckDoordeweeksBeschikbaarAsync(
            DoordeweeksBeschikbaarRequest request, ILogger log)
        {
            var response = new DoordeweeksBeschikbaarResponse { DagFilter = request.DagFilter };

            var seizoenEinde = await PlannerDataAccess.GetSeasonEndDateAsync()
                ?? DateOnly.FromDateTime(DateTime.Today.AddMonths(6));
            response.SeizoenEinde = seizoenEinde.ToString("yyyy-MM-dd");

            // Gewenste duur afleiden
            int? gewensteDuur = request.DuurMinuten;
            if (!gewensteDuur.HasValue && !string.IsNullOrEmpty(request.LeeftijdsCategorie))
            {
                var speeltijd = await PlannerDataAccess.GetSpeeltijdAsync(request.LeeftijdsCategorie);
                if (speeltijd != null) gewensteDuur = speeltijd.WedstrijdTotaal;
            }

            // Dagfilter vertalen
            DayOfWeek? dagFilter = request.DagFilter?.ToLowerInvariant() switch
            {
                "maandag" => DayOfWeek.Monday,
                "dinsdag" => DayOfWeek.Tuesday,
                "woensdag" => DayOfWeek.Wednesday,
                "donderdag" => DayOfWeek.Thursday,
                _ => null
            };

            var startDate = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
            for (var date = startDate; date <= seizoenEinde; date = date.AddDays(1))
            {
                if (date.DayOfWeek < DayOfWeek.Monday || date.DayOfWeek > DayOfWeek.Thursday)
                    continue;
                if (dagFilter.HasValue && date.DayOfWeek != dagFilter.Value)
                    continue;

                var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date);
                if (availableFields.Count == 0) continue;

                TimeOnly? sunset = await PlannerDataAccess.GetSunsetAsync(date)
                    ?? SunsetCalculator.GetSunset(date);
                string sunsetStr = sunset.HasValue ? sunset.Value.ToString("HH:mm") : "";

                foreach (var field in availableFields)
                {
                    if (field.GebruikZonsondergang && sunset.HasValue && sunset.Value < field.BeschikbaarTot)
                        field.BeschikbaarTot = sunset.Value;
                }

                var occupations = await PlannerDataAccess.GetFieldOccupationsAsync(date);

                foreach (var field in availableFields)
                {
                    var fieldOccs = occupations
                        .Where(o => o.VeldNummer == field.VeldNummer)
                        .OrderBy(o => o.AanvangsTijd).ToList();

                    // Bereken vrije vensters (zelfde logica als BuildWindowsResponse)
                    var gapStart = field.BeschikbaarVanaf;
                    void AddVenster(TimeOnly van, TimeOnly tot)
                    {
                        int maxDuur = (int)(tot.ToTimeSpan() - van.ToTimeSpan()).TotalMinutes;
                        if (maxDuur < 30) return;
                        response.BeschikbareDatums.Add(new DoordeweekseDatum
                        {
                            Datum = date.ToString("yyyy-MM-dd"),
                            DagVanWeek = date.ToString("dddd", NL),
                            BeschikbaarVan = van.ToString("HH:mm"),
                            BeschikbaarTot = tot.ToString("HH:mm"),
                            Zonsondergang = sunsetStr,
                            MaxDuurMinuten = maxDuur,
                            PastGewensteDuur = !gewensteDuur.HasValue || maxDuur >= gewensteDuur.Value,
                            GeplandeWedstrijden = fieldOccs.Select(o => new BestaandeWedstrijdSamenvatting
                            {
                                Wedstrijd = o.Wedstrijd?.Trim() ?? "",
                                AanvangsTijd = o.AanvangsTijd.ToString("HH:mm"),
                                EindTijd = o.EindTijd.ToString("HH:mm")
                            }).ToList()
                        });
                    }

                    foreach (var occ in fieldOccs)
                    {
                        var occStart = occ.AanvangsTijd.AddMinutes(-StandardBufferMinutes);
                        if (occStart > gapStart)
                            AddVenster(gapStart, occStart);
                        gapStart = occ.EindTijd.AddMinutes(StandardBufferMinutes);
                    }
                    if (gapStart < field.BeschikbaarTot)
                        AddVenster(gapStart, field.BeschikbaarTot);
                }
            }

            response.AantalBeschikbaar = response.BeschikbareDatums.Count;
            return response;
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

            // Buffer uit request of standaard
            int bufferMin = request.BufferMinuten ?? StandardBufferMinutes;

            // Capaciteitsberekening: check of optimalisatie nodig is
            var capaciteit = BerekenCapaciteit(bezettingen, availableFields);
            response.CapaciteitOverzicht = capaciteit;

            if (capaciteit.AantalWedstrijdenOpVeld5 == 0 && capaciteit.BezettingsPercentage < MaxBezettingsPercentageVoorOverslaan)
            {
                response.VoldoendeRuimte = true;
                response.VoldoendeRuimteMelding =
                    $"Voldoende ruimte op {date.ToString("dddd d MMMM", nl)}: " +
                    $"geen wedstrijden op veld 5, {capaciteit.BezettingsPercentage:F0}% bezet " +
                    $"({capaciteit.AantalLegeVelden} veld(en) ongebruikt). Geen optimalisatie nodig.";
                response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(
                    date, bezettingen, new List<OptimalisatieSuggestie>(), velden,
                    request.Doel ?? "optimaliseren");
                return response;
            }

            var suggesties = new List<OptimalisatieSuggestie>();
            var doel = request.Doel?.ToLowerInvariant() ?? "";

            switch (doel)
            {
                case "veld5-ontlasten":
                    suggesties = OptimaliseerVeld5Ontlasten(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                    break;
                case "strakker-plannen":
                    suggesties = OptimaliseerStrakkerPlannen(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                    break;
                default:
                    suggesties = OptimaliseerVeld5Ontlasten(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                    var strakkerSuggesties = OptimaliseerStrakkerPlannen(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                    // Voeg strakker-suggesties toe die niet al in veld5-suggesties zitten
                    foreach (var s in strakkerSuggesties)
                    {
                        if (!suggesties.Any(bestaand => bestaand.Wedstrijd == s.Wedstrijd))
                            suggesties.Add(s);
                    }
                    break;
            }

            // Stap 2: Dynamische buffer — verdeel resterende ruimte als extra buffer
            TimeOnly gewensteEindtijd = new(16, 15);
            if (!string.IsNullOrEmpty(request.GewensteEindtijd) && TimeOnly.TryParse(request.GewensteEindtijd, out var parsed))
                gewensteEindtijd = parsed;

            suggesties = VerdeelExtraBuffer(suggesties, bezettingen, availableFields, velden, vasteWedstrijden, allTeamRules, gewensteEindtijd);

            response.Suggesties = suggesties;
            response.AantalVerplaatsingen = suggesties.Count;
            response.AantalVanVeld5Verplaatst = suggesties.Count(s => s.HuidigVeldNummer == 5);

            // HTML genereren
            response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(date, bezettingen, suggesties, velden, request.Doel ?? "optimaliseren");

            return response;
        }

        private static VeldCapaciteitInfo BerekenCapaciteit(
            List<BestaandeWedstrijd> bezettingen,
            List<VeldBeschikbaarheidInfo> beschikbareVelden)
        {
            int totaalBeschikbaar = 0;
            foreach (var veld in beschikbareVelden)
                totaalBeschikbaar += (int)(veld.BeschikbaarTot.ToTimeSpan() - veld.BeschikbaarVanaf.ToTimeSpan()).TotalMinutes;

            int totaalBezet = 0;
            foreach (var b in bezettingen)
                totaalBezet += (int)((b.EindTijd.ToTimeSpan() - b.AanvangsTijd.ToTimeSpan()).TotalMinutes * (double)b.VeldDeelGebruik);

            var bezetteVelden = bezettingen.Select(b => b.VeldNummer).Distinct().ToHashSet();
            int aantalLegeVelden = beschikbareVelden.Count(v => !bezetteVelden.Contains(v.VeldNummer));

            return new VeldCapaciteitInfo
            {
                TotaalBeschikbareMinuten = totaalBeschikbaar,
                TotaalBezettMinuten = totaalBezet,
                BezettingsPercentage = totaalBeschikbaar > 0
                    ? (double)totaalBezet / totaalBeschikbaar * 100.0 : 0,
                AantalWedstrijdenOpVeld5 = bezettingen.Count(b => b.VeldNummer == 5),
                AantalLegeVelden = aantalLegeVelden
            };
        }

        private static List<OptimalisatieSuggestie> OptimaliseerVeld5Ontlasten(
            List<BestaandeWedstrijd> bezettingen,
            List<VeldInfo> velden,
            List<VeldBeschikbaarheidInfo> beschikbareVelden,
            HashSet<string> vasteWedstrijden,
            Dictionary<string, List<TeamRegel>> allTeamRules,
            int bufferMin = 15)
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
                        NieuweTijd = RondAfOp5Min(besteSlot.AanvangsTijd).ToString("HH:mm"),
                        Reden = $"Verplaats van veld 5 (geen kunstlicht) naar {veldNaam}"
                    });

                    // Werkbezetting bijwerken zodat volgende suggesties deze plek als bezet zien
                    werkBezettingen.Remove(wedstrijd);
                    var afgerondeTijd = RondAfOp5Min(besteSlot.AanvangsTijd);
                    werkBezettingen.Add(new BestaandeWedstrijd
                    {
                        Datum = wedstrijd.Datum,
                        AanvangsTijd = afgerondeTijd,
                        EindTijd = afgerondeTijd.AddMinutes(duur),
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
            HashSet<string> vasteWedstrijden,
            Dictionary<string, List<TeamRegel>> allTeamRules,
            int bufferMin = 15)
        {
            var suggesties = new List<OptimalisatieSuggestie>();

            // Globale werkbezetting over ALLE velden — wordt bijgewerkt per suggestie
            // zodat kettingreacties correct werken (als A naar voren gaat, kan B ook)
            var werkBezetting = bezettingen.ToList();

            // Groepeer wedstrijden in "blokken" per veld+starttijd
            // Een blok = alle wedstrijden die tegelijk op hetzelfde veld starten
            // (bijv. 2x half veld of 4x kwart veld)
            var blokken = bezettingen
                .GroupBy(b => $"{b.VeldNummer}_{b.AanvangsTijd:HH:mm}")
                .Select(g => new {
                    VeldNummer = g.First().VeldNummer,
                    AanvangsTijd = g.First().AanvangsTijd,
                    EindTijd = g.Max(w => w.EindTijd),
                    Wedstrijden = g.GroupBy(w => w.Wedstrijd?.Trim()).Select(wg => wg.First()).ToList(),
                    IsVast = g.Any(w => vasteWedstrijden.Contains($"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}"))
                })
                .Where(b => !b.IsVast)
                .OrderBy(b => b.AanvangsTijd)
                .ThenBy(b => b.VeldNummer)
                .ToList();

            foreach (var blok in blokken)
            {
                // Duur = langste wedstrijd in het blok
                int duur = (int)(blok.EindTijd - blok.AanvangsTijd).TotalMinutes;
                // Totale veldfractie van het blok
                decimal blokFractie = blok.Wedstrijden.Sum(w => w.VeldDeelGebruik);
                // Gebruik de eerste wedstrijd als referentie
                var wedstrijd = blok.Wedstrijden.First();

                // Deelveld-blokken alleen op eigen veld schuiven, heel-veld op alle velden
                bool isDeelveldBlok = blokFractie > 0 && blokFractie <= 1.0m && blok.Wedstrijden.Count > 1;
                var teCheckenVelden = isDeelveldBlok
                    ? beschikbareVelden.Where(v => v.VeldNummer == blok.VeldNummer).ToList()
                    : beschikbareVelden.OrderBy(v => v.VeldNummer).ToList();

                TimeOnly? besteSlotTijd = null;
                int besteSlotVeld = blok.VeldNummer;

                foreach (var kandidaatVeld in teCheckenVelden)
                {
                    // Werkbezetting op dit veld, zonder alle wedstrijden uit dit blok
                    var kandidaatBezetting = werkBezetting
                        .Where(b => b.VeldNummer == kandidaatVeld.VeldNummer)
                        .Where(b => !blok.Wedstrijden.Any(bw =>
                            bw.VeldNummer == b.VeldNummer &&
                            bw.AanvangsTijd == b.AanvangsTijd &&
                            bw.Wedstrijd?.Trim() == b.Wedstrijd?.Trim()))
                        .ToList();

                    for (var tijd = kandidaatVeld.BeschikbaarVanaf; tijd.AddMinutes(duur) <= kandidaatVeld.BeschikbaarTot; tijd = tijd.AddMinutes(5))
                    {
                        if (tijd >= blok.AanvangsTijd) break;
                        var eindTijd = tijd.AddMinutes(duur);
                        // Voor deelveld-blokken: check of het hele blok past (alle fracties samen)
                        bool past = isDeelveldBlok
                            ? blok.Wedstrijden.All(bw => CanFitMatch(tijd, eindTijd, bw.VeldDeelGebruik, kandidaatVeld.VeldNummer,
                                kandidaatBezetting, allTeamRules, new List<TeamRegel>()))
                            : CanFitMatch(tijd, eindTijd, 1.0m, kandidaatVeld.VeldNummer,
                                kandidaatBezetting, allTeamRules, new List<TeamRegel>());
                        if (past)
                        {
                            if (besteSlotTijd == null || tijd < besteSlotTijd.Value)
                            {
                                besteSlotTijd = tijd;
                                besteSlotVeld = kandidaatVeld.VeldNummer;
                            }
                            break;
                        }
                    }
                }

                if (besteSlotTijd.HasValue)
                {
                    var verschilMinuten = (blok.AanvangsTijd - besteSlotTijd.Value).TotalMinutes;
                    if (verschilMinuten >= 15)
                    {
                        var huidigVeldNaam = velden.FirstOrDefault(v => v.VeldNummer == blok.VeldNummer)?.VeldNaam ?? $"veld {blok.VeldNummer}";
                        var nieuwVeldNaam = velden.FirstOrDefault(v => v.VeldNummer == besteSlotVeld)?.VeldNaam ?? $"veld {besteSlotVeld}";
                        var reden = besteSlotVeld == blok.VeldNummer
                            ? $"Naar voren schuiven ({(int)verschilMinuten} min eerder)"
                            : $"Verplaatsen naar {nieuwVeldNaam} ({(int)verschilMinuten} min eerder)";

                        // Voeg suggestie toe voor elke wedstrijd in het blok
                        foreach (var bw in blok.Wedstrijden)
                        {
                            suggesties.Add(new OptimalisatieSuggestie
                            {
                                Wedstrijd = bw.Wedstrijd?.Trim() ?? "",
                                HuidigVeldNummer = bw.VeldNummer,
                                HuidigVeld = huidigVeldNaam,
                                HuidigeTijd = bw.AanvangsTijd.ToString("HH:mm"),
                                NieuwVeldNummer = besteSlotVeld,
                                NieuwVeld = nieuwVeldNaam,
                                NieuweTijd = RondAfOp5Min(besteSlotTijd.Value).ToString("HH:mm"),
                                Reden = reden
                            });
                        }

                        // Globale werkbezetting bijwerken: verwijder alle wedstrijden uit het blok
                        foreach (var bw in blok.Wedstrijden)
                        {
                            werkBezetting.RemoveAll(b =>
                                b.VeldNummer == bw.VeldNummer &&
                                b.AanvangsTijd == bw.AanvangsTijd &&
                                b.Wedstrijd?.Trim() == bw.Wedstrijd?.Trim());
                        }
                        // Voeg verplaatste wedstrijden toe op nieuwe positie
                        var afgerond = RondAfOp5Min(besteSlotTijd.Value);
                        foreach (var bw in blok.Wedstrijden)
                        {
                            int bwDuur = (int)(bw.EindTijd - bw.AanvangsTijd).TotalMinutes;
                            werkBezetting.Add(new BestaandeWedstrijd
                            {
                                Datum = bw.Datum,
                                AanvangsTijd = afgerond,
                                EindTijd = afgerond.AddMinutes(bwDuur),
                                VeldNummer = besteSlotVeld,
                                VeldDeelGebruik = bw.VeldDeelGebruik,
                                TeamNaam = bw.TeamNaam,
                                Wedstrijd = bw.Wedstrijd,
                                Bron = "Suggestie"
                            });
                        }
                    }
                }
            }

            return suggesties;
        }

        /// <summary>
        /// Verdeel resterende ruimte als extra buffer tussen wedstrijden.
        /// Als de planning voor de gewenste eindtijd klaar is, wordt de
        /// overgebleven ruimte gelijkmatig verdeeld (max 30 min per buffer).
        /// </summary>
        private static List<OptimalisatieSuggestie> VerdeelExtraBuffer(
            List<OptimalisatieSuggestie> suggesties,
            List<BestaandeWedstrijd> origineleBezetting,
            List<VeldBeschikbaarheidInfo> beschikbareVelden,
            List<VeldInfo> velden,
            HashSet<string> vasteWedstrijden,
            Dictionary<string, List<TeamRegel>> allTeamRules,
            TimeOnly gewensteEindtijd)
        {
            const int maxBuffer = 30;

            // Bouw de nieuwe bezetting op basis van suggesties
            var nieuweBezetting = origineleBezetting
                .GroupBy(w => $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}")
                .Select(g => g.First()).ToList();

            foreach (var s in suggesties)
            {
                nieuweBezetting.RemoveAll(b =>
                    b.VeldNummer == s.HuidigVeldNummer &&
                    b.AanvangsTijd.ToString("HH:mm") == s.HuidigeTijd &&
                    b.Wedstrijd?.Trim() == s.Wedstrijd.Trim());
                TimeOnly.TryParse(s.NieuweTijd, out var nt);
                var orig = origineleBezetting.FirstOrDefault(b =>
                    b.VeldNummer == s.HuidigVeldNummer &&
                    b.AanvangsTijd.ToString("HH:mm") == s.HuidigeTijd &&
                    b.Wedstrijd?.Trim() == s.Wedstrijd.Trim());
                int duur = orig != null ? (int)(orig.EindTijd - orig.AanvangsTijd).TotalMinutes : 75;
                nieuweBezetting.Add(new BestaandeWedstrijd
                {
                    Datum = orig?.Datum ?? default, AanvangsTijd = nt, EindTijd = nt.AddMinutes(duur),
                    VeldNummer = s.NieuwVeldNummer, VeldDeelGebruik = orig?.VeldDeelGebruik ?? 1.0m,
                    TeamNaam = orig?.TeamNaam, Wedstrijd = orig?.Wedstrijd, Bron = "Suggestie"
                });
            }

            // Per veld: bereken of er ruimte is om buffers te vergroten
            foreach (var veldBesch in beschikbareVelden)
            {
                var veldWedstrijden = nieuweBezetting
                    .Where(b => b.VeldNummer == veldBesch.VeldNummer && b.VeldDeelGebruik >= 1.0m)
                    .OrderBy(b => b.AanvangsTijd).ToList();

                if (veldWedstrijden.Count < 2) continue;

                // Bereken eindtijd van de laatste wedstrijd op dit veld
                var laatsteEinde = veldWedstrijden.Max(w => w.EindTijd);
                if (laatsteEinde >= gewensteEindtijd) continue; // Geen ruimte over

                // Tel het aantal gaten (buffers) tussen heel-veld wedstrijden
                int aantalGaten = 0;
                for (int i = 1; i < veldWedstrijden.Count; i++)
                {
                    string key = $"{veldWedstrijden[i].VeldNummer}_{veldWedstrijden[i].AanvangsTijd:HH:mm}_{veldWedstrijden[i].Wedstrijd?.Trim()}";
                    if (!vasteWedstrijden.Contains(key)) aantalGaten++;
                }
                if (aantalGaten == 0) continue;

                // Beschikbare extra ruimte
                double extraMinuten = (gewensteEindtijd - laatsteEinde).TotalMinutes;
                int extraPerGat = Math.Min((int)(extraMinuten / aantalGaten), maxBuffer - StandardBufferMinutes);
                if (extraPerGat <= 0) continue;

                // Pas suggesties aan voor dit veld: schuif wedstrijden iets naar achteren
                int cumulatieveVertraging = 0;
                for (int i = 0; i < veldWedstrijden.Count; i++)
                {
                    if (i > 0)
                    {
                        string key = $"{veldWedstrijden[i].VeldNummer}_{veldWedstrijden[i].AanvangsTijd:HH:mm}_{veldWedstrijden[i].Wedstrijd?.Trim()}";
                        if (!vasteWedstrijden.Contains(key))
                            cumulatieveVertraging += extraPerGat;
                    }

                    if (cumulatieveVertraging > 0)
                    {
                        var wedstrijd = veldWedstrijden[i];
                        var nieuweTijd = wedstrijd.AanvangsTijd.AddMinutes(cumulatieveVertraging);

                        // Update de suggestie als die er al is
                        var bestaandeSuggestie = suggesties.FirstOrDefault(s =>
                            s.Wedstrijd.Trim() == wedstrijd.Wedstrijd?.Trim() &&
                            s.NieuwVeldNummer == wedstrijd.VeldNummer);
                        if (bestaandeSuggestie != null)
                        {
                            bestaandeSuggestie.NieuweTijd = RondAfOp5Min(nieuweTijd).ToString("HH:mm");
                        }
                        else
                        {
                            // Kijk of dit een originele wedstrijd is die nog niet in suggesties zit
                            var origKey = $"{wedstrijd.VeldNummer}_{wedstrijd.AanvangsTijd:HH:mm}_{wedstrijd.Wedstrijd?.Trim()}";
                            if (!vasteWedstrijden.Contains(origKey))
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
                                    NieuweTijd = RondAfOp5Min(nieuweTijd).ToString("HH:mm"),
                                    Reden = $"Extra buffer (+{cumulatieveVertraging} min)"
                                });
                            }
                        }
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

            // Stap 2: Bepaal wedstrijdparameters
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

            // Richtingfilter: bij 'vervroegen' alleen slots VÓÓR matchStart, bij 'verlaten' erna.
            // Per veld het slot kiezen dat het dichtst tegen matchStart aanligt — dat is de
            // 'natuurlijke' verschuiving die aansluit op de bestaande planning (latest-fit voor
            // vervroegen, earliest-fit voor verlaten). Hierdoor blijft veld 5 zichtbaar als het
            // de enige optie is, in plaats van weggesorteerd onder veld 1-4.
            bool vervroegen = string.Equals(request.Richting, "vervroegen", StringComparison.OrdinalIgnoreCase);
            bool verlaten = string.Equals(request.Richting, "verlaten", StringComparison.OrdinalIgnoreCase);
            int neemAantal = response.Beschikbaar ? 2 : 3;

            if (vervroegen)
            {
                // Latest-fit per veld: zo dicht mogelijk tegen matchStart óf tegen de eerstvolgende
                // bezetting in de ochtend — dat is wat een trainer bij 'vervroegen' bedoelt
                // (minste verstoring voor andere afspraken op die dag).
                candidates = FindLatestFitPerField(
                    availableFields, occupations, allTeamRules, teamRules,
                    veldFractie, duurMinuten,
                    upperBound: matchStart, windowStart: dagdeelVan);
                candidates = candidates
                    .Where(c => !(c.VeldNummer == matchVeldNummer && c.AanvangsTijd == matchStart))
                    .OrderByDescending(c => c.AanvangsTijd)
                    .ToList();
                neemAantal = candidates.Count; // toon alle relevante velden
            }
            else if (verlaten)
            {
                candidates = candidates
                    .Where(c => c.AanvangsTijd > matchStart)
                    .GroupBy(c => c.VeldNummer)
                    .Select(g => g.OrderBy(c => c.AanvangsTijd).First())
                    .OrderBy(c => c.AanvangsTijd)
                    .ToList();
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
