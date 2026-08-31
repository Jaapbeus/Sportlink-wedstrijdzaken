using Planner.Shared;
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

        // Speeltijden per club, met terugval op de primaire club (#573/#666): oudere databases hebben
        // nog geen ALLSTARS-rijen, en zonder speeltijden is géén enkele wedstrijd inplanbaar.
        var speeltijden    = await GetSpeeltijdenMetTerugvalAsync(clubCode);
        var veldInfoLookup = velden.ToDictionary(v => v.VeldNummer);
        int dagVanWeek     = datum.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)datum.DayOfWeek;
        var voorkeurLookup = await PlannerDataAccess.GetVoorkeurTijdenLookupAsync(dagVanWeek, clubCode);

        // TeamRegels expliciet op de opgevraagde club (#666). Dit stond eerder op de primaire club met
        // de aanname "er zijn geen ALLSTARS-rijen" (#573). Die aanname klopt niet meer: de demomodus
        // heeft inmiddels eigen teamregels, en die werden daardoor stilzwijgend genegeerd — buffers en
        // voorkeursveld hadden in testmodus dus geen effect, precies de modus waarin je het test.
        // Géén terugval hier: een leeg resultaat betekent gewoon "dit team heeft geen regels".
        var teamBuffers    = await PlannerDataAccess.GetAllTeamBuffersAsync(clubCode);
        var voorkeurVelden = await PlannerDataAccess.GetAllTeamVoorkeurVeldenAsync(clubCode);

        // Per wedstrijd het planningsdoel bepalen volgens de vastgelegde rangorde (#666):
        //   1. Regels     — dbo.TeamRegels: buffers (altijd van kracht) en VoorkeurVeld (veld + evt. tijd)
        //   2. Voorkeuren — dbo.TeamVoorkeurTijden voor deze speeldag
        //   3. Defaults   — dbo.Speeltijden.StandaardVoorkeurTijd per leeftijdscategorie
        // Binnen elke laag beslist Prioriteit (laag getal = belangrijker) wie zijn doel als eerste mag
        // claimen. Dát is wat conflicten tussen teams oplost: wie eerder verwerkt wordt, krijgt de plek.
        var doelen = alleWedstrijden.ToDictionary(
            w => w,
            w => BepaalPlanDoel(w, isAllstars, voorkeurVelden, voorkeurLookup, speeltijden));

        var gesorteerd = alleWedstrijden
            .OrderBy(w => doelen[w].Laag)          // regels vóór voorkeuren vóór defaults
            .ThenBy(w => doelen[w].Prioriteit)     // laag getal = belangrijker
            .ThenBy(w => doelen[w].DoelTijd.HasValue
                ? (int)doelen[w].DoelTijd!.Value.ToTimeSpan().TotalMinutes
                : GetDefaultTimeSortKey(doelen[w].Leeftijd.Length > 0 ? doelen[w].Leeftijd : null))
            .ThenBy(w => GetLeeftijdSortOrder(doelen[w].Leeftijd))
            .ThenBy(w => w.TeamNaam)
            .ToList();

        var scheduler = new FieldScheduler(beschikbaarheid, velden, buffer, teamBuffers);
        var items = new List<AutoPlanWedstrijdItem>();

        foreach (var wedstrijd in gesorteerd)
        {
            var doel = doelen[wedstrijd];
            var leeftijd = doel.Leeftijd;
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

            if (doel.DoelTijd.HasValue)
            {
                // Streeftijd bekend (uit een regel, een teamvoorkeur of de leeftijdsdefault) — plan zo
                // dicht mogelijk daarbij, en probeer daarbij eerst het voorkeursveld als dat gezet is.
                var doelTijd = doel.DoelTijd.Value;
                voorkeurTijdStr = doelTijd.ToString("HH:mm");
                slot = scheduler.FindAndOccupyNearTime(doelTijd, speeltijdInfo.Veldafmeting,
                    speeltijdInfo.WedstrijdTotaal, teamBufVoor, wedstrijd.TeamNaam,
                    voorkeurVeldNummer: doel.VoorkeurVeldNummer);
                if (slot != null)
                    voorkeurAfwijking = (int)(slot.AanvangsTijd.ToTimeSpan() - doelTijd.ToTimeSpan()).TotalMinutes;
            }
            else
            {
                // Geen streeftijd bekend: eerst beschikbare gat, zoals voorheen. Een voorkeursveld
                // zonder tijd wordt nog steeds gerespecteerd zolang het veld ruimte heeft.
                slot = doel.VoorkeurVeldNummer.HasValue
                    ? scheduler.FindAndOccupyNearTime(FieldScheduler.DagStart, speeltijdInfo.Veldafmeting,
                        speeltijdInfo.WedstrijdTotaal, teamBufVoor, wedstrijd.TeamNaam,
                        voorkeurVeldNummer: doel.VoorkeurVeldNummer)
                    : scheduler.FindAndOccupyNextSlot(speeltijdInfo.Veldafmeting, speeltijdInfo.WedstrijdTotaal,
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
                VoorkeurAfwijkingMinuten = voorkeurAfwijking,
                VoorkeurBron = doel.Bron,
                VoorkeurStatus = BepaalVoorkeurStatus(voorkeurTijdStr, voorkeurAfwijking),
                VoorkeurVeldNummer = doel.VoorkeurVeldNummer,
                VoorkeurVeldToegepast = doel.VoorkeurVeldNummer.HasValue
                    ? slot.VeldNummer == doel.VoorkeurVeldNummer.Value
                    : null
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

        // De vier clubinstellingen die de (nu gedeelde) HTML-generator nodig heeft — §42. De
        // verplichte-instelling-controle staat hier, bij de aanroeper die de cache leest, in plaats
        // van halverwege het renderen.
        var htmlInstellingen = new HtmlInstellingen(
            Accommodatie: SystemUtilities.AppSettings.GetSetting("accommodatie") ?? "",
            PlannerAfzenderNaam: SystemUtilities.AppSettings.GetSetting("plannerAfzenderNaam")
                ?? throw new InvalidOperationException("Vereiste instelling 'plannerAfzenderNaam' ontbreekt in dbo.AppSettings"),
            EersteElftalNaam: SystemUtilities.AppSettings.GetSetting("eersteElftalNaam"),
            ClubCode: SystemUtilities.AppSettings.GetOptionalClubCode());

        string huidigeHtml  = PlannerHtmlGenerator.GenereerHtml(datum, huidigeOccupations, new List<OptimalisatieSuggestie>(), velden, "huidig", htmlInstellingen);
        string optimaleHtml = PlannerHtmlGenerator.GenereerHtml(datum, optimaleOccupations, new List<OptimalisatieSuggestie>(), velden, "optimaal", htmlInstellingen);

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

    // Lichtgewicht "wat staat er nu gepland"-weergave (#566): geen FieldScheduler-berekening,
    // alleen de ruwe wedstrijddata die AutoPlanAsync anders ook al zonder optimalisatie zou tonen.
    // Duur/veldafmeting komen uit de goedkope speeltijden-lookup (één simpele SELECT) — niet uit
    // de FieldScheduler, die pas nodig is zodra er ook daadwerkelijk geoptimaliseerd wordt.
    public static async Task<List<VeldbezettingItem>> VeldbezettingAsync(DateOnly datum, string clubCode)
    {
        bool isAllstars = clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase);
        var wedstrijden = await PlannerDataAccess.GetAllMatchesForDatumAsync(datum, clubCode);
        // Eigen club met terugval op de primaire club — zelfde bron als AutoPlanAsync (#573/#666),
        // zodat de duur die hier getoond wordt overeenkomt met waarmee de planner rekent.
        var speeltijden = await GetSpeeltijdenMetTerugvalAsync(clubCode);

        return wedstrijden
            .Select(w =>
            {
                var leeftijd = (!string.IsNullOrWhiteSpace(w.LeeftijdsCategorie))
                    ? w.LeeftijdsCategorie
                    : (isAllstars ? ExtractLeeftijdFromTeamNaam(w.TeamNaam) ?? "" : "");
                speeltijden.TryGetValue(leeftijd, out var speeltijdInfo);

                return new VeldbezettingItem
                {
                    WedstrijdCode = w.WedstrijdCode,
                    Wedstrijd = w.Wedstrijd,
                    TeamNaam = w.TeamNaam,
                    Uitteam = w.Uitteam,
                    AanvangsTijd = w.AanvangsTijd,
                    Veld = w.Veld,
                    Competitiesoort = w.Competitiesoort,
                    LeeftijdsCategorie = w.LeeftijdsCategorie,
                    DuurMinuten = speeltijdInfo?.WedstrijdTotaal ?? 0,
                    Veldafmeting = speeltijdInfo?.Veldafmeting ?? 1.00m
                };
            })
            .OrderBy(w => string.IsNullOrWhiteSpace(w.AanvangsTijd) ? "99:99" : w.AanvangsTijd)
            .ToList();
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

    /// <summary>
    /// Speeltijden van de opgevraagde club; is die tabel voor deze club leeg, dan de primaire club.
    /// De terugval bestaat omdat zonder speeltijden geen enkele wedstrijd een duur of veldafmeting
    /// heeft en de hele dag als "onbekend-team" terugkomt (#573/#666).
    /// </summary>
    private static async Task<Dictionary<string, Speeltijd>> GetSpeeltijdenMetTerugvalAsync(string clubCode)
    {
        var eigen = await PlannerDataAccess.GetSpeeltijdenLookupAsync(clubCode);
        if (eigen.Count > 0) return eigen;
        return await PlannerDataAccess.GetSpeeltijdenLookupAsync();
    }

    // ── Planningsdoel en pure helpers (#666) ──
    //
    // De regels zelf staan sinds issue 888 vervolg (§42) in Planner.Shared/AutoPlanRegels.cs: ze
    // raken geen database, geen instellingencache en geen tier-specifiek type, en de Postgres-tier
    // heeft ze net zo hard nodig. Wat hieronder staat zijn dunne delegaties — zelfde patroon als
    // NormaliseerVeld (#819) en PlannerShared na §38: geen enkele aanroeper of test hoefde te
    // wijzigen, alleen de plek waar de logica woont.

    internal static PlanDoel BepaalPlanDoel(
        WedstrijdRaw wedstrijd,
        bool isAllstars,
        Dictionary<string, TeamVoorkeurVeld> voorkeurVelden,
        Dictionary<string, List<(TimeOnly Tijd, int Prioriteit)>> voorkeurLookup,
        Dictionary<string, Speeltijd> speeltijden)
        => AutoPlanRegels.BepaalPlanDoel(
            wedstrijd.TeamNaam, wedstrijd.LeeftijdsCategorie, isAllstars,
            voorkeurVelden, voorkeurLookup, speeltijden);

    /// <inheritdoc cref="AutoPlanRegels.BepaalVoorkeurStatus"/>
    internal static string BepaalVoorkeurStatus(string? voorkeurTijd, int? afwijkingMinuten)
        => AutoPlanRegels.BepaalVoorkeurStatus(voorkeurTijd, afwijkingMinuten);

    /// <inheritdoc cref="AutoPlanRegels.ExtractLeeftijdFromTeamNaam"/>
    internal static string? ExtractLeeftijdFromTeamNaam(string? teamNaam)
        => AutoPlanRegels.ExtractLeeftijdFromTeamNaam(teamNaam);

    /// <inheritdoc cref="AutoPlanRegels.GetLeeftijdSortOrder"/>
    internal static int GetLeeftijdSortOrder(string? leeftijd)
        => AutoPlanRegels.GetLeeftijdSortOrder(leeftijd);

    /// <inheritdoc cref="AutoPlanRegels.GetDefaultTimeSortKey"/>
    internal static int GetDefaultTimeSortKey(string? leeftijd)
        => AutoPlanRegels.GetDefaultTimeSortKey(leeftijd);

    /// <inheritdoc cref="AutoPlanRegels.BuildSportlinkVeldString"/>
    internal static string BuildSportlinkVeldString(string veldNaam, string subpositie)
        => AutoPlanRegels.BuildSportlinkVeldString(veldNaam, subpositie);

    /// <remarks>
    /// #819: dunne delegatie naar het tier-agnostische <c>Planner.Shared.VeldNormalisatie</c> —
    /// zie de toelichting bij <see cref="PlannerShared.ResolveVeld(string?, IEnumerable{ValueTuple{string?, int}})"/>.
    /// Gedrag ongewijzigd.
    /// </remarks>
    internal static string NormaliseerVeld(string? veld)
        => global::Planner.Shared.VeldNormalisatie.Normaliseer(veld);
}
