namespace SportlinkFunction.Planner;

/// <summary>
/// Gedeelde constanten, utilities en helper-methoden voor alle planner use-case services.
/// Extracted uit PlannerService (#475).
/// </summary>
internal static class PlannerShared
{
    internal const int StandardBufferMinutes = 15;
    internal const double MaxBezettingsPercentageVoorOverslaan = 50.0;
    internal const int SunsetWarningMarginMinutes = 20;
    internal static readonly System.Globalization.CultureInfo NL = new("nl-NL");

    /// <summary>Rond aanvangstijd naar boven af op 5 minuten.</summary>
    internal static TimeOnly RondAfOp5Min(TimeOnly tijd)
    {
        int minuten = tijd.Hour * 60 + tijd.Minute;
        int rest = minuten % 5;
        if (rest > 0) minuten += (5 - rest);
        return new TimeOnly(minuten / 60, minuten % 60);
    }

    internal static bool CanFitMatch(
        TimeOnly start, TimeOnly end, decimal veldFractie, int veldNummer,
        List<BestaandeWedstrijd> fieldOccupations,
        Dictionary<string, List<TeamRegel>> allTeamRules,
        List<TeamRegel> requestingTeamRules)
    {
        int bufferVoor = StandardBufferMinutes;
        int bufferNa   = StandardBufferMinutes;
        foreach (var rule in requestingTeamRules)
        {
            if (rule.RegelType == "BufferVoor" && rule.WaardeMinuten.HasValue)
                bufferVoor = Math.Max(bufferVoor, rule.WaardeMinuten.Value);
            if (rule.RegelType == "BufferNa" && rule.WaardeMinuten.HasValue)
                bufferNa = Math.Max(bufferNa, rule.WaardeMinuten.Value);
        }
        foreach (var occ in fieldOccupations)
        {
            int occBufVoor = StandardBufferMinutes;
            int occBufNa   = StandardBufferMinutes;
            if (!string.IsNullOrEmpty(occ.TeamNaam) && allTeamRules.TryGetValue(occ.TeamNaam, out var existing))
            {
                foreach (var rule in existing)
                {
                    if (rule.RegelType == "BufferVoor" && rule.WaardeMinuten.HasValue)
                        occBufVoor = Math.Max(occBufVoor, rule.WaardeMinuten.Value);
                    if (rule.RegelType == "BufferNa" && rule.WaardeMinuten.HasValue)
                        occBufNa = Math.Max(occBufNa, rule.WaardeMinuten.Value);
                }
            }
            bool gelijktijdig = occ.AanvangsTijd < end && occ.EindTijd > start;
            if (gelijktijdig)
            {
                if (veldFractie < 1.0m && occ.VeldDeelGebruik < 1.0m)
                {
                    decimal maxCap = 0;
                    for (var t = start; t < end; t = t.AddMinutes(5))
                    {
                        var te = t.AddMinutes(5);
                        decimal cap = fieldOccupations
                            .Where(o => o.VeldDeelGebruik < 1.0m && o.AanvangsTijd < te && o.EindTijd > t)
                            .Sum(o => o.VeldDeelGebruik);
                        maxCap = Math.Max(maxCap, cap);
                    }
                    if (maxCap + veldFractie > 1.0m) return false;
                    continue;
                }
                return false;
            }
            var occBeschStart = occ.AanvangsTijd.AddMinutes(-occBufVoor);
            var occBeschEinde = occ.EindTijd.AddMinutes(occBufNa);
            if (start < occBeschEinde && end > occBeschStart) return false;
            var nieuwStart = start.AddMinutes(-bufferVoor);
            var nieuwEinde = end.AddMinutes(bufferNa);
            if (occ.AanvangsTijd < nieuwEinde && occ.EindTijd > nieuwStart) return false;
        }
        return true;
    }

    internal static CandidateSlot? TryExactTime(
        TimeOnly preferredTime,
        List<VeldBeschikbaarheidInfo> availableFields,
        List<BestaandeWedstrijd> occupations,
        List<VeldInfo> velden,
        Dictionary<string, List<TeamRegel>> allTeamRules,
        List<TeamRegel> requestingTeamRules,
        decimal veldFractie, int duurMinuten, TimeOnly? sunset)
    {
        var endTime = preferredTime.AddMinutes(duurMinuten);
        var grasveldNrs = velden.Where(v => v.VeldType != "kunstgras").Select(v => v.VeldNummer).ToHashSet();
        foreach (var field in availableFields.OrderBy(f => grasveldNrs.Contains(f.VeldNummer) ? 1 : 0))
        {
            if (preferredTime < field.BeschikbaarVanaf || endTime > field.BeschikbaarTot) continue;
            var fieldOccs = occupations.Where(o => o.VeldNummer == field.VeldNummer).ToList();
            if (CanFitMatch(preferredTime, endTime, veldFractie, field.VeldNummer,
                            fieldOccs, allTeamRules, requestingTeamRules))
                return new CandidateSlot
                {
                    VeldNummer = field.VeldNummer,
                    AanvangsTijd = preferredTime,
                    EindTijd = endTime
                };
        }
        return null;
    }

    internal static List<CandidateSlot> FindAllSlots(
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
            var fieldOccs = occupations.Where(o => o.VeldNummer == field.VeldNummer).ToList();
            var windowStart = dagdeelVan < field.BeschikbaarVanaf ? field.BeschikbaarVanaf : dagdeelVan;
            var windowEnd   = dagdeelTot > field.BeschikbaarTot   ? field.BeschikbaarTot   : dagdeelTot;
            for (var time = windowStart; time < windowEnd && time.AddMinutes(duurMinuten) <= field.BeschikbaarTot; time = time.AddMinutes(5))
            {
                var endTime = time.AddMinutes(duurMinuten);
                if (CanFitMatch(time, endTime, veldFractie, field.VeldNummer,
                                fieldOccs, allTeamRules, requestingTeamRules))
                {
                    candidates.Add(new CandidateSlot { VeldNummer = field.VeldNummer, AanvangsTijd = time, EindTijd = endTime });
                    time = time.AddMinutes(duurMinuten + StandardBufferMinutes - 5);
                }
            }
        }
        var grasveldNrs = velden.Where(v => v.VeldType != "kunstgras").Select(v => v.VeldNummer).ToHashSet();
        return candidates
            .OrderBy(c => grasveldNrs.Contains(c.VeldNummer) ? 1 : 0)
            .ThenBy(c => c.AanvangsTijd.ToTimeSpan().TotalMinutes)
            .ToList();
    }

    internal static SlotToewijzing ToSlotToewijzing(DateOnly date, CandidateSlot slot, int duurMinuten, List<VeldInfo> velden)
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

    /// <summary>
    /// Doordeweekse waarschuwing — clubneutraal en configuratiegedreven (#576).
    /// Nooit vaste veldnummers in de tekst: welke velden doordeweeks vrij zijn volgt uit
    /// dbo.VeldBeschikbaarheid en verschilt per club en per seizoen. Een hardcoded aanname
    /// ("alleen veld 5") is bij een andere clubconfiguratie feitelijk onjuist.
    /// </summary>
    internal static string BouwWeekdayWarning(DateOnly date)
        => $"{date.ToString("dddd", NL)}: doordeweeks — kunstgrasvelden mogelijk in gebruik voor training. Controleer veldbeschikbaarheid.";

    internal static bool IsWeekday(DateOnly date)
        => date.DayOfWeek >= DayOfWeek.Monday && date.DayOfWeek <= DayOfWeek.Thursday;

    internal static void AddWeekdayWarning(CheckAvailabilityResponse response, DateOnly date)
    {
        if (IsWeekday(date)) response.Waarschuwingen.Add(BouwWeekdayWarning(date));
    }

    internal static void AddWeekdayWarning(List<string> waarschuwingen, DateOnly date)
    {
        if (IsWeekday(date)) waarschuwingen.Add(BouwWeekdayWarning(date));
    }
}

// ── Hulpklassen gedeeld tussen services ──

internal class CandidateSlot
{
    public int VeldNummer { get; set; }
    public TimeOnly AanvangsTijd { get; set; }
    public TimeOnly EindTijd { get; set; }
    public decimal VeldFractie { get; set; }
}

internal class IngeplandSlot
{
    public int VeldNummer { get; set; }
    public TimeOnly AanvangsTijd { get; set; }
    public TimeOnly EindTijd { get; set; }
    public decimal Fractie { get; set; }
    public string VeldSubpositie { get; set; } = string.Empty;
    public string? TeamNaam { get; set; }
}

/// <summary>
/// Pure scheduling engine — geen DB-calls, alleen slot-berekening op basis van beschikbaarheid.
/// Extracted uit PlannerService (#475).
/// </summary>
internal class FieldScheduler
{
    private readonly List<VeldBeschikbaarheidInfo> _beschikbaarheid;
    private readonly List<VeldInfo> _velden;
    private readonly int _buffer;
    private readonly Dictionary<string, (int bufferVoor, int bufferNa)> _teamBuffers;
    private readonly Dictionary<int, List<IngeplandSlot>> _occupations = new();
    private static readonly TimeOnly StartTijd = new(9, 0);

    /// <summary>Vroegste tijd waarop de planner inplant — als streeftijd te gebruiken wanneer er
    /// geen voorkeurstijd is maar wel een voorkeursveld (#666).</summary>
    internal static TimeOnly DagStart => StartTijd;

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

    private int EffectieveBuffer(string? occTeamNaam, int nieuwBufVoor) =>
        Math.Max(TeamBufferNa(occTeamNaam), nieuwBufVoor);

    private IngeplandSlot? FindBestEarliestSlot(decimal fractie, int duurMinuten, int nieuwBufVoor)
    {
        var sorted = _velden.OrderByDescending(v => v.IsKunstgras).ThenBy(v => v.VeldNummer).ToList();
        IngeplandSlot? best = null;
        foreach (var veld in sorted)
        {
            var besch = _beschikbaarheid.FirstOrDefault(b => b.VeldNummer == veld.VeldNummer);
            if (besch == null) continue;
            var van = besch.BeschikbaarVanaf < StartTijd ? StartTijd : besch.BeschikbaarVanaf;
            var slot = FindEarliestSlot(veld.VeldNummer, fractie, duurMinuten, van, besch.BeschikbaarTot, nieuwBufVoor);
            if (slot != null && (best == null || slot.AanvangsTijd < best.AanvangsTijd))
            {
                best = slot;
                if (best.AanvangsTijd == van) break;
            }
        }
        return best;
    }

    public IngeplandSlot? FindAndOccupyNextSlot(decimal fractie, int duurMinuten, int nieuwBufVoor = -1, string? teamNaam = null)
    {
        if (nieuwBufVoor < 0) nieuwBufVoor = _buffer;
        var best = FindBestEarliestSlot(fractie, duurMinuten, nieuwBufVoor);
        if (best != null) { best.TeamNaam = teamNaam; _occupations[best.VeldNummer].Add(best); }
        return best;
    }

    /// <summary>
    /// Zoekt een slot zo dicht mogelijk bij <paramref name="voorkeurTijd"/> en bezet het.
    ///
    /// <para><paramref name="voorkeurVeldNummer"/> (#666): het veld uit een 'VoorkeurVeld'-teamregel.
    /// Dat veld wordt bij elke kandidaat-tijd als eerste geprobeerd. Het is een zachte voorkeur — is
    /// het veld bezet of te klein, dan valt de planner terug op de normale veldsortering, zodat een
    /// team nooit onplanbaar wordt door alleen een veldvoorkeur.</para>
    ///
    /// <para>De voorkeurstijd is hier het doel — er wordt niet naar het vroegste gat van de dag
    /// gezocht. Tot #666 stond hier het omgekeerde: lag het vroegste vrije slot meer dan één buffer
    /// vóór de voorkeurstijd, dan pakte de planner dát slot en verdween de voorkeur volledig. Een team
    /// met voorkeur 14:30 werd zo op 09:00 gezet — vijf en een half uur ernaast, terwijl de tabel
    /// "OK" meldde. De kandidaatlijst hieronder loopt van de voorkeurstijd naar buiten (±5, ±10, …),
    /// dus het dichtstbijzijnde haalbare tijdslot wint, met eerder vóór later bij gelijke afstand.</para>
    /// </summary>
    public IngeplandSlot? FindAndOccupyNearTime(TimeOnly voorkeurTijd, decimal fractie, int duurMinuten,
        int nieuwBufVoor = -1, string? teamNaam = null, int tolerantieMinuten = 90,
        int? voorkeurVeldNummer = null)
    {
        if (nieuwBufVoor < 0) nieuwBufVoor = _buffer;
        var candidates = new List<TimeOnly> { voorkeurTijd };
        for (int delta = 5; delta <= tolerantieMinuten; delta += 5)
        {
            var vroeger = voorkeurTijd.AddMinutes(-delta);
            var later   = voorkeurTijd.AddMinutes(delta);
            if (vroeger >= StartTijd) candidates.Add(vroeger);
            candidates.Add(later);
        }
        // Voorkeursveld vooraan in de veldsortering — de rest van de volgorde blijft ongewijzigd
        // (kunstgras vóór gras, daarna veldnummer), zodat het gedrag zonder voorkeursveld identiek is.
        var sorted = _velden
            .OrderByDescending(v => voorkeurVeldNummer.HasValue && v.VeldNummer == voorkeurVeldNummer.Value)
            .ThenByDescending(v => v.IsKunstgras)
            .ThenBy(v => v.VeldNummer)
            .ToList();
        foreach (var kandidaatTijd in candidates)
        {
            foreach (var veld in sorted)
            {
                var besch = _beschikbaarheid.FirstOrDefault(b => b.VeldNummer == veld.VeldNummer);
                if (besch == null) continue;
                // Ondergrens is hier de veldbeschikbaarheid zelf, NIET de standaard dagstart van 09:00
                // (#666). Een team dat 08:30 als voorkeurstijd heeft opgegeven werd anders stilzwijgend
                // naar 09:00 geschoven terwijl het veld al om 08:00 open was — een afwijking van 30
                // minuten die niemand had gevraagd. Waar de dag begint hoort uit dbo.VeldBeschikbaarheid
                // te komen, niet uit een vaste waarde in code. De 09:00-ondergrens blijft wél gelden
                // voor wedstrijden zónder voorkeurstijd: die lopen via FindAndOccupyNextSlot.
                var van   = besch.BeschikbaarVanaf;
                var start = PlannerShared.RondAfOp5Min(kandidaatTijd < van ? van : kandidaatTijd);
                var end   = start.AddMinutes(duurMinuten);
                if (end > besch.BeschikbaarTot || end <= start) continue;
                var occs = _occupations.TryGetValue(veld.VeldNummer, out var list) ? list : new List<IngeplandSlot>();
                var fractiesInUse = occs.Where(o => o.AanvangsTijd < end && o.EindTijd > start).Sum(o => o.Fractie);
                if (fractiesInUse + fractie > 1.0m + 0.001m) continue;
                int concurrent = occs.Count(o => o.AanvangsTijd < end && o.EindTijd > start);
                var slot = new IngeplandSlot { VeldNummer = veld.VeldNummer, AanvangsTijd = start, EindTijd = end, Fractie = fractie, VeldSubpositie = GetSubpositie(fractie, concurrent), TeamNaam = teamNaam };
                _occupations[veld.VeldNummer].Add(slot);
                return slot;
            }
        }
        return FindAndOccupyNextSlot(fractie, duurMinuten, nieuwBufVoor, teamNaam);
    }

    private IngeplandSlot? FindEarliestSlot(int veldNummer, decimal fractie, int duurMinuten, TimeOnly van, TimeOnly tot, int nieuwBufVoor = -1)
    {
        if (nieuwBufVoor < 0) nieuwBufVoor = _buffer;
        var occs = _occupations.TryGetValue(veldNummer, out var list) ? list.OrderBy(o => o.AanvangsTijd).ToList() : new List<IngeplandSlot>();
        var candidates = new HashSet<TimeOnly> { van };
        foreach (var occ in occs)
        {
            candidates.Add(occ.AanvangsTijd);
            var afterEnd = occ.EindTijd.AddMinutes(EffectieveBuffer(occ.TeamNaam, nieuwBufVoor));
            if (afterEnd > van) candidates.Add(afterEnd);
        }
        foreach (var candidate in candidates.OrderBy(t => t))
        {
            if (candidate < van) continue;
            var start = PlannerShared.RondAfOp5Min(candidate);
            if (start < van) start = van;
            var end = start.AddMinutes(duurMinuten);
            if (end > tot || end <= start) continue;
            var fractiesInUse = occs.Where(o => o.AanvangsTijd < end && o.EindTijd > start).Sum(o => o.Fractie);
            if (fractiesInUse + fractie > 1.0m + 0.001m) continue;
            int concurrent = occs.Count(o => o.AanvangsTijd < end && o.EindTijd > start);
            return new IngeplandSlot { VeldNummer = veldNummer, AanvangsTijd = start, EindTijd = end, Fractie = fractie, VeldSubpositie = GetSubpositie(fractie, concurrent) };
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
