using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner;

/// <summary>
/// Use-case service voor automatisch inplannen.
/// Extracted uit PlannerService (#475).
/// </summary>
internal static class AutoPlanService
{
    public static async Task<AutoPlanResponse> AutoPlanAsync(
        AutoPlanRequest request, string clubCode, ILogger log)
    {
        bool isAllstars = clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase);
        int buffer = request.BufferMinuten ?? PlannerShared.StandardBufferMinutes;

        if (!DateOnly.TryParse(request.Datum, out var datum))
            return new AutoPlanResponse { Datum = request.Datum };

        var alleWedstrijden = await PlannerDataAccess.GetAllMatchesForDatumAsync(datum, clubCode);

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

        var speeltijden    = await PlannerDataAccess.GetSpeeltijdenLookupAsync();
        var veldInfoLookup = velden.ToDictionary(v => v.VeldNummer);
        int dagVanWeek     = datum.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)datum.DayOfWeek;
        var voorkeurLookup = await PlannerDataAccess.GetVoorkeurTijdenLookupAsync(dagVanWeek, clubCode);
        var teamBuffers    = await PlannerDataAccess.GetAllTeamBuffersAsync();

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

        var scheduler = new FieldScheduler(beschikbaarheid, velden, buffer, teamBuffers);
        var items = new List<AutoPlanWedstrijdItem>();

        foreach (var wedstrijd in gesorteerd)
        {
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
                    OptimaalVeld = wedstrijd.Veld,
                    OptimaalTijd = wedstrijd.AanvangsTijd,
                    Status = "onbekend-team",
                });
                continue;
            }

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

            var optimaalVeldNaam = veldInfoLookup.TryGetValue(slot.VeldNummer, out var vi) ? vi.VeldNaam : $"veld {slot.VeldNummer}";
            var optimaalVeld = BuildSportlinkVeldString(optimaalVeldNaam, slot.VeldSubpositie);
            var optimaalTijd = slot.AanvangsTijd.ToString("HH:mm");
            var huidigeVeldNorm  = NormaliseerVeld(wedstrijd.Veld);
            var optimaalVeldNorm = NormaliseerVeld(optimaalVeld);
            bool heeftVeld = !string.IsNullOrWhiteSpace(wedstrijd.Veld);
            bool heeftTijd = !string.IsNullOrWhiteSpace(wedstrijd.AanvangsTijd);
            bool tijdWijzigt = wedstrijd.AanvangsTijd?.Trim() != optimaalTijd;
            bool veldWijzigt = huidigeVeldNorm != optimaalVeldNorm;
            string status = (!heeftVeld || !heeftTijd) ? "nieuw-slot" : (tijdWijzigt || veldWijzigt) ? "wijziging" : "ongewijzigd";

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

        int zonderVeld     = items.Count(i => !i.HeeftVeld);
        int zonderTijd     = items.Count(i => !i.HeeftTijd);
        int teWijzigen     = items.Count(i => i.Status is "nieuw-slot" or "wijziging");
        int nietInplanbaar = items.Count(i => i.Status == "niet-inplanbaar");

        var eindTijden = items
            .Where(i => i.OptimaalTijd != null && i.DuurMinuten > 0 && TimeOnly.TryParse(i.OptimaalTijd, out _))
            .Select(i => TimeOnly.Parse(i.OptimaalTijd!).AddMinutes(i.DuurMinuten)).ToList();
        string? eindTijd = eindTijden.Count > 0 ? eindTijden.Max().ToString("HH:mm") : null;

        var huidigeOccupations = items
            .Where(i => i.HeeftVeld && i.HeeftTijd && i.OptimaalVeldNummer.HasValue)
            .Select(i =>
            {
                var huidigVeldNr = velden.FirstOrDefault(v =>
                    NormaliseerVeld(v.VeldNaam) == NormaliseerVeld(i.HuidigeVeld?.Split(' ').Take(2).LastOrDefault() ?? ""))?.VeldNummer ?? 0;
                if (huidigVeldNr == 0) return null;
                if (!TimeOnly.TryParse(i.HuidigeTijd, out var aTime)) return null;
                return new BestaandeWedstrijd
                {
                    Datum = datum, AanvangsTijd = aTime, EindTijd = aTime.AddMinutes(i.DuurMinuten),
                    VeldNummer = huidigVeldNr, VeldDeelGebruik = i.Veldafmeting > 0 ? i.Veldafmeting : 1m,
                    LeeftijdsCategorie = i.LeeftijdsCategorie, TeamNaam = i.TeamNaam, Wedstrijd = i.Wedstrijd, Bron = "Sportlink"
                };
            }).Where(o => o != null).Cast<BestaandeWedstrijd>().ToList();

        var optimaleOccupations = items
            .Where(i => i.OptimaalVeldNummer.HasValue && i.OptimaalTijd != null && TimeOnly.TryParse(i.OptimaalTijd, out _))
            .Select(i => new BestaandeWedstrijd
            {
                Datum = datum, AanvangsTijd = TimeOnly.Parse(i.OptimaalTijd!),
                EindTijd = TimeOnly.Parse(i.OptimaalTijd!).AddMinutes(i.DuurMinuten),
                VeldNummer = i.OptimaalVeldNummer!.Value, VeldDeelGebruik = i.Veldafmeting > 0 ? i.Veldafmeting : 1m,
                VeldSubpositie = i.OptimaalVeld?.Contains(' ') == true ? i.OptimaalVeld.Split(' ').LastOrDefault() : null,
                LeeftijdsCategorie = i.LeeftijdsCategorie, TeamNaam = i.TeamNaam, Wedstrijd = i.Wedstrijd, Bron = "Optimaal"
            }).ToList();

        string huidigeHtml  = PlannerHtmlGenerator.GenereerHtml(datum, huidigeOccupations, new List<OptimalisatieSuggestie>(), velden, "huidig");
        string optimaleHtml = PlannerHtmlGenerator.GenereerHtml(datum, optimaleOccupations, new List<OptimalisatieSuggestie>(), velden, "optimaal");

        log.LogInformation("AutoPlan {Datum}: {Totaal} wedstrijden, {Wijzigen} te wijzigen, eindtijd {Eind}",
            datum, items.Count, teWijzigen, eindTijd ?? "?");

        return new AutoPlanResponse
        {
            Datum = request.Datum, TotaalWedstrijden = items.Count,
            ZonderVeld = zonderVeld, ZonderTijd = zonderTijd,
            TeWijzigen = teWijzigen, NietInplanbaar = nietInplanbaar,
            GeschatteEindTijd = eindTijd, Wedstrijden = items,
            HuidigeHtml = huidigeHtml, OptimaleHtml = optimaleHtml
        };
    }

    public static async Task<AutoPlanToepassenResponse> AutoPlanToepassenAsync(
        AutoPlanToepassenRequest request, string clubCode, ILogger log)
    {
        if (!clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Toepassen is alleen beschikbaar in testmodus (ALLSTARS).");

        var planResponse = await AutoPlanAsync(new AutoPlanRequest { Datum = request.Datum, BufferMinuten = request.BufferMinuten }, clubCode, log);
        var response = new AutoPlanToepassenResponse();

        foreach (var item in planResponse.Wedstrijden)
        {
            if (item.Status == "ongewijzigd") continue;
            if (item.Status == "niet-inplanbaar") continue;
            if (item.Status == "onbekend-team") continue;
            if (item.WedstrijdCode == null) continue;
            if (item.OptimaalVeld == null || item.OptimaalTijd == null) continue;
            try
            {
                int updated = await PlannerDataAccess.UpdateAllstarsMatchAsync(item.WedstrijdCode.Value, item.OptimaalVeld, item.OptimaalTijd);
                if (updated > 0) response.Bijgewerkt++;
                else { response.Mislukt++; response.Fouten.Add($"{item.Wedstrijd}: wedstrijdcode {item.WedstrijdCode} niet gevonden"); }
            }
            catch (Exception ex)
            {
                response.Mislukt++;
                log.LogError(ex, "AutoPlan: fout bij toepassen wedstrijd {Wedstrijd} ({Code})", item.Wedstrijd, item.WedstrijdCode);
                response.Fouten.Add($"{item.Wedstrijd}: technische fout bij toepassen — zie logs");
            }
        }
        log.LogInformation("AutoPlan toepassen {Datum}: {Bijgewerkt} bijgewerkt, {Mislukt} mislukt",
            request.Datum, response.Bijgewerkt, response.Mislukt);
        return response;
    }

    // ── Privé helpers ──

    private static string? ExtractLeeftijdFromTeamNaam(string? teamNaam)
    {
        if (string.IsNullOrWhiteSpace(teamNaam)) return null;
        var parts = teamNaam.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var second = parts[1];
        var hyphenIdx = second.IndexOf('-');
        if (hyphenIdx > 0) second = second[..hyphenIdx];
        return second.ToUpperInvariant() switch
        {
            "HEREN" => "1-99", "DAMES" => "VR", "VROUWEN" => "VR",
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
        return 90;
    }

    private static int GetDefaultTimeSortKey(string? leeftijd)
    {
        var order = GetLeeftijdSortOrder(leeftijd);
        return order <= 11 ? 540 : order <= 13 ? 600 : order <= 15 ? 630 : order <= 17 ? 660
             : order <= 19 ? 690 : order <= 25 ? 720 : order <= 85 ? 750 : 780;
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
}
