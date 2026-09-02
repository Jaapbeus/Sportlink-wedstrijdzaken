using Planner.Shared;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner;

/// <summary>
/// Use-case service voor beschikbaarheidscontroles.
/// Extracted uit PlannerService (#475).
///
/// <paramref name="clubCode"/> is optioneel: kanalen zonder clubcontext (e-mailflow,
/// timer-triggers) vallen terug op de primaire club van deze deployment. Alle
/// onderliggende queries zijn hard gescoped — zie <see cref="ClubScope"/> (#573, #580).
/// </summary>
internal static class AvailabilityService
{
    public static async Task<CheckAvailabilityResponse> CheckAvailabilityAsync(
        CheckAvailabilityRequest request, ILogger log, string? clubCode = null)
    {
        var response = new CheckAvailabilityResponse();
        clubCode = ClubScope.Resolve(clubCode);

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

        var duurBepaling = await BepaalDuurEnVeldfractieAsync(request, clubCode);
        if (duurBepaling.Reden != null)
        {
            response.Reden = duurBepaling.Reden;
            return response;
        }
        if (duurBepaling.Waarschuwing != null)
            response.Waarschuwingen.Add(duurBepaling.Waarschuwing);
        int duurMinuten = duurBepaling.Duur;
        decimal veldFractie = duurBepaling.VeldFractie;

        if (!string.IsNullOrEmpty(request.TeamNaam))
        {
            var teamWedstrijden = await PlannerDataAccess.GetTeamMatchesOnDateAsync(request.TeamNaam, date, clubCode);
            if (!teamWedstrijden.TeamHerkend)
            {
                // Niet stilzwijgend doorlopen (#945): zonder herkend team is er niets vergeleken, en
                // dat mag nooit als "geen conflict" lezen. De aanvraag wordt niet geweigerd — de
                // beheerder plant vaker een team in dat de teamlijst nog niet kent — maar het
                // ontbreken van de controle staat wel in het antwoord.
                response.Waarschuwingen.Add(
                    $"'{request.TeamNaam}' staat niet in de teamlijst. Er is NIET gecontroleerd of dit " +
                    "team die dag al een wedstrijd heeft — controleer dat zelf.");
            }
            else if (teamWedstrijden.Wedstrijden.Count > 0)
            {
                var conflict = teamWedstrijden.Wedstrijden[0];
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

        var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date, clubCode);
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

        var occupations = await SportlinkApiClient.GetFieldOccupationsWithApiAsync(date, log, clubCode);
        var velden      = await PlannerDataAccess.GetVeldenAsync(clubCode);
        var teamRules   = new List<TeamRegel>();
        if (!string.IsNullOrEmpty(request.TeamNaam))
            teamRules = await PlannerDataAccess.GetTeamRulesAsync(request.TeamNaam, clubCode);

        // Één bulkquery voor alle bezette teams i.p.v. één query per team (#575)
        var allTeamRules = await PlannerDataAccess.GetTeamRulesForTeamsAsync(
            occupations.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!), clubCode);

        var sunset = await BepaalEnPasZonsondergangToeAsync(date, availableFields);

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

        (TimeOnly dagdeelVan, TimeOnly dagdeelTot) = BepaalDagdeelVenster(request.Dagdeel, new TimeOnly(8, 30), new TimeOnly(22, 0));

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
                response.Toewijzing = ToSlotMetVeldType(date, exactMatch, duurMinuten, velden);
                response.BeschikbareVensters = venstersResponse.BeschikbareVensters;
                AddSunsetWarning(response, exactMatch, sunset, velden);
                AddNabijeWedstrijdWaarschuwing(response, exactMatch, duurMinuten, occupations, velden);
                PlannerShared.AddWeekdayWarning(response.Waarschuwingen, date);
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
                response.Alternatieven = alternatives.Prepend(best).Select(c => ToSlotMetVeldType(date, c, duurMinuten, velden)).Take(3).ToList();
                response.BeschikbareVensters = venstersResponse.BeschikbareVensters;
                PlannerShared.AddWeekdayWarning(response.Waarschuwingen, date);
            }
            else
            {
                response.Beschikbaar = true;
                response.Toewijzing = ToSlotMetVeldType(date, best, duurMinuten, velden);
                AddNabijeWedstrijdWaarschuwing(response, best, duurMinuten, occupations, velden);
                response.Alternatieven = alternatives.Select(c => ToSlotMetVeldType(date, c, duurMinuten, velden)).ToList();
                response.BeschikbareVensters = venstersResponse.BeschikbareVensters;
                AddSunsetWarning(response, best, sunset, velden);
                PlannerShared.AddWeekdayWarning(response.Waarschuwingen, date);
            }
        }
        else
        {
            response.Reden = $"Geen beschikbaar veld gevonden op {date.ToString("dddd d MMMM", PlannerShared.NL)}.";
            PlannerShared.AddWeekdayWarning(response.Waarschuwingen, date);
        }
        return response;
    }

    public static async Task<DoordeweeksBeschikbaarResponse> CheckDoordeweeksBeschikbaarAsync(
        DoordeweeksBeschikbaarRequest request, ILogger log, string? clubCode = null)
    {
        clubCode = ClubScope.Resolve(clubCode);
        var response = new DoordeweeksBeschikbaarResponse { DagFilter = request.DagFilter };
        var seizoenEinde = await PlannerDataAccess.GetSeasonEndDateAsync()
            ?? DateOnly.FromDateTime(DateTime.Today.AddMonths(6));
        response.SeizoenEinde = seizoenEinde.ToString("yyyy-MM-dd");

        int? gewensteDuur = request.DuurMinuten;
        if (!gewensteDuur.HasValue && !string.IsNullOrEmpty(request.LeeftijdsCategorie))
        {
            var speeltijd = await PlannerDataAccess.GetSpeeltijdAsync(request.LeeftijdsCategorie, clubCode);
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

            var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date, clubCode);
            if (availableFields.Count == 0) continue;

            var sunset = await BepaalEnPasZonsondergangToeAsync(date, availableFields);
            string sunsetStr = sunset.HasValue ? sunset.Value.ToString("HH:mm") : "";

            var occupations = await SportlinkApiClient.GetFieldOccupationsWithApiAsync(date, log, clubCode);

            foreach (var field in availableFields)
            {
                var fieldOccs = occupations.Where(o => o.VeldNummer == field.VeldNummer).OrderBy(o => o.AanvangsTijd).ToList();
                foreach (var (van, tot, maxDuur) in VindOpenGaten(field.BeschikbaarVanaf, field.BeschikbaarTot, fieldOccs))
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
        }
        response.AantalBeschikbaar = response.BeschikbareDatums.Count;
        return response;
    }

    // ── Privé helpers ──

    private readonly record struct DuurBepaling(int Duur, decimal VeldFractie, string? Reden, string? Waarschuwing);

    /// <summary>
    /// Bepaalt de speelduur en veldfractie voor een beschikbaarheidscontrole: leest desgevraagd
    /// <c>dbo.Speeltijden</c> op de leeftijdscategorie, past de <c>HeelVeld</c>-uitzondering toe
    /// (inplannen op een heel veld ondanks een leeftijdscategorie die normaal op een halftijdsveld
    /// speelt) en valideert dat er een duur is. <see cref="DuurBepaling.Reden"/> gezet betekent: de
    /// aanroeper moet direct met die foutmelding stoppen; <see cref="DuurBepaling.Waarschuwing"/>
    /// gezet betekent: toevoegen aan de warningslijst van de response, verder gaan.
    /// </summary>
    private static async Task<DuurBepaling> BepaalDuurEnVeldfractieAsync(CheckAvailabilityRequest request, string clubCode)
    {
        int duurMinuten = 0;
        decimal veldFractie = 1.00m;

        if (!string.IsNullOrEmpty(request.LeeftijdsCategorie))
        {
            var speeltijd = await PlannerDataAccess.GetSpeeltijdAsync(request.LeeftijdsCategorie, clubCode);
            if (speeltijd == null)
                return new DuurBepaling(0, veldFractie,
                    $"Onbekende leeftijdscategorie: {request.LeeftijdsCategorie}. Voeg de categorie toe aan dbo.Speeltijden via /instellingen/speeltijden.",
                    null);
            duurMinuten = request.WedstrijdDuurMinuten ?? speeltijd.WedstrijdTotaal;
            veldFractie = speeltijd.Veldafmeting;
        }
        else if (request.WedstrijdDuurMinuten.HasValue)
        {
            duurMinuten = request.WedstrijdDuurMinuten.Value;
        }

        string? waarschuwing = null;
        if (request.HeelVeld == true && veldFractie < 1.00m)
        {
            if (!string.IsNullOrEmpty(request.LeeftijdsCategorie))
                waarschuwing =
                    $"{request.LeeftijdsCategorie} speelt normaal op een halftijdsspeelveld ({veldFractie:P0} veld). " +
                    $"Inplannen op heel veld conform het verzoek (speelduur blijft {duurMinuten} min).";
            veldFractie = 1.00m;
        }

        if (duurMinuten <= 0)
            return new DuurBepaling(duurMinuten, veldFractie,
                "Leeftijdscategorie of wedstrijdduur is vereist. Voeg de categorie toe aan dbo.Speeltijden via /instellingen/speeltijden.",
                waarschuwing);

        return new DuurBepaling(duurMinuten, veldFractie, null, waarschuwing);
    }

    /// <summary>
    /// Ochtend/middag/avond → tijdvenster. Bij een leeg of onbekend dagdeel de meegegeven
    /// standaardwaarden. Gedeeld tussen CheckAvailabilityAsync, BuildWindowsResponse en
    /// RescheduleService.CheckRescheduleAvailabilityAsync — alleen de defaults verschillen per
    /// aanroeper.
    /// </summary>
    internal static (TimeOnly Van, TimeOnly Tot) BepaalDagdeelVenster(string? dagdeel, TimeOnly standaardVan, TimeOnly standaardTot)
    {
        if (string.IsNullOrEmpty(dagdeel)) return (standaardVan, standaardTot);
        return dagdeel.ToLowerInvariant() switch
        {
            "ochtend" => (new TimeOnly(8, 30), new TimeOnly(12, 0)),
            "middag"  => (new TimeOnly(12, 0), new TimeOnly(17, 0)),
            "avond"   => (new TimeOnly(17, 0), new TimeOnly(22, 0)),
            _ => (standaardVan, standaardTot)
        };
    }

    /// <summary>
    /// Haalt de zonsondergangstijd op (met terugval op <see cref="SunsetCalculator"/> als
    /// <c>PlannerDataAccess.GetSunsetAsync</c> niets teruggeeft) en begrenst in-place elk veld met
    /// <c>GebruikZonsondergang</c> tot die tijd. Gedeeld tussen CheckAvailabilityAsync,
    /// CheckDoordeweeksBeschikbaarAsync en RescheduleService.CheckRescheduleAvailabilityAsync.
    /// </summary>
    internal static async Task<TimeOnly?> BepaalEnPasZonsondergangToeAsync(DateOnly date, List<VeldBeschikbaarheidInfo> velden)
    {
        var sunset = await PlannerDataAccess.GetSunsetAsync(date);
        if (sunset == null) sunset = SunsetCalculator.GetSunset(date);
        foreach (var field in velden)
            if (field.GebruikZonsondergang && sunset.HasValue && sunset.Value < field.BeschikbaarTot)
                field.BeschikbaarTot = sunset.Value;
        return sunset;
    }

    /// <summary>
    /// Vindt open gaten tussen bezettingen op één veld, met <see cref="PlannerShared.StandardBufferMinutes"/>
    /// marge vóór/na elke bezetting en een minimum van 30 minuten per gat. <paramref name="fieldOccs"/>
    /// moet al op <c>AanvangsTijd</c> gesorteerd zijn (beide aanroepers doen dat al). Gedeeld tussen
    /// BuildWindowsResponse en CheckDoordeweeksBeschikbaarAsync.
    /// </summary>
    private static List<(TimeOnly Van, TimeOnly Tot, int MaxDuurMinuten)> VindOpenGaten(
        TimeOnly vanaf, TimeOnly tot, List<BestaandeWedstrijd> fieldOccs)
    {
        var gaten = new List<(TimeOnly Van, TimeOnly Tot, int MaxDuurMinuten)>();
        var gapStart = vanaf;
        foreach (var occ in fieldOccs)
        {
            var occStart = occ.AanvangsTijd.AddMinutes(-PlannerShared.StandardBufferMinutes);
            if (occStart > gapStart)
            {
                int gapMin = (int)(occStart.ToTimeSpan() - gapStart.ToTimeSpan()).TotalMinutes;
                if (gapMin >= 30) gaten.Add((gapStart, occStart, gapMin));
            }
            gapStart = occ.EindTijd.AddMinutes(PlannerShared.StandardBufferMinutes);
        }
        if (gapStart < tot)
        {
            int gapMin = (int)(tot.ToTimeSpan() - gapStart.ToTimeSpan()).TotalMinutes;
            if (gapMin >= 30) gaten.Add((gapStart, tot, gapMin));
        }
        return gaten;
    }

    /// <summary>
    /// Slot-DTO inclusief het werkelijke veldtype uit <c>dbo.Velden</c> (#705). Het automatische
    /// e-mailantwoord kiest hierop welke alternatieven het aanbiedt; leidde het dat zelf af uit het
    /// veldnummer, dan gold die aanname alleen voor één accommodatie. Onbekend veld = <c>null</c>.
    /// </summary>
    private static SlotToewijzing ToSlotMetVeldType(
        DateOnly date, CandidateSlot slot, int duurMinuten, List<VeldInfo> velden)
    {
        var toewijzing = PlannerShared.ToSlotToewijzing(date, slot, duurMinuten, velden);
        toewijzing.VeldType = velden.FirstOrDefault(v => v.VeldNummer == slot.VeldNummer)?.VeldType;
        return toewijzing;
    }

    private static CheckAvailabilityResponse BuildWindowsResponse(
        DateOnly date, List<VeldBeschikbaarheidInfo> fields,
        List<BestaandeWedstrijd> occupations, List<VeldInfo> velden,
        TimeOnly? sunset, string? dagdeel)
    {
        var response = new CheckAvailabilityResponse();
        var windows = new List<BeschikbaarVenster>();
        (TimeOnly filterVan, TimeOnly filterTot) = BepaalDagdeelVenster(dagdeel, new TimeOnly(0, 0), new TimeOnly(23, 59));
        foreach (var field in fields)
        {
            var veldInfo  = velden.FirstOrDefault(v => v.VeldNummer == field.VeldNummer);
            var fieldOccs = occupations.Where(o => o.VeldNummer == field.VeldNummer).OrderBy(o => o.AanvangsTijd).ToList();
            var effectiveStart = field.BeschikbaarVanaf < filterVan ? filterVan : field.BeschikbaarVanaf;
            var effectiveEnd   = field.BeschikbaarTot   > filterTot ? filterTot : field.BeschikbaarTot;
            foreach (var (van, tot, maxDuur) in VindOpenGaten(effectiveStart, effectiveEnd, fieldOccs))
                windows.Add(new BeschikbaarVenster
                {
                    VeldNummer = field.VeldNummer,
                    VeldNaam = veldInfo?.VeldNaam ?? $"veld {field.VeldNummer}",
                    VeldType = veldInfo?.VeldType,
                    Van = van.ToString("HH:mm"),
                    Tot = tot.ToString("HH:mm"),
                    MaxDuurMinuten = maxDuur,
                    Opmerking = !field.GebruikZonsondergang ? null : $"Zonsondergang {sunset:HH:mm}, geen kunstlicht"
                });
        }
        response.Beschikbaar = windows.Count > 0;
        response.BeschikbareVensters = windows;
        if (!response.Beschikbaar)
            response.Reden = $"Geen beschikbare vensters op {date.ToString("dddd d MMMM", PlannerShared.NL)}.";
        PlannerShared.AddWeekdayWarning(response.Waarschuwingen, date);
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
