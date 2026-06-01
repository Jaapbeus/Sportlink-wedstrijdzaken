using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner;

/// <summary>
/// Use-case service voor beschikbaarheidscontroles.
/// Extracted uit PlannerService (#475).
/// </summary>
internal static class AvailabilityService
{
    public static async Task<CheckAvailabilityResponse> CheckAvailabilityAsync(
        CheckAvailabilityRequest request, ILogger log)
    {
        var response = new CheckAvailabilityResponse();

        if (!DateOnly.TryParse(request.Datum, out var date))
        {
            response.Reden = $"Ongeldige datum: {request.Datum}";
            return response;
        }
        if (date <= DateOnly.FromDateTime(DateTime.Today))
        {
            response.Reden = $"De gewenste datum {request.Datum} kan niet verwerkt worden. Een datum moet in de toekomst zijn.";
            return response;
        }

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
                response.Reden = $"{request.TeamNaam} heeft al een wedstrijd op {date.ToString("d MMMM", PlannerShared.NL)}: " +
                                 $"{conflict.Wedstrijd} om {conflict.AanvangsTijd:HH:mm} ({response.TeamConflict.VeldNaam}).";
                return response;
            }
        }

        var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date);
        if (availableFields.Count == 0)
        {
            response.Reden = $"Geen wedstrijden mogelijk op {date.DayOfWeek switch
            {
                DayOfWeek.Friday => "vrijdag",
                DayOfWeek.Sunday => "zondag",
                _ => date.ToString("dddd d MMMM", PlannerShared.NL)
            }}.";
            return response;
        }

        var occupations = await SportlinkApiClient.GetFieldOccupationsWithApiAsync(date, log);
        var velden      = await PlannerDataAccess.GetVeldenAsync();
        var teamRules   = new List<TeamRegel>();
        if (!string.IsNullOrEmpty(request.TeamNaam))
            teamRules = await PlannerDataAccess.GetTeamRulesAsync(request.TeamNaam);

        var allTeamRules = new Dictionary<string, List<TeamRegel>>();
        foreach (var occ in occupations.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!).Distinct())
            allTeamRules[occ] = await PlannerDataAccess.GetTeamRulesAsync(occ);

        TimeOnly? sunset = await PlannerDataAccess.GetSunsetAsync(date);
        if (sunset == null) sunset = SunsetCalculator.GetSunset(date);
        foreach (var field in availableFields)
            if (field.GebruikZonsondergang && sunset.HasValue && sunset.Value < field.BeschikbaarTot)
                field.BeschikbaarTot = sunset.Value;

        if (string.IsNullOrEmpty(request.AanvangsTijd))
        {
            var windowsResponse = BuildWindowsResponse(date, availableFields, occupations, velden, sunset, request.Dagdeel);
            if (windowsResponse.BeschikbareVensters != null && duurMinuten > 0)
            {
                windowsResponse.BeschikbareVensters = windowsResponse.BeschikbareVensters
                    .Where(w => w.MaxDuurMinuten >= duurMinuten).ToList();
                windowsResponse.Beschikbaar = windowsResponse.BeschikbareVensters.Count > 0;
                if (!windowsResponse.Beschikbaar)
                    windowsResponse.Reden = $"Geen venster van minimaal {duurMinuten} minuten beschikbaar op {date.ToString("dddd d MMMM", PlannerShared.NL)}.";
            }
            return windowsResponse;
        }

        TimeOnly? preferredTime = null;
        if (!string.IsNullOrEmpty(request.AanvangsTijd) && TimeOnly.TryParse(request.AanvangsTijd, out var parsed))
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

        var venstersResponse = BuildWindowsResponse(date, availableFields, occupations, velden, sunset, request.Dagdeel);
        if (duurMinuten > 0 && venstersResponse.BeschikbareVensters != null)
            venstersResponse.BeschikbareVensters = venstersResponse.BeschikbareVensters.Where(w => w.MaxDuurMinuten >= duurMinuten).ToList();

        if (preferredTime.HasValue)
        {
            var exactMatch = PlannerShared.TryExactTime(preferredTime.Value, availableFields, occupations, velden,
                                           allTeamRules, teamRules, veldFractie, duurMinuten, sunset);
            if (exactMatch != null)
            {
                response.Beschikbaar = true;
                response.Toewijzing = PlannerShared.ToSlotToewijzing(date, exactMatch, duurMinuten, velden);
                response.BeschikbareVensters = venstersResponse.BeschikbareVensters;
                AddSunsetWarning(response, exactMatch, sunset, velden);
                AddNabijeWedstrijdWaarschuwing(response, exactMatch, duurMinuten, occupations, velden);
                PlannerShared.AddWeekdayWarning(response, date);
                return response;
            }
        }

        var candidates = PlannerShared.FindAllSlots(availableFields, occupations, velden, allTeamRules, teamRules,
                                      veldFractie, duurMinuten, dagdeelVan, dagdeelTot, sunset);
        if (candidates.Count > 0)
        {
            var ordered = preferredTime.HasValue
                ? candidates.OrderBy(c => Math.Abs(c.AanvangsTijd.ToTimeSpan().TotalMinutes - preferredTime.Value.ToTimeSpan().TotalMinutes))
                : candidates.OrderBy(c => c.AanvangsTijd.ToTimeSpan().TotalMinutes);
            var best = ordered.First();
            var alternatives = ordered.Skip(1).Take(3).ToList();

            if (preferredTime.HasValue)
            {
                response.Reden = $"Gewenste tijd {preferredTime.Value:HH:mm} is niet beschikbaar.";
                response.Alternatieven = alternatives.Prepend(best).Select(c => PlannerShared.ToSlotToewijzing(date, c, duurMinuten, velden)).Take(3).ToList();
                response.BeschikbareVensters = venstersResponse.BeschikbareVensters;
                PlannerShared.AddWeekdayWarning(response, date);
            }
            else
            {
                response.Beschikbaar = true;
                response.Toewijzing = PlannerShared.ToSlotToewijzing(date, best, duurMinuten, velden);
                AddNabijeWedstrijdWaarschuwing(response, best, duurMinuten, occupations, velden);
                response.Alternatieven = alternatives.Select(c => PlannerShared.ToSlotToewijzing(date, c, duurMinuten, velden)).ToList();
                response.BeschikbareVensters = venstersResponse.BeschikbareVensters;
                AddSunsetWarning(response, best, sunset, velden);
                PlannerShared.AddWeekdayWarning(response, date);
            }
        }
        else
        {
            response.Reden = $"Geen beschikbaar veld gevonden op {date.ToString("dddd d MMMM", PlannerShared.NL)}.";
            PlannerShared.AddWeekdayWarning(response, date);
        }
        return response;
    }

    public static async Task<DoordeweeksBeschikbaarResponse> CheckDoordeweeksBeschikbaarAsync(
        DoordeweeksBeschikbaarRequest request, ILogger log)
    {
        var response = new DoordeweeksBeschikbaarResponse { DagFilter = request.DagFilter };
        var seizoenEinde = await PlannerDataAccess.GetSeasonEndDateAsync()
            ?? DateOnly.FromDateTime(DateTime.Today.AddMonths(6));
        response.SeizoenEinde = seizoenEinde.ToString("yyyy-MM-dd");

        int? gewensteDuur = request.DuurMinuten;
        if (!gewensteDuur.HasValue && !string.IsNullOrEmpty(request.LeeftijdsCategorie))
        {
            var speeltijd = await PlannerDataAccess.GetSpeeltijdAsync(request.LeeftijdsCategorie);
            if (speeltijd != null) gewensteDuur = speeltijd.WedstrijdTotaal;
        }

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
            if (date.DayOfWeek < DayOfWeek.Monday || date.DayOfWeek > DayOfWeek.Thursday) continue;
            if (dagFilter.HasValue && date.DayOfWeek != dagFilter.Value) continue;

            var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date);
            if (availableFields.Count == 0) continue;

            TimeOnly? sunset = await PlannerDataAccess.GetSunsetAsync(date);
            if (sunset == null) sunset = SunsetCalculator.GetSunset(date);
            string sunsetStr = sunset.HasValue ? sunset.Value.ToString("HH:mm") : "";
            foreach (var field in availableFields)
                if (field.GebruikZonsondergang && sunset.HasValue && sunset.Value < field.BeschikbaarTot)
                    field.BeschikbaarTot = sunset.Value;

            var occupations = await SportlinkApiClient.GetFieldOccupationsWithApiAsync(date, log);

            foreach (var field in availableFields)
            {
                var fieldOccs = occupations.Where(o => o.VeldNummer == field.VeldNummer).OrderBy(o => o.AanvangsTijd).ToList();
                var gapStart = field.BeschikbaarVanaf;
                void AddVenster(TimeOnly van, TimeOnly tot)
                {
                    int maxDuur = (int)(tot.ToTimeSpan() - van.ToTimeSpan()).TotalMinutes;
                    if (maxDuur < 30) return;
                    response.BeschikbareDatums.Add(new DoordeweekseDatum
                    {
                        Datum = date.ToString("yyyy-MM-dd"),
                        DagVanWeek = date.ToString("dddd", PlannerShared.NL),
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
                    var occStart = occ.AanvangsTijd.AddMinutes(-PlannerShared.StandardBufferMinutes);
                    if (occStart > gapStart) AddVenster(gapStart, occStart);
                    gapStart = occ.EindTijd.AddMinutes(PlannerShared.StandardBufferMinutes);
                }
                if (gapStart < field.BeschikbaarTot) AddVenster(gapStart, field.BeschikbaarTot);
            }
        }
        response.AantalBeschikbaar = response.BeschikbareDatums.Count;
        return response;
    }

    // ── Privé helpers ──

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
            switch (dagdeel.ToLowerInvariant())
            {
                case "ochtend": filterVan = new(8, 30); filterTot = new(12, 0); break;
                case "middag":  filterVan = new(12, 0); filterTot = new(17, 0); break;
                case "avond":   filterVan = new(17, 0); filterTot = new(22, 0); break;
            }
        foreach (var field in fields)
        {
            var veldInfo  = velden.FirstOrDefault(v => v.VeldNummer == field.VeldNummer);
            var fieldOccs = occupations.Where(o => o.VeldNummer == field.VeldNummer).OrderBy(o => o.AanvangsTijd).ToList();
            var effectiveStart = field.BeschikbaarVanaf < filterVan ? filterVan : field.BeschikbaarVanaf;
            var effectiveEnd   = field.BeschikbaarTot   > filterTot ? filterTot : field.BeschikbaarTot;
            var gapStart = effectiveStart;
            foreach (var occ in fieldOccs)
            {
                var occStart = occ.AanvangsTijd.AddMinutes(-PlannerShared.StandardBufferMinutes);
                if (occStart > gapStart)
                {
                    int gapMin = (int)(occStart.ToTimeSpan() - gapStart.ToTimeSpan()).TotalMinutes;
                    if (gapMin >= 30)
                        windows.Add(new BeschikbaarVenster
                        {
                            VeldNummer = field.VeldNummer,
                            VeldNaam = veldInfo?.VeldNaam ?? $"veld {field.VeldNummer}",
                            Van = gapStart.ToString("HH:mm"),
                            Tot = occStart.ToString("HH:mm"),
                            MaxDuurMinuten = gapMin,
                            Opmerking = !field.GebruikZonsondergang ? null : $"Zonsondergang {sunset:HH:mm}, geen kunstlicht"
                        });
                }
                gapStart = occ.EindTijd.AddMinutes(PlannerShared.StandardBufferMinutes);
            }
            if (gapStart < effectiveEnd)
            {
                int gapMin = (int)(effectiveEnd.ToTimeSpan() - gapStart.ToTimeSpan()).TotalMinutes;
                if (gapMin >= 30)
                    windows.Add(new BeschikbaarVenster
                    {
                        VeldNummer = field.VeldNummer,
                        VeldNaam = veldInfo?.VeldNaam ?? $"veld {field.VeldNummer}",
                        Van = gapStart.ToString("HH:mm"),
                        Tot = effectiveEnd.ToString("HH:mm"),
                        MaxDuurMinuten = gapMin,
                        Opmerking = !field.GebruikZonsondergang ? null : $"Zonsondergang {sunset:HH:mm}, geen kunstlicht"
                    });
            }
        }
        response.Beschikbaar = windows.Count > 0;
        response.BeschikbareVensters = windows;
        if (!response.Beschikbaar)
            response.Reden = $"Geen beschikbare vensters op {date.ToString("dddd d MMMM", PlannerShared.NL)}.";
        PlannerShared.AddWeekdayWarning(response, date);
        return response;
    }

    private static void AddSunsetWarning(CheckAvailabilityResponse response, CandidateSlot slot, TimeOnly? sunset, List<VeldInfo> velden)
    {
        if (!sunset.HasValue) return;
        var veld = velden.FirstOrDefault(v => v.VeldNummer == slot.VeldNummer);
        if (veld == null || veld.HeeftKunstlicht) return;
        var margin = (sunset.Value.ToTimeSpan() - slot.EindTijd.ToTimeSpan()).TotalMinutes;
        if (margin < PlannerShared.SunsetWarningMarginMinutes)
            response.Waarschuwingen.Add(
                $"Geen kunstlicht op {veld.VeldNaam}. Wedstrijd eindigt om {slot.EindTijd:HH:mm}, " +
                $"zonsondergang {sunset.Value:HH:mm} ({(int)margin} min marge).");
    }

    private static void AddNabijeWedstrijdWaarschuwing(
        CheckAvailabilityResponse response, CandidateSlot slot, int duurMinuten,
        List<BestaandeWedstrijd> occupations, List<VeldInfo> velden)
    {
        var slotStart = slot.AanvangsTijd;
        var slotEinde = slot.AanvangsTijd.AddMinutes(duurMinuten);
        var veldOccs  = occupations.Where(o => o.VeldNummer == slot.VeldNummer).ToList();
        var directErna = veldOccs
            .Where(o => o.AanvangsTijd >= slotEinde && o.AanvangsTijd <= slotEinde.AddMinutes(PlannerShared.StandardBufferMinutes + 5))
            .OrderBy(o => o.AanvangsTijd).FirstOrDefault();
        if (directErna != null)
        {
            int marge = (int)(directErna.AanvangsTijd - slotEinde).TotalMinutes;
            response.Waarschuwingen.Add(
                $"Let op: {directErna.Wedstrijd?.Trim() ?? ""} begint om {directErna.AanvangsTijd:HH:mm} op hetzelfde veld ({marge} min na einde).");
        }
        var directErvoor = veldOccs
            .Where(o => o.EindTijd <= slotStart && o.EindTijd >= slotStart.AddMinutes(-(PlannerShared.StandardBufferMinutes + 5)))
            .OrderByDescending(o => o.EindTijd).FirstOrDefault();
        if (directErvoor != null)
        {
            int marge = (int)(slotStart - directErvoor.EindTijd).TotalMinutes;
            response.Waarschuwingen.Add(
                $"Let op: {directErvoor.Wedstrijd?.Trim() ?? ""} eindigt om {directErvoor.EindTijd:HH:mm} op hetzelfde veld ({marge} min voor aanvang).");
        }
    }
}
