using FunctionApp.Postgres.Planner.Repositories;
using Microsoft.Extensions.Logging;
using Planner.Shared;

namespace FunctionApp.Postgres.Planner;

internal sealed record VeldbezettingItem(
    long? WedstrijdCode, string Wedstrijd, string TeamNaam, string? Uitteam,
    string? AanvangsTijd, string? Veld, string? Competitiesoort, string? LeeftijdsCategorie,
    int DuurMinuten, decimal Veldafmeting);

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/Services/AutoPlanService.cs</c> (#888).
/// Sinds issue 888 vervolg (§42) volledig: naast <see cref="VeldbezettingAsync"/> (de
/// "lichtgewicht weergave zonder FieldScheduler-berekening", #566) nu ook
/// <see cref="AutoPlanAsync"/> en <see cref="AutoPlanToepassenAsync"/> — de laatste twee
/// 501-stubs van deze tier.
///
/// <para>
/// <b>De planningsregels zelf staan niet hier maar in <see cref="AutoPlanRegels"/></b>
/// (<c>Planner.Shared</c>): de rangorde regels → voorkeuren → defaults (#666), de sorteersleutels
/// en de voorkeurstatus zijn pure rekenlogica zonder tier-afhankelijkheid. Deze klasse is de
/// orchestratie eromheen: data ophalen, de gedeelde <see cref="FieldScheduler"/> voeden, en het
/// resultaat naar de wire-DTO's schrijven.
/// </para>
///
/// <para>
/// <b>Bewust géén <c>ToDictionary(w =&gt; w, …)</c> op de wedstrijden.</b> Het SQL Server-origineel
/// bouwt een dictionary met de wedstrijd zelf als sleutel; dat is daar veilig omdat
/// <c>WedstrijdRaw</c> een class is (referentie-gelijkheid). Op deze tier is
/// <see cref="WedstrijdRaw"/> een <c>record</c> met waarde-gelijkheid: twee wedstrijden met
/// identieke velden zouden dan dezelfde sleutel zijn en de dictionary gooit een
/// <see cref="ArgumentException"/>. Hier wordt daarom een lijst van paren gesorteerd.
/// <br/>
/// <b>Eerlijk over de reikwijdte:</b> vandaag is die botsing niet bereikbaar — de query selecteert
/// <c>wedstrijdcode</c>, en <c>his.matches</c> heeft daar een unieke business key op, dus twee
/// records verschillen altijd in minstens dat veld. Dit is dus een defensieve keuze, geen
/// bugfix: hij hangt niet af van een invariant die drie lagen verderop staat en die een
/// toekomstige wijziging in de SELECT (een kolom laten vallen) stilzwijgend zou breken.
/// </para>
/// </summary>
internal static class AutoPlanService
{
    /// <summary>
    /// De dagplanning-optimalisatie (#666): bepaalt per wedstrijd een planningsdoel, verwerkt ze in
    /// de vastgelegde rangorde, en laat de gedeelde <see cref="FieldScheduler"/> er slots bij
    /// zoeken. Postgres-vertaling van het SQL Server-origineel (§42).
    /// </summary>
    internal static async Task<AutoPlanResponse> AutoPlanAsync(
        string connectionString, AutoPlanRequest request, string clubCode, ILogger log)
    {
        bool isAllstars = clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase);
        int buffer = request.BufferMinuten ?? PlannerShared.StandardBufferMinutes;

        if (!DateOnly.TryParse(request.Datum, out var datum))
            return new AutoPlanResponse { Datum = request.Datum };

        var alleWedstrijden = await AllstarsTestDataRepository.GetAllMatchesForDatumAsync(connectionString, datum, clubCode);

        List<VeldInfo> velden;
        List<VeldBeschikbaarheidInfo> beschikbaarheid;
        if (isAllstars)
        {
            velden = await AllstarsTestDataRepository.GetAllstarsVeldenAsync(connectionString);
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
            velden = await PlannerSettingsRepository.GetVeldenAsync(connectionString, clubCode);
            beschikbaarheid = await PlannerAvailabilityRepository.GetAvailableFieldsAsync(connectionString, datum, clubCode);
        }

        // Speeltijden per club, met terugval op de primaire club (#573/#666).
        var speeltijden = await GetSpeeltijdenMetTerugvalAsync(connectionString, clubCode);
        var veldInfoLookup = velden.ToDictionary(v => v.VeldNummer);
        int dagVanWeek = datum.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)datum.DayOfWeek;
        var voorkeurLookup = await PlannerSettingsRepository.GetVoorkeurTijdenLookupAsync(connectionString, dagVanWeek, clubCode);

        // TeamRegels expliciet op de opgevraagde club (#666) — géén terugval: leeg betekent
        // gewoon "dit team heeft geen regels".
        var teamBuffers = await TeamRulesRepository.GetAllTeamBuffersAsync(connectionString, clubCode);
        var voorkeurVelden = await TeamRulesRepository.GetAllTeamVoorkeurVeldenAsync(connectionString, clubCode);

        // Paren i.p.v. een dictionary — zie de klasse-doc-comment over record-waardegelijkheid.
        var metDoel = alleWedstrijden
            .Select(w => (Wedstrijd: w, Doel: AutoPlanRegels.BepaalPlanDoel(
                w.TeamNaam, w.LeeftijdsCategorie, isAllstars, voorkeurVelden, voorkeurLookup, speeltijden)))
            .OrderBy(p => p.Doel.Laag)                 // regels vóór voorkeuren vóór defaults
            .ThenBy(p => p.Doel.Prioriteit)            // laag getal = belangrijker
            .ThenBy(p => p.Doel.DoelTijd.HasValue
                ? (int)p.Doel.DoelTijd!.Value.ToTimeSpan().TotalMinutes
                : AutoPlanRegels.GetDefaultTimeSortKey(p.Doel.Leeftijd.Length > 0 ? p.Doel.Leeftijd : null))
            .ThenBy(p => AutoPlanRegels.GetLeeftijdSortOrder(p.Doel.Leeftijd))
            .ThenBy(p => p.Wedstrijd.TeamNaam)
            .ToList();

        var scheduler = new FieldScheduler(beschikbaarheid, velden, buffer, teamBuffers);
        var items = new List<AutoPlanWedstrijdItem>();

        foreach (var (wedstrijd, doel) in metDoel)
        {
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
            var optimaalVeld = AutoPlanRegels.BuildSportlinkVeldString(optimaalVeldNaam, slot.VeldSubpositie);
            var optimaalTijd = slot.AanvangsTijd.ToString("HH:mm");
            var huidigeVeldNorm = VeldNormalisatie.Normaliseer(wedstrijd.Veld);
            var optimaalVeldNorm = VeldNormalisatie.Normaliseer(optimaalVeld);
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
                VoorkeurStatus = AutoPlanRegels.BepaalVoorkeurStatus(voorkeurTijdStr, voorkeurAfwijking),
                VoorkeurVeldNummer = doel.VoorkeurVeldNummer,
                VoorkeurVeldToegepast = doel.VoorkeurVeldNummer.HasValue
                    ? slot.VeldNummer == doel.VoorkeurVeldNummer.Value
                    : null
            });
        }

        int zonderVeld = items.Count(i => !i.HeeftVeld);
        int zonderTijd = items.Count(i => !i.HeeftTijd);
        int teWijzigen = items.Count(i => i.Status is "nieuw-slot" or "wijziging");
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
                    VeldNormalisatie.Normaliseer(v.VeldNaam) == VeldNormalisatie.Normaliseer(i.HuidigeVeld?.Split(' ').Take(2).LastOrDefault() ?? ""))?.VeldNummer ?? 0;
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

        var htmlInstellingen = BouwHtmlInstellingen();
        string huidigeHtml = PlannerHtmlGenerator.GenereerHtml(datum, huidigeOccupations, new List<OptimalisatieSuggestie>(), velden, "huidig", htmlInstellingen);
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

    /// <summary>
    /// Schrijft het AutoPlan-resultaat terug op de demowedstrijden. Uitsluitend in testmodus
    /// (ALLSTARS) — zelfde harde grens als het SQL Server-origineel: dit pad schrijft naar
    /// <c>his.matches</c> en mag nooit echte, gesynchroniseerde clubdata aanraken.
    /// </summary>
    internal static async Task<AutoPlanToepassenResponse> AutoPlanToepassenAsync(
        string connectionString, AutoPlanToepassenRequest request, string clubCode, ILogger log)
    {
        if (!clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Toepassen is alleen beschikbaar in testmodus (ALLSTARS).");

        var planResponse = await AutoPlanAsync(
            connectionString, new AutoPlanRequest { Datum = request.Datum, BufferMinuten = request.BufferMinuten }, clubCode, log);
        var response = new AutoPlanToepassenResponse();

        foreach (var item in planResponse.Wedstrijden)
        {
            if (item.Status is "ongewijzigd" or "niet-inplanbaar" or "onbekend-team") continue;
            if (item.WedstrijdCode == null) continue;
            if (item.OptimaalVeld == null || item.OptimaalTijd == null) continue;
            try
            {
                int updated = await AllstarsTestDataRepository.UpdateAllstarsMatchAsync(
                    connectionString, item.WedstrijdCode.Value, item.OptimaalVeld, item.OptimaalTijd);
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
    /// De vier clubinstellingen voor de gedeelde HTML-generator (§42), uit de Postgres-cache.
    /// <para>
    /// <c>eersteElftalNaam</c> bestaat op geen van beide tiers als opgeslagen instelling — de
    /// generator valt daar zelf terug op <c>clubCode + " 1"</c>, precies zoals op de SQL
    /// Server-tier. Hier dus bewust <c>null</c> en geen verzonnen sleutel.
    /// </para>
    /// </summary>
    private static HtmlInstellingen BouwHtmlInstellingen() => new(
        Accommodatie: PostgresAppSettings.GetSetting("accommodatie") ?? "",
        PlannerAfzenderNaam: PostgresAppSettings.GetSetting("plannerAfzenderNaam")
            ?? throw new InvalidOperationException(
                "Vereiste instelling 'plannerafzendernaam' ontbreekt in public.appsettings"),
        EersteElftalNaam: null,
        ClubCode: PostgresAppSettings.GetSetting("clubCode"));

    internal static async Task<List<VeldbezettingItem>> VeldbezettingAsync(
        string connectionString, DateOnly datum, string clubCode)
    {
        bool isAllstars = clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase);
        var wedstrijden = await AllstarsTestDataRepository.GetAllMatchesForDatumAsync(connectionString, datum, clubCode);
        var speeltijden = await GetSpeeltijdenMetTerugvalAsync(connectionString, clubCode);

        return wedstrijden
            .Select(w =>
            {
                var leeftijd = !string.IsNullOrWhiteSpace(w.LeeftijdsCategorie)
                    ? w.LeeftijdsCategorie
                    : (isAllstars ? ExtractLeeftijdFromTeamNaam(w.TeamNaam) ?? "" : "");
                speeltijden.TryGetValue(leeftijd, out var speeltijdInfo);

                return new VeldbezettingItem(
                    WedstrijdCode: w.WedstrijdCode,
                    Wedstrijd: w.Wedstrijd,
                    TeamNaam: w.TeamNaam,
                    Uitteam: w.Uitteam,
                    AanvangsTijd: w.AanvangsTijd,
                    Veld: w.Veld,
                    Competitiesoort: w.Competitiesoort,
                    LeeftijdsCategorie: w.LeeftijdsCategorie,
                    DuurMinuten: speeltijdInfo?.WedstrijdTotaal ?? 0,
                    Veldafmeting: speeltijdInfo?.Veldafmeting ?? 1.00m);
            })
            .OrderBy(w => string.IsNullOrWhiteSpace(w.AanvangsTijd) ? "99:99" : w.AanvangsTijd)
            .ToList();
    }

    private static async Task<Dictionary<string, Speeltijd>> GetSpeeltijdenMetTerugvalAsync(
        string connectionString, string clubCode)
    {
        var eigen = await PlannerSettingsRepository.GetSpeeltijdenLookupAsync(connectionString, clubCode);
        if (eigen.Count > 0) return eigen;

        var primair = PostgresAppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in public.appsettings");
        return await PlannerSettingsRepository.GetSpeeltijdenLookupAsync(connectionString, primair);
    }

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
}
