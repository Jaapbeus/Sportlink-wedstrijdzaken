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
            int duurMinuten = 0;
            decimal veldFractie = 1.00m;

            if (!string.IsNullOrEmpty(request.LeeftijdsCategorie))
            {
                speeltijd = await PlannerDataAccess.GetSpeeltijdAsync(request.LeeftijdsCategorie);
                if (speeltijd == null)
                {
                    response.Reden = $"Onbekende leeftijdscategorie: {request.LeeftijdsCategorie}. Voeg de categorie toe aan dbo.Speeltijden via /instellingen/speeltijden.";
                    return response;
                }
                duurMinuten = request.WedstrijdDuurMinuten ?? speeltijd.WedstrijdTotaal;
                veldFractie = speeltijd.Veldafmeting;
            }
            else if (request.WedstrijdDuurMinuten.HasValue)
            {
                duurMinuten = request.WedstrijdDuurMinuten.Value;
            }

            // Heel-veld override: als expliciet gevraagd, overschrijf de speeltijd-veldafmeting
            if (request.HeelVeld == true && veldFractie < 1.00m)
            {
                if (!string.IsNullOrEmpty(request.LeeftijdsCategorie))
                    response.Waarschuwingen.Add(
                        $"{request.LeeftijdsCategorie} speelt normaal op een halftijdsspeelveld ({veldFractie:P0} veld). " +
                        $"Inplannen op heel veld conform het verzoek (speelduur blijft {duurMinuten} min).");
                veldFractie = 1.00m;
            }

            if (duurMinuten <= 0)
            {
                response.Reden = "Leeftijdscategorie of wedstrijdduur is vereist. Voeg de categorie toe aan dbo.Speeltijden via /instellingen/speeltijden.";
                return response;
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

            // Stap 4: Alle huidige veldbezettingen laden (real-time API of DB als fallback)
            var occupations = await SportlinkApiClient.GetFieldOccupationsWithApiAsync(date, log);
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

            // Kunstgrasvelden prefereren boven grasvelden (grasvelden laatste keuze)
            var grasveldNummers = velden.Where(v => v.VeldType != "kunstgras").Select(v => v.VeldNummer).ToHashSet();
            foreach (var field in availableFields.OrderBy(f => grasveldNummers.Contains(f.VeldNummer) ? 1 : 0))
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

            // Sorteren: kunstgrasvelden prefereren boven grasvelden
            var grasveldNummersSort = velden.Where(v => v.VeldType != "kunstgras").Select(v => v.VeldNummer).ToHashSet();
            return candidates.OrderBy(c => grasveldNummersSort.Contains(c.VeldNummer) ? 1 : 0)
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
                    $"{date.ToString("dddd", NL)}: doordeweeks — kunstgrasvelden mogelijk in gebruik voor training. Controleer veldbeschikbaarheid.");
            }
        }

        private class CandidateSlot
        {
            public int VeldNummer { get; set; }
            public TimeOnly AanvangsTijd { get; set; }
            public TimeOnly EindTijd { get; set; }
            public decimal VeldFractie { get; set; }
        }

        // ── Auto-plan (#380) ──

        private class IngeplandSlot
        {
            public int VeldNummer { get; set; }
            public TimeOnly AanvangsTijd { get; set; }
            public TimeOnly EindTijd { get; set; }
            public decimal Fractie { get; set; }
            public string VeldSubpositie { get; set; } = string.Empty;
            public string? TeamNaam { get; set; }
        }

        private class FieldScheduler
        {
            private readonly List<VeldBeschikbaarheidInfo> _beschikbaarheid;
            private readonly List<VeldInfo> _velden;
            private readonly int _buffer;
            private readonly Dictionary<string, (int bufferVoor, int bufferNa)> _teamBuffers;
            private readonly Dictionary<int, List<IngeplandSlot>> _occupations = new();
            private static readonly TimeOnly StartTijd = new(9, 0);

            public FieldScheduler(List<VeldBeschikbaarheidInfo> beschikbaarheid, List<VeldInfo> velden, int buffer,
                Dictionary<string, (int bufferVoor, int bufferNa)>? teamBuffers = null)
            {
                _beschikbaarheid = beschikbaarheid;
                _velden = velden;
                _buffer = buffer;
                _teamBuffers = teamBuffers ?? new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in velden)
                    _occupations[v.VeldNummer] = new List<IngeplandSlot>();
            }

            private int TeamBufferVoor(string? teamNaam) =>
                teamNaam != null && _teamBuffers.TryGetValue(teamNaam, out var b) && b.bufferVoor > _buffer
                    ? b.bufferVoor : _buffer;

            private int TeamBufferNa(string? teamNaam) =>
                teamNaam != null && _teamBuffers.TryGetValue(teamNaam, out var b) && b.bufferNa > _buffer
                    ? b.bufferNa : _buffer;

            // Effectieve buffer tussen bestaande wedstrijd (occTeam) en nieuwe wedstrijd (nieuwTeam)
            private int EffectieveBuffer(string? occTeamNaam, int nieuwBufVoor) =>
                Math.Max(TeamBufferNa(occTeamNaam), nieuwBufVoor);

            // Zoekt het vroegst beschikbare slot ZONDER het te bezetten (peek voor gap-berekening)
            private IngeplandSlot? FindBestEarliestSlot(decimal fractie, int duurMinuten, int nieuwBufVoor)
            {
                var sortedVelden = _velden
                    .OrderByDescending(v => v.IsKunstgras)
                    .ThenBy(v => v.VeldNummer)
                    .ToList();

                IngeplandSlot? best = null;
                foreach (var veld in sortedVelden)
                {
                    var beschikbaar = _beschikbaarheid.FirstOrDefault(b => b.VeldNummer == veld.VeldNummer);
                    if (beschikbaar == null) continue;
                    var van = beschikbaar.BeschikbaarVanaf < StartTijd ? StartTijd : beschikbaar.BeschikbaarVanaf;
                    var tot = beschikbaar.BeschikbaarTot;
                    var slot = FindEarliestSlot(veld.VeldNummer, fractie, duurMinuten, van, tot, nieuwBufVoor);
                    if (slot != null && (best == null || slot.AanvangsTijd < best.AanvangsTijd))
                    {
                        best = slot;
                        if (best.AanvangsTijd == van) break;
                    }
                }
                return best; // niet toegevoegd aan _occupations
            }

            public IngeplandSlot? FindAndOccupyNextSlot(decimal fractie, int duurMinuten,
                int nieuwBufVoor = -1, string? teamNaam = null)
            {
                if (nieuwBufVoor < 0) nieuwBufVoor = _buffer;
                var best = FindBestEarliestSlot(fractie, duurMinuten, nieuwBufVoor);
                if (best != null)
                {
                    best.TeamNaam = teamNaam;
                    _occupations[best.VeldNummer].Add(best);
                }
                return best;
            }

            /// <summary>
            /// Probeert in te plannen nabij de voorkeurstijd. Compactheid heeft prioriteit:
            /// als de vroegst mogelijke start meer dan nieuwBufVoor minuten vóór de voorkeurstijd
            /// ligt, wordt compact ingepland (voorkeurstijd als richtlijn, niet als hard doel).
            /// </summary>
            public IngeplandSlot? FindAndOccupyNearTime(TimeOnly voorkeurTijd, decimal fractie, int duurMinuten,
                int nieuwBufVoor = -1, string? teamNaam = null, int tolerantieMinuten = 90)
            {
                if (nieuwBufVoor < 0) nieuwBufVoor = _buffer;

                // Compactheid-check: vroegst mogelijk slot zonder voorkeurstijd
                var vroegste = FindBestEarliestSlot(fractie, duurMinuten, nieuwBufVoor);
                if (vroegste != null)
                {
                    int gapMinuten = (int)(voorkeurTijd - vroegste.AanvangsTijd).TotalMinutes;
                    // Gap groter dan teamspecifieke buffer → voorkeurstijd creëert onnodig gat → compact
                    if (gapMinuten > nieuwBufVoor)
                    {
                        vroegste.TeamNaam = teamNaam;
                        _occupations[vroegste.VeldNummer].Add(vroegste);
                        return vroegste;
                    }
                }

                // Gap past binnen de buffer — voorkeurstijd is gerechtvaardigd
                // Probeer exact, dan uitwaaierend in stappen van 5 minuten
                var candidates = new List<TimeOnly> { voorkeurTijd };
                for (int delta = 5; delta <= tolerantieMinuten; delta += 5)
                {
                    var vroeger = voorkeurTijd.AddMinutes(-delta);
                    var later = voorkeurTijd.AddMinutes(delta);
                    if (vroeger >= StartTijd) candidates.Add(vroeger);
                    candidates.Add(later);
                }

                var sortedVelden = _velden
                    .OrderByDescending(v => v.IsKunstgras)
                    .ThenBy(v => v.VeldNummer)
                    .ToList();

                foreach (var kandidaatTijd in candidates)
                {
                    foreach (var veld in sortedVelden)
                    {
                        var beschikbaar = _beschikbaarheid.FirstOrDefault(b => b.VeldNummer == veld.VeldNummer);
                        if (beschikbaar == null) continue;

                        var van = beschikbaar.BeschikbaarVanaf < StartTijd ? StartTijd : beschikbaar.BeschikbaarVanaf;
                        var tot = beschikbaar.BeschikbaarTot;
                        var start = RondAfOp5Min(kandidaatTijd < van ? van : kandidaatTijd);
                        var end = start.AddMinutes(duurMinuten);
                        if (end > tot || end <= start) continue;

                        var occupations = _occupations.TryGetValue(veld.VeldNummer, out var list)
                            ? list : new List<IngeplandSlot>();
                        var fractiesInUse = occupations
                            .Where(o => o.AanvangsTijd < end && o.EindTijd > start)
                            .Sum(o => o.Fractie);
                        if (fractiesInUse + fractie > 1.0m + 0.001m) continue;

                        int concurrent = occupations.Count(o => o.AanvangsTijd < end && o.EindTijd > start);
                        var slot = new IngeplandSlot
                        {
                            VeldNummer = veld.VeldNummer,
                            AanvangsTijd = start,
                            EindTijd = end,
                            Fractie = fractie,
                            VeldSubpositie = GetSubpositie(fractie, concurrent),
                            TeamNaam = teamNaam
                        };
                        _occupations[veld.VeldNummer].Add(slot);
                        return slot;
                    }
                }

                // Valt terug op vroegst beschikbaar als niets binnen tolerantie past
                return FindAndOccupyNextSlot(fractie, duurMinuten, nieuwBufVoor, teamNaam);
            }

            private IngeplandSlot? FindEarliestSlot(int veldNummer, decimal fractie, int duurMinuten, TimeOnly van, TimeOnly tot,
                int nieuwBufVoor = -1)
            {
                if (nieuwBufVoor < 0) nieuwBufVoor = _buffer;
                var occupations = _occupations.TryGetValue(veldNummer, out var list)
                    ? list.OrderBy(o => o.AanvangsTijd).ToList()
                    : new List<IngeplandSlot>();

                // Candidate start times: field opens + after each match ends (respects team-specific buffers)
                var candidates = new HashSet<TimeOnly> { van };
                foreach (var occ in occupations)
                {
                    candidates.Add(occ.AanvangsTijd);
                    var afterEnd = occ.EindTijd.AddMinutes(EffectieveBuffer(occ.TeamNaam, nieuwBufVoor));
                    if (afterEnd > van) candidates.Add(afterEnd);
                }

                foreach (var candidate in candidates.OrderBy(t => t))
                {
                    if (candidate < van) continue;

                    var start = RondAfOp5Min(candidate);
                    if (start < van) start = van;
                    var end = start.AddMinutes(duurMinuten);

                    if (end > tot) continue;
                    if (end <= start) continue; // overflow past midnight guard

                    // Check fractie-capaciteit: sum of overlapping fractions <= 1.0 - fractie
                    var fractiesInUse = occupations
                        .Where(o => o.AanvangsTijd < end && o.EindTijd > start)
                        .Sum(o => o.Fractie);

                    if (fractiesInUse + fractie > 1.0m + 0.001m) continue;

                    // Count concurrent matches at this time for subpositie
                    int concurrent = occupations.Count(o => o.AanvangsTijd < end && o.EindTijd > start);

                    return new IngeplandSlot
                    {
                        VeldNummer = veldNummer,
                        AanvangsTijd = start,
                        EindTijd = end,
                        Fractie = fractie,
                        VeldSubpositie = GetSubpositie(fractie, concurrent)
                    };
                }

                return null;
            }

            private static string GetSubpositie(decimal fractie, int concurrent) => fractie switch
            {
                <= 0.25m => concurrent switch { 0 => "A1", 1 => "A2", 2 => "B1", _ => "B2" },
                <= 0.50m => concurrent == 0 ? "A" : "B",
                _ => string.Empty
            };
        }

        // Extraheert leeftijdscategorie uit ALLSTARS teamnaam: "VRC JO7-1" → "JO7", "ALLSTARS Heren 1" → "1-99"
        private static string? ExtractLeeftijdFromTeamNaam(string? teamNaam)
        {
            if (string.IsNullOrWhiteSpace(teamNaam)) return null;
            var parts = teamNaam.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            var second = parts[1];
            // Strip trailing "-N" zoals "JO7-1" → "JO7"
            var hyphenIdx = second.IndexOf('-');
            if (hyphenIdx > 0) second = second[..hyphenIdx];
            // Mapping naar Speeltijden-sleutels (zelfde als GetAllstarsOccupationsAsync op #365 branch)
            return second.ToUpperInvariant() switch
            {
                "HEREN"   => "1-99",
                "DAMES"   => "VR",
                "VROUWEN" => "VR",
                _ => string.IsNullOrWhiteSpace(second) ? null : second
            };
        }

        private static int GetLeeftijdSortOrder(string? leeftijd)
        {
            if (string.IsNullOrWhiteSpace(leeftijd)) return 99;
            var l = leeftijd.Trim().ToUpperInvariant();
            if (l.StartsWith("JO") && int.TryParse(l[2..], out var jo)) return jo;
            if (l.StartsWith("MO") && int.TryParse(l[2..], out var mo)) return 50 + mo;
            if (l == "VR" || l.StartsWith("VROUWEN")) return 80;
            if (l.StartsWith("G")) return 85;
            return 90; // Senioren / onbekend
        }

        // Standaard sorteersleutel (minuten na middernacht) voor teams zonder voorkeurstijd,
        // gebaseerd op leeftijdscategorie. Zorgt dat JO-teams vóór late Heren-teams worden ingepland.
        private static int GetDefaultTimeSortKey(string? leeftijd)
        {
            var order = GetLeeftijdSortOrder(leeftijd);
            return order <= 11 ? 540       // JO7–JO11  → 09:00
                 : order <= 13 ? 600       // JO12–JO13 → 10:00
                 : order <= 15 ? 630       // JO14–JO15 → 10:30
                 : order <= 17 ? 660       // JO16–JO17 → 11:00
                 : order <= 19 ? 690       // JO18–JO19 → 11:30
                 : order <= 25 ? 720       // JO21–JO23 → 12:00
                 : order <= 85 ? 750       // Vrouwen/Meisjes → 12:30
                 : 780;                    // Senioren / onbekend → 13:00
        }

        private static string BuildSportlinkVeldString(string veldNaam, string subpositie)
        {
            var naam = veldNaam.Trim();
            return string.IsNullOrEmpty(subpositie) ? naam : $"{naam} {subpositie}";
        }

        private static string NormaliseerVeld(string? veld)
        {
            if (string.IsNullOrWhiteSpace(veld)) return string.Empty;
            return veld.Trim().ToLowerInvariant().Replace("  ", " ");
        }

        public static async Task<AutoPlanResponse> AutoPlanAsync(
            AutoPlanRequest request, string clubCode, ILogger log)
        {
            bool isAllstars = clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase);
            int buffer = request.BufferMinuten ?? StandardBufferMinutes;

            if (!DateOnly.TryParse(request.Datum, out var datum))
                return new AutoPlanResponse { Datum = request.Datum };

            // 1. Data laden
            var alleWedstrijden = await PlannerDataAccess.GetAllMatchesForDatumAsync(datum, clubCode);

            // ALLSTARS: velden >= 100 (testmodus), synthetische beschikbaarheid 08:00–22:00
            List<VeldInfo> velden;
            List<VeldBeschikbaarheidInfo> beschikbaarheid;
            if (isAllstars)
            {
                velden = await PlannerDataAccess.GetAllstarsVeldenAsync();
                beschikbaarheid = velden.Select(v => new VeldBeschikbaarheidInfo
                {
                    VeldNummer = v.VeldNummer,
                    BeschikbaarVanaf = new TimeOnly(8, 0),
                    BeschikbaarTot = new TimeOnly(22, 0),
                    GebruikZonsondergang = false
                }).ToList();
            }
            else
            {
                velden = await PlannerDataAccess.GetVeldenAsync(clubCode);
                beschikbaarheid = await PlannerDataAccess.GetAvailableFieldsAsync(datum, clubCode);
            }

            var speeltijden = await PlannerDataAccess.GetSpeeltijdenLookupAsync();
            var veldInfoLookup = velden.ToDictionary(v => v.VeldNummer);

            // DagVanWeek: .NET DayOfWeek (0=zondag…6=zaterdag) → onze conventie (1=Ma…7=Zo)
            int dagVanWeek = datum.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)datum.DayOfWeek;
            var voorkeurLookup = await PlannerDataAccess.GetVoorkeurTijdenLookupAsync(dagVanWeek, clubCode);
            var teamBuffers = await PlannerDataAccess.GetAllTeamBuffersAsync();

            // 2. Sorteer wedstrijden op geschatte aanvangstijd.
            //    Teams met voorkeurstijd worden gesorteerd op die tijd (vroeger = eerder gepland).
            //    Teams zonder voorkeurstijd krijgen een standaard-tijd op basis van leeftijdscategorie.
            //    Dit zorgt dat vroeg-spelende JO-teams vóór Heren-teams met late voorkeurstijden
            //    worden ingepland, zodat de gap-check in FindAndOccupyNearTime correct werkt.
            var gesorteerd = alleWedstrijden
                .OrderBy(w =>
                {
                    var leeftijd = (!string.IsNullOrWhiteSpace(w.LeeftijdsCategorie))
                        ? w.LeeftijdsCategorie
                        : (isAllstars ? ExtractLeeftijdFromTeamNaam(w.TeamNaam) ?? "" : "");
                    if (voorkeurLookup.TryGetValue(w.TeamNaam, out var v) && v.Count > 0)
                        return (int)v.OrderBy(x => x.Prioriteit).First().Tijd.ToTimeSpan().TotalMinutes;
                    return GetDefaultTimeSortKey(leeftijd.Length > 0 ? leeftijd : null);
                })
                .ThenBy(w => GetLeeftijdSortOrder(
                    (!string.IsNullOrWhiteSpace(w.LeeftijdsCategorie))
                        ? w.LeeftijdsCategorie
                        : (isAllstars ? ExtractLeeftijdFromTeamNaam(w.TeamNaam) ?? "" : "")))
                .ThenBy(w => w.TeamNaam)
                .ToList();

            // 3. Scheduling algoritme
            var scheduler = new FieldScheduler(beschikbaarheid, velden, buffer, teamBuffers);
            var items = new List<AutoPlanWedstrijdItem>();

            foreach (var wedstrijd in gesorteerd)
            {
                // Voor ALLSTARS: leeftijdscategorie staat niet in DB maar in teamnaam ("VRC JO7-1" → "JO7")
                var leeftijd = (!string.IsNullOrWhiteSpace(wedstrijd.LeeftijdsCategorie))
                    ? wedstrijd.LeeftijdsCategorie
                    : (isAllstars ? ExtractLeeftijdFromTeamNaam(wedstrijd.TeamNaam) ?? "" : "");
                speeltijden.TryGetValue(leeftijd, out var speeltijdInfo);

                if (speeltijdInfo == null)
                {
                    items.Add(new AutoPlanWedstrijdItem
                    {
                        WedstrijdCode = wedstrijd.WedstrijdCode,
                        Wedstrijd = wedstrijd.Wedstrijd,
                        TeamNaam = wedstrijd.TeamNaam,
                        LeeftijdsCategorie = string.IsNullOrWhiteSpace(leeftijd) ? null : leeftijd,
                        Competitiesoort = wedstrijd.Competitiesoort,
                        HuidigeVeld = wedstrijd.Veld,
                        HuidigeTijd = wedstrijd.AanvangsTijd,
                        HeeftVeld = !string.IsNullOrWhiteSpace(wedstrijd.Veld),
                        HeeftTijd = !string.IsNullOrWhiteSpace(wedstrijd.AanvangsTijd),
                        Status = "niet-inplanbaar",
                        NietInplanbaaarReden = string.IsNullOrWhiteSpace(leeftijd)
                            ? "Leeftijdscategorie onbekend — team niet gevonden in his.teams"
                            : $"Leeftijdscategorie '{leeftijd}' ontbreekt in Speeltijden — voeg toe via Instellingen → Speeltijden"
                    });
                    continue;
                }

                // Gebruik voorkeurstijd als die bestaat voor dit team
                IngeplandSlot? slot;
                string? voorkeurTijdStr = null;
                int? voorkeurAfwijking = null;

                int teamBufVoor = teamBuffers.TryGetValue(wedstrijd.TeamNaam, out var tb) && tb.bufferVoor > buffer
                    ? tb.bufferVoor : buffer;

                if (voorkeurLookup.TryGetValue(wedstrijd.TeamNaam, out var voorkeuren) && voorkeuren.Count > 0)
                {
                    var primair = voorkeuren.OrderBy(v => v.Prioriteit).First();
                    voorkeurTijdStr = primair.Tijd.ToString("HH:mm");
                    slot = scheduler.FindAndOccupyNearTime(primair.Tijd, speeltijdInfo.Veldafmeting,
                        speeltijdInfo.WedstrijdTotaal, teamBufVoor, wedstrijd.TeamNaam);
                    if (slot != null)
                        voorkeurAfwijking = (int)(slot.AanvangsTijd.ToTimeSpan() - primair.Tijd.ToTimeSpan()).TotalMinutes;
                }
                else
                {
                    slot = scheduler.FindAndOccupyNextSlot(speeltijdInfo.Veldafmeting, speeltijdInfo.WedstrijdTotaal,
                        teamBufVoor, wedstrijd.TeamNaam);
                }

                if (slot == null)
                {
                    items.Add(new AutoPlanWedstrijdItem
                    {
                        WedstrijdCode = wedstrijd.WedstrijdCode,
                        Wedstrijd = wedstrijd.Wedstrijd,
                        TeamNaam = wedstrijd.TeamNaam,
                        LeeftijdsCategorie = wedstrijd.LeeftijdsCategorie,
                        Competitiesoort = wedstrijd.Competitiesoort,
                        DuurMinuten = speeltijdInfo.WedstrijdTotaal,
                        Veldafmeting = speeltijdInfo.Veldafmeting,
                        HuidigeVeld = wedstrijd.Veld,
                        HuidigeTijd = wedstrijd.AanvangsTijd,
                        HeeftVeld = !string.IsNullOrWhiteSpace(wedstrijd.Veld),
                        HeeftTijd = !string.IsNullOrWhiteSpace(wedstrijd.AanvangsTijd),
                        Status = "niet-inplanbaar",
                        NietInplanbaaarReden = "Geen beschikbaar veld gevonden voor deze datum"
                    });
                    continue;
                }

                var optimaalVeldNaam = veldInfoLookup.TryGetValue(slot.VeldNummer, out var vi)
                    ? vi.VeldNaam : $"veld {slot.VeldNummer}";
                var optimaalVeld = BuildSportlinkVeldString(optimaalVeldNaam, slot.VeldSubpositie);
                var optimaalTijd = slot.AanvangsTijd.ToString("HH:mm");

                var huidigeVeldNorm = NormaliseerVeld(wedstrijd.Veld);
                var optimaalVeldNorm = NormaliseerVeld(optimaalVeld);
                bool heeftVeld = !string.IsNullOrWhiteSpace(wedstrijd.Veld);
                bool heeftTijd = !string.IsNullOrWhiteSpace(wedstrijd.AanvangsTijd);
                bool tijdWijzigt = wedstrijd.AanvangsTijd?.Trim() != optimaalTijd;
                bool veldWijzigt = huidigeVeldNorm != optimaalVeldNorm;

                string status;
                if (!heeftVeld || !heeftTijd)
                    status = "nieuw-slot";
                else if (tijdWijzigt || veldWijzigt)
                    status = "wijziging";
                else
                    status = "ongewijzigd";

                items.Add(new AutoPlanWedstrijdItem
                {
                    WedstrijdCode = wedstrijd.WedstrijdCode,
                    Wedstrijd = wedstrijd.Wedstrijd,
                    TeamNaam = wedstrijd.TeamNaam,
                    LeeftijdsCategorie = string.IsNullOrWhiteSpace(leeftijd) ? null : leeftijd,
                    Competitiesoort = wedstrijd.Competitiesoort,
                    DuurMinuten = speeltijdInfo.WedstrijdTotaal,
                    Veldafmeting = speeltijdInfo.Veldafmeting,
                    HuidigeVeld = wedstrijd.Veld,
                    HuidigeTijd = wedstrijd.AanvangsTijd,
                    HeeftVeld = heeftVeld,
                    HeeftTijd = heeftTijd,
                    OptimaalVeldNummer = slot.VeldNummer,
                    OptimaalVeldNaam = optimaalVeldNaam,
                    OptimaalVeld = optimaalVeld,
                    OptimaalTijd = optimaalTijd,
                    Status = status,
                    VoorkeurTijd = voorkeurTijdStr,
                    VoorkeurAfwijkingMinuten = voorkeurAfwijking
                });
            }

            // 4. Statistieken
            int zonderVeld = items.Count(i => !i.HeeftVeld);
            int zonderTijd = items.Count(i => !i.HeeftTijd);
            int teWijzigen = items.Count(i => i.Status is "nieuw-slot" or "wijziging");
            int nietInplanbaar = items.Count(i => i.Status == "niet-inplanbaar");

            var eindTijden = items
                .Where(i => i.OptimaalTijd != null && i.DuurMinuten > 0
                    && TimeOnly.TryParse(i.OptimaalTijd, out _))
                .Select(i => TimeOnly.Parse(i.OptimaalTijd!).AddMinutes(i.DuurMinuten))
                .ToList();
            string? eindTijd = eindTijden.Count > 0 ? eindTijden.Max().ToString("HH:mm") : null;

            // 5. HTML-visualisaties (huidig + optimaal)
            var huidigeOccupations = items
                .Where(i => i.HeeftVeld && i.HeeftTijd && i.OptimaalVeldNummer.HasValue)
                .Select(i =>
                {
                    // Map huidig veld naar veldnummer via naam
                    var huidigVeldNr = velden.FirstOrDefault(v =>
                        NormaliseerVeld(v.VeldNaam) == NormaliseerVeld(i.HuidigeVeld?.Split(' ').Take(2).LastOrDefault() ?? ""))?.VeldNummer ?? 0;
                    if (huidigVeldNr == 0) return null;
                    if (!TimeOnly.TryParse(i.HuidigeTijd, out var aTime)) return null;
                    return new BestaandeWedstrijd
                    {
                        Datum = datum,
                        AanvangsTijd = aTime,
                        EindTijd = aTime.AddMinutes(i.DuurMinuten),
                        VeldNummer = huidigVeldNr,
                        VeldDeelGebruik = i.Veldafmeting > 0 ? i.Veldafmeting : 1m,
                        LeeftijdsCategorie = i.LeeftijdsCategorie,
                        TeamNaam = i.TeamNaam,
                        Wedstrijd = i.Wedstrijd,
                        Bron = "Sportlink"
                    };
                })
                .Where(o => o != null).Cast<BestaandeWedstrijd>().ToList();

            var optimaleOccupations = items
                .Where(i => i.OptimaalVeldNummer.HasValue && i.OptimaalTijd != null
                    && TimeOnly.TryParse(i.OptimaalTijd, out _))
                .Select(i => new BestaandeWedstrijd
                {
                    Datum = datum,
                    AanvangsTijd = TimeOnly.Parse(i.OptimaalTijd!),
                    EindTijd = TimeOnly.Parse(i.OptimaalTijd!).AddMinutes(i.DuurMinuten),
                    VeldNummer = i.OptimaalVeldNummer!.Value,
                    VeldDeelGebruik = i.Veldafmeting > 0 ? i.Veldafmeting : 1m,
                    VeldSubpositie = i.OptimaalVeld?.Contains(' ') == true
                        ? i.OptimaalVeld.Split(' ').LastOrDefault() : null,
                    LeeftijdsCategorie = i.LeeftijdsCategorie,
                    TeamNaam = i.TeamNaam,
                    Wedstrijd = i.Wedstrijd,
                    Bron = "Optimaal"
                })
                .ToList();

            string huidigeHtml = PlannerHtmlGenerator.GenereerHtml(
                datum, huidigeOccupations, new List<OptimalisatieSuggestie>(), velden, "huidig");
            string optimaleHtml = PlannerHtmlGenerator.GenereerHtml(
                datum, optimaleOccupations, new List<OptimalisatieSuggestie>(), velden, "optimaal");

            log.LogInformation("AutoPlan {Datum}: {Totaal} wedstrijden, {Wijzigen} te wijzigen, eindtijd {Eind}",
                datum, items.Count, teWijzigen, eindTijd ?? "?");

            return new AutoPlanResponse
            {
                Datum = request.Datum,
                TotaalWedstrijden = items.Count,
                ZonderVeld = zonderVeld,
                ZonderTijd = zonderTijd,
                TeWijzigen = teWijzigen,
                NietInplanbaar = nietInplanbaar,
                GeschatteEindTijd = eindTijd,
                Wedstrijden = items,
                HuidigeHtml = huidigeHtml,
                OptimaleHtml = optimaleHtml
            };
        }

        public static async Task<AutoPlanToepassenResponse> AutoPlanToepassenAsync(
            AutoPlanToepassenRequest request, string clubCode, ILogger log)
        {
            if (!clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Toepassen is alleen beschikbaar in testmodus (ALLSTARS).");

            // Herbereken het plan (deterministisch — zelfde input = zelfde output)
            var planResponse = await AutoPlanAsync(new AutoPlanRequest
            {
                Datum = request.Datum,
                BufferMinuten = request.BufferMinuten
            }, clubCode, log);

            var response = new AutoPlanToepassenResponse();

            foreach (var item in planResponse.Wedstrijden)
            {
                if (item.Status == "ongewijzigd") continue;
                if (item.Status == "niet-inplanbaar") continue;
                if (item.WedstrijdCode == null) continue;
                if (item.OptimaalVeld == null || item.OptimaalTijd == null) continue;

                try
                {
                    int updated = await PlannerDataAccess.UpdateAllstarsMatchAsync(
                        item.WedstrijdCode.Value, item.OptimaalVeld, item.OptimaalTijd);
                    if (updated > 0) response.Bijgewerkt++;
                    else
                    {
                        response.Mislukt++;
                        response.Fouten.Add($"{item.Wedstrijd}: wedstrijdcode {item.WedstrijdCode} niet gevonden");
                    }
                }
                catch (Exception ex)
                {
                    response.Mislukt++;
                    response.Fouten.Add($"{item.Wedstrijd}: {ex.Message}");
                    log.LogWarning(ex, "AutoPlan toepassen mislukt voor wedstrijd {Code}", item.WedstrijdCode);
                }
            }

            log.LogInformation("AutoPlan toepassen {Datum}: {Bijgewerkt} bijgewerkt, {Mislukt} mislukt",
                request.Datum, response.Bijgewerkt, response.Mislukt);

            return response;
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

                var occupations = await SportlinkApiClient.GetFieldOccupationsWithApiAsync(date, log);

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
            OptimaliseerRequest request, string? clubCode, ILogger log)
        {
            var nl = new System.Globalization.CultureInfo("nl-NL");
            var response = new OptimaliseerResponse { Datum = request.Datum };

            if (!DateOnly.TryParse(request.Datum, out var date))
            {
                response.HtmlPlanner = "<p>Ongeldige datum</p>";
                return response;
            }

            // Data laden (real-time API of DB als fallback)
            var occupations = await SportlinkApiClient.GetFieldOccupationsWithApiAsync(date, log);
            var velden = await PlannerDataAccess.GetVeldenAsync(clubCode);
            var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date, clubCode);
            var grasveldNummers = velden.Where(v => v.VeldType != "kunstgras").Select(v => v.VeldNummer).ToHashSet();
            var kunstgrasNummers = velden.Where(v => v.VeldType == "kunstgras").Select(v => v.VeldNummer).ToHashSet();

            // Dedup bezettingen
            var bezettingen = occupations
                .GroupBy(o => $"{o.VeldNummer}_{o.AanvangsTijd:HH:mm}_{o.Wedstrijd?.Trim()}")
                .Select(g => g.First())
                .OrderBy(o => o.AanvangsTijd).ThenBy(o => o.VeldNummer)
                .ToList();

            // Geen wedstrijden op deze dag (bijv. zondag) → informatieve response
            if (bezettingen.Count == 0)
            {
                response.VoldoendeRuimte = true;
                response.VoldoendeRuimteMelding = $"Geen wedstrijden gepland op {date.ToString("dddd d MMMM", nl)}.";
                response.HtmlPlanner = $"<p style='color:#8b949e;font-family:sans-serif;'>Geen wedstrijden gepland op {date.ToString("dddd d MMMM yyyy", nl)}.</p>";
                return response;
            }

            // Huidige eindtijd bepalen
            var laatsteWedstrijd = bezettingen.OrderByDescending(o => o.EindTijd).FirstOrDefault();
            response.HuidigeEindtijd = laatsteWedstrijd?.EindTijd.ToString("HH:mm") ?? "—";

            // Team-regels laden voor vaste wedstrijden
            var allTeamRules = new Dictionary<string, List<TeamRegel>>();
            foreach (var teamNaam in bezettingen.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!).Distinct())
            {
                allTeamRules[teamNaam] = await PlannerDataAccess.GetTeamRulesAsync(teamNaam);
            }

            // Vaste wedstrijden identificeren (eerste elftal of wedstrijden met speciale bufferregels)
            var vasteWedstrijden = new HashSet<string>();
            foreach (var b in bezettingen)
            {
                if (b.TeamNaam != null && allTeamRules.TryGetValue(b.TeamNaam, out var regels) && regels.Count > 0)
                    vasteWedstrijden.Add($"{b.VeldNummer}_{b.AanvangsTijd:HH:mm}_{b.Wedstrijd?.Trim()}");
            }

            // Buffer uit request of standaard
            int bufferMin = request.BufferMinuten ?? StandardBufferMinutes;

            // Capaciteitsberekening en optimalisatie-noodzaak analyse
            var capaciteit = BerekenCapaciteit(bezettingen, availableFields, grasveldNummers);
            response.CapaciteitOverzicht = capaciteit;

            // Bepaal welke optimalisaties zinvol zijn voor deze specifieke dag en het opgegeven doel.
            //
            // grasveld-ontlasten: zinvol als kunstgrasvelden beschikbaar zijn (zaterdag) én er wedstrijden op
            //   grasveld staan. Op doordeweekse dagen zijn kunstgrasvelden mogelijk in gebruik voor training.
            //
            // strakker-plannen: zinvol als de gebruiker een gewenste eindtijd heeft opgegeven of
            //   het doel expliciet op 'strakker-plannen' gezet heeft. Zonder die grenswaarde weet
            //   de planner niet wat 'beter' is en genereert hij onnodig suggesties.
            var doel = (request.Doel ?? "").ToLowerInvariant().Trim();
            bool alleenGrasveldBeschikbaar = availableFields.Count > 0 && !availableFields.Any(f => kunstgrasNummers.Contains(f.VeldNummer));
            bool gewensteEindtijdOpgegeven = !string.IsNullOrEmpty(request.GewensteEindtijd);
            bool grasveldOntlastenZinvol = !alleenGrasveldBeschikbaar && capaciteit.AantalWedstrijdenOpGrasveld > 0;
            bool strakkerPlannenZinvol = gewensteEindtijdOpgegeven || doel == "strakker-plannen";

            // Check 1: laag bezettingspercentage én geen grasveld gebruik
            if (capaciteit.AantalWedstrijdenOpGrasveld == 0 && capaciteit.BezettingsPercentage < MaxBezettingsPercentageVoorOverslaan)
            {
                response.VoldoendeRuimte = true;
                response.VoldoendeRuimteMelding =
                    $"Voldoende ruimte op {date.ToString("dddd d MMMM", nl)}: " +
                    $"geen wedstrijden op grasveld, {capaciteit.BezettingsPercentage:F0}% bezet " +
                    $"({capaciteit.AantalLegeVelden} veld(en) ongebruikt). Geen optimalisatie nodig.";
                response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(
                    date, bezettingen, new List<OptimalisatieSuggestie>(), velden, doel);
                return response;
            }

            // Check 2: geen enkel optimalisatiedoel is zinvol voor deze dag
            if (!grasveldOntlastenZinvol && !strakkerPlannenZinvol)
            {
                string redenDetail = alleenGrasveldBeschikbaar
                    ? "doordeweekse dag — alleen grasveld beschikbaar, kunstgrasvelden niet beschikbaar, en er is geen gewenste eindtijd opgegeven"
                    : "geen wedstrijden op grasveld en geen gewenste eindtijd opgegeven";
                response.VoldoendeRuimte = true;
                response.VoldoendeRuimteMelding = $"Geen optimalisatie nodig op {date.ToString("dddd d MMMM", nl)}: {redenDetail}.";
                response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(
                    date, bezettingen, new List<OptimalisatieSuggestie>(), velden, doel);
                return response;
            }

            var suggesties = new List<OptimalisatieSuggestie>();

            switch (doel)
            {
                case "grasveld-ontlasten":
                    if (grasveldOntlastenZinvol)
                        suggesties = OptimaliseerGrasveldOntlasten(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                    break;
                case "strakker-plannen":
                    if (strakkerPlannenZinvol)
                        suggesties = OptimaliseerStrakkerPlannen(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                    break;
                default:
                    if (grasveldOntlastenZinvol)
                        suggesties = OptimaliseerGrasveldOntlasten(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                    if (strakkerPlannenZinvol)
                    {
                        var strakkerSuggesties = OptimaliseerStrakkerPlannen(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                        foreach (var s in strakkerSuggesties)
                        {
                            if (!suggesties.Any(bestaand => bestaand.Wedstrijd == s.Wedstrijd))
                                suggesties.Add(s);
                        }
                    }
                    break;
            }

            // Stap 2: Dynamische buffer — verdeel resterende ruimte als extra buffer
            TimeOnly gewensteEindtijd = new(16, 15);
            if (!string.IsNullOrEmpty(request.GewensteEindtijd) && TimeOnly.TryParse(request.GewensteEindtijd, out var parsed))
                gewensteEindtijd = parsed;

            suggesties = VerdeelExtraBuffer(suggesties, bezettingen, availableFields, velden, vasteWedstrijden, allTeamRules, gewensteEindtijd);

            // Check 3: suggesties verbeteren de eindtijd niet — no-op onderdrukken (#301)
            // Bereken de nieuwe eindtijd na toepassing van de suggesties. Als die niet eerder is
            // dan de huidige eindtijd, zijn de suggesties zinloos en worden ze onderdrukt.
            if (suggesties.Count > 0 && bezettingen.Count > 0)
            {
                var huidigeMax = bezettingen.Max(b => b.EindTijd);
                var nieuweMax = bezettingen.Select(b =>
                {
                    var sug = suggesties.FirstOrDefault(s => s.Wedstrijd == b.Wedstrijd?.Trim());
                    if (sug != null && TimeOnly.TryParse(sug.NieuweTijd, out var nieuwStart))
                        return TimeOnly.FromTimeSpan(nieuwStart.ToTimeSpan() + (b.EindTijd.ToTimeSpan() - b.AanvangsTijd.ToTimeSpan()));
                    return b.EindTijd;
                }).Max();

                if (nieuweMax >= huidigeMax)
                {
                    response.VoldoendeRuimte = true;
                    response.VoldoendeRuimteMelding =
                        $"Geen optimalisatie nodig op {date.ToString("dddd d MMMM", nl)}: " +
                        "de huidige planning is al optimaal — verplaatsingen leveren geen eerdere eindtijd op.";
                    response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(
                        date, bezettingen, new List<OptimalisatieSuggestie>(), velden, doel);
                    return response;
                }
            }

            response.Suggesties = suggesties;
            response.AantalVerplaatsingen = suggesties.Count;
            response.AantalVanGrasveldVerplaatst = suggesties.Count(s => grasveldNummers.Contains(s.HuidigVeldNummer));

            // HTML genereren
            response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(date, bezettingen, suggesties, velden, request.Doel ?? "optimaliseren");

            return response;
        }

        private static VeldCapaciteitInfo BerekenCapaciteit(
            List<BestaandeWedstrijd> bezettingen,
            List<VeldBeschikbaarheidInfo> beschikbareVelden,
            HashSet<int> grasveldNummers)
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
                AantalWedstrijdenOpGrasveld = bezettingen.Count(b => grasveldNummers.Contains(b.VeldNummer)),
                AantalLegeVelden = aantalLegeVelden
            };
        }

        private static List<OptimalisatieSuggestie> OptimaliseerGrasveldOntlasten(
            List<BestaandeWedstrijd> bezettingen,
            List<VeldInfo> velden,
            List<VeldBeschikbaarheidInfo> beschikbareVelden,
            HashSet<string> vasteWedstrijden,
            Dictionary<string, List<TeamRegel>> allTeamRules,
            int bufferMin = 15)
        {
            var grasveldNummers = velden.Where(v => v.VeldType != "kunstgras").Select(v => v.VeldNummer).ToHashSet();
            var kunstgrasNummers = velden.Where(v => v.VeldType == "kunstgras").Select(v => v.VeldNummer).ToHashSet();
            var suggesties = new List<OptimalisatieSuggestie>();

            // Wedstrijden op grasvelden die verplaatst mogen worden
            var grasveldWedstrijden = bezettingen
                .Where(b => grasveldNummers.Contains(b.VeldNummer))
                .Where(b => !vasteWedstrijden.Contains($"{b.VeldNummer}_{b.AanvangsTijd:HH:mm}_{b.Wedstrijd?.Trim()}"))
                .OrderBy(b => b.AanvangsTijd)
                .ToList();

            // Werk met een kopie van bezettingen die we aanpassen naarmate we suggesties doen
            var werkBezettingen = bezettingen.ToList();

            foreach (var wedstrijd in grasveldWedstrijden)
            {
                int duur = (int)(wedstrijd.EindTijd - wedstrijd.AanvangsTijd).TotalMinutes;
                decimal fractie = wedstrijd.VeldDeelGebruik;
                var huidigVeldNaam = velden.FirstOrDefault(v => v.VeldNummer == wedstrijd.VeldNummer)?.VeldNaam ?? $"veld {wedstrijd.VeldNummer}";

                // Zoek een plek op kunstgrasveld die EERDER is dan huidige tijd op grasveld
                // Alleen verplaatsen als het een verbetering is (eerder of gelijktijdig op kunstgras)
                CandidateSlot? besteSlot = null;
                foreach (var veldBesch in beschikbareVelden.Where(f => kunstgrasNummers.Contains(f.VeldNummer)).OrderBy(f => f.VeldNummer))
                {
                    var veldBezetting = werkBezettingen.Where(b => b.VeldNummer == veldBesch.VeldNummer).ToList();

                    for (var tijd = veldBesch.BeschikbaarVanaf; tijd.AddMinutes(duur) <= veldBesch.BeschikbaarTot; tijd = tijd.AddMinutes(5))
                    {
                        // Alleen accepteren als het niet later is dan de huidige tijd op grasveld
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
                        HuidigVeldNummer = wedstrijd.VeldNummer,
                        HuidigVeld = huidigVeldNaam,
                        HuidigeTijd = wedstrijd.AanvangsTijd.ToString("HH:mm"),
                        NieuwVeldNummer = besteSlot.VeldNummer,
                        NieuwVeld = veldNaam,
                        NieuweTijd = RondAfOp5Min(besteSlot.AanvangsTijd).ToString("HH:mm"),
                        Reden = $"Verplaats van {huidigVeldNaam} (grasveld) naar {veldNaam} (kunstgras)"
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

            var occupations = await SportlinkApiClient.GetFieldOccupationsExcludingMatchWithApiAsync(
                date, match.Wedstrijd, matchStart, matchVeldNummer, log);

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

        // ── Team schedule (#70) ──

        public static async Task<TeamScheduleResponse?> GetTeamScheduleAsync(string team)
        {
            if (!await PlannerDataAccess.TeamExistsAsync(team))
                return null;

            var seizoenEinde = await PlannerDataAccess.GetSeasonEndDateAsync() ?? DateOnly.FromDateTime(DateTime.Today.AddMonths(3));
            var vandaag = DateOnly.FromDateTime(DateTime.Today);
            var wedstrijden = await PlannerDataAccess.GetFutureMatchesForTeamAsync(team, vandaag, seizoenEinde);

            // Alle zaterdagen van vandaag t/m seizoenseinde
            var zaterdagen = new List<TeamScheduleZaterdag>();
            var zaterdag = vandaag;
            while (zaterdag.DayOfWeek != DayOfWeek.Saturday)
                zaterdag = zaterdag.AddDays(1);

            while (zaterdag <= seizoenEinde)
            {
                var zatStr = zaterdag.ToString("yyyy-MM-dd");
                var opDeDag = wedstrijden.Where(w => w.Datum == zatStr).ToList();

                string status;
                TeamScheduleWedstrijd? bezetDoor = null;

                var bezet = opDeDag.FirstOrDefault(w => w.Type == "competitie" || w.Type == "beker");
                if (bezet != null)
                {
                    status = "bezet";
                    bezetDoor = bezet;
                }
                else
                {
                    var oefen = opDeDag.FirstOrDefault(w => w.Type == "oefenwedstrijd");
                    if (oefen != null)
                    {
                        status = "oefenwedstrijd";
                        bezetDoor = oefen;
                    }
                    else
                    {
                        status = "vrij";
                    }
                }

                zaterdagen.Add(new TeamScheduleZaterdag
                {
                    Datum = zatStr,
                    Status = status,
                    BezetDoor = bezetDoor
                });

                zaterdag = zaterdag.AddDays(7);
            }

            return new TeamScheduleResponse
            {
                Team = team,
                SeizoenEinde = seizoenEinde.ToString("yyyy-MM-dd"),
                Zaterdagen = zaterdagen,
                Wedstrijden = wedstrijden
            };
        }
    }
}
