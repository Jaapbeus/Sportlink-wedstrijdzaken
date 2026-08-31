namespace Planner.Shared;

/// <summary>
/// Gedeelde constanten, utilities en helper-methoden voor de planningsmotor (#888, verhuisd uit
/// <c>FunctionApp/Planner/Services/PlannerShared.cs</c> — extracted uit PlannerService, #475).
///
/// <para>
/// <b>Waarom deze klasse "PlannerShared" heet terwijl ze al in de namespace <c>Planner.Shared</c>
/// zit:</b> bewuste keuze om alle circa zestig bestaande aanroepen (<c>PlannerShared.CanFitMatch</c>,
/// <c>PlannerShared.ResolveVeld</c>, ...) in <c>AvailabilityService</c>, <c>RescheduleService</c>,
/// <c>AutoPlanService</c>, <c>SportlinkApiClient</c> en de bijbehorende testbestanden ongewijzigd te
/// laten werken — alleen een <c>using Planner.Shared;</c> is nodig, geen enkele aanroepnaam hoeft
/// te veranderen. Een risicoarme verhuizing weegt hier zwaarder dan een cosmetisch andere naam.
/// </para>
/// </summary>
public static class PlannerShared
{
    public const int StandardBufferMinutes = 15;
    public const double MaxBezettingsPercentageVoorOverslaan = 50.0;
    public const int SunsetWarningMarginMinutes = 20;
    public static readonly System.Globalization.CultureInfo NL = new("nl-NL");

    /// <summary>Rond aanvangstijd naar boven af op 5 minuten.</summary>
    public static TimeOnly RondAfOp5Min(TimeOnly tijd)
    {
        int minuten = tijd.Hour * 60 + tijd.Minute;
        int rest = minuten % 5;
        if (rest > 0) minuten += (5 - rest);
        return new TimeOnly(minuten / 60, minuten % 60);
    }

    // ── Sportlink-veldstring → veldnummer: één plek, gebruikt door élke consumer (#707) ──
    //
    // Sportlink levert het veld als "<veldnaam>[ <subpositie>]" ("veld 1 A"); de veldentabel bevat
    // alleen de veldnaam zelf. #819 verhuisde de daadwerkelijke matching-implementatie al naar
    // Planner.Shared/VeldResolver.cs (puur tekstlogica). Deze klasse zat er sindsdien alleen nog
    // tussen als dunne delegatie vanuit de SQL Server-boom — met de verhuizing van deze klasse
    // zélf naar Planner.Shared (#888) is die tussenlaag overbodig geworden; de methoden hieronder
    // roepen VeldResolver nu rechtstreeks aan.

    /// <summary>
    /// Splitst een Sportlink-veldstring in het veldnummer en de subpositie die Sportlink erachter
    /// zet. Een treffer is een exact gelijke veldnaam óf een veldnaam gevolgd door een spatie en de
    /// subpositie — nooit een langer veldnummer, zodat "veld 10" niet op "veld 1" valt.
    /// </summary>
    /// <returns>
    /// Veldnummer, of <c>0</c> als geen enkel veld matcht (dezelfde sentinel als de oude
    /// lookup-miss), plus de subpositie in hoofdletters of <c>null</c> als die ontbreekt.
    /// </returns>
    public static (int VeldNummer, string? Subpositie) ResolveVeld(
        string? sportlinkVeld, IEnumerable<(string? VeldNaam, int VeldNummer)> velden)
        => VeldResolver.Resolve(sportlinkVeld, velden);

    /// <inheritdoc cref="ResolveVeld(string?, IEnumerable{ValueTuple{string?, int}})"/>
    public static (int VeldNummer, string? Subpositie) ResolveVeld(
        string? sportlinkVeld, IReadOnlyDictionary<string, int> veldenPerNaam)
        => VeldResolver.Resolve(sportlinkVeld, veldenPerNaam);

    /// <summary>
    /// Veldnummer bij de veldnaam zoals Sportlink die levert, of <c>0</c> als geen veld matcht.
    /// Zelfde matching als <see cref="ResolveVeld(string?, IEnumerable{ValueTuple{string?, int}})"/>
    /// — de bezetting en het herplanpad mogen nooit een eigen variant gebruiken.
    /// </summary>
    public static int VindVeldNummer(string? sportlinkVeld, IEnumerable<VeldInfo> velden)
        => ResolveVeld(sportlinkVeld, velden.Select(v => ((string?)v.VeldNaam, v.VeldNummer))).VeldNummer;

    public static bool CanFitMatch(
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

    public static CandidateSlot? TryExactTime(
        TimeOnly preferredTime,
        List<VeldBeschikbaarheidInfo> availableFields,
        List<BestaandeWedstrijd> occupations,
        List<VeldInfo> velden,
        Dictionary<string, List<TeamRegel>> allTeamRules,
        List<TeamRegel> requestingTeamRules,
        decimal veldFractie, int duurMinuten, TimeOnly? sunset)
    {
        var endTime = preferredTime.AddMinutes(duurMinuten);
        // Kunstgras eerst om grasvelden te ontlasten. Classificatie via VeldInfo.IsKunstgras — één
        // definitie van "kunstgras" voor de hele codebase (#707); een eigen stringvergelijking hier
        // noemde "Kunstgras 2" géén kunstgras en zette dat veld dus achteraan.
        var nietKunstgrasNrs = velden.Where(v => !v.IsKunstgras).Select(v => v.VeldNummer).ToHashSet();
        foreach (var field in availableFields.OrderBy(f => nietKunstgrasNrs.Contains(f.VeldNummer) ? 1 : 0))
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

    public static List<CandidateSlot> FindAllSlots(
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
        // Zelfde voorkeursordening als TryExactTime, via dezelfde kunstgras-definitie (#707).
        var nietKunstgrasNrs = velden.Where(v => !v.IsKunstgras).Select(v => v.VeldNummer).ToHashSet();
        return candidates
            .OrderBy(c => nietKunstgrasNrs.Contains(c.VeldNummer) ? 1 : 0)
            .ThenBy(c => c.AanvangsTijd.ToTimeSpan().TotalMinutes)
            .ToList();
    }

    /// <summary>
    /// Zet een kandidaat-slot om naar de publieke <see cref="SlotToewijzing"/>-DTO.
    ///
    /// <para><b>Veldtype reist altijd mee (#705/#707).</b> Elke aanroeper geeft de veldenlijst al mee
    /// voor de veldnaam, dus het veldtype hoort hier gevuld te worden — niet per aanroeper.</para>
    ///
    /// <para>Staat het veld niet in <paramref name="velden"/> (bijv. een inactief veld dat nog in een
    /// bezetting voorkomt), dan blijft <c>VeldType</c> <c>null</c> = onbekend. Filters mogen zo'n
    /// slot nooit wegfilteren — zie <see cref="VeldSoort.Onbekend"/>.</para>
    /// </summary>
    public static SlotToewijzing ToSlotToewijzing(DateOnly date, CandidateSlot slot, int duurMinuten, List<VeldInfo> velden)
    {
        var veld = velden.FirstOrDefault(v => v.VeldNummer == slot.VeldNummer);
        return new SlotToewijzing
        {
            Datum = date.ToString("yyyy-MM-dd"),
            AanvangsTijd = slot.AanvangsTijd.ToString("HH:mm"),
            EindTijd = slot.EindTijd.ToString("HH:mm"),
            VeldNummer = slot.VeldNummer,
            VeldNaam = veld?.VeldNaam ?? $"veld {slot.VeldNummer}",
            VeldType = veld?.VeldType,
            VeldDeelGebruik = slot.VeldFractie > 0 ? slot.VeldFractie : 1.00m,
            WedstrijdDuurMinuten = duurMinuten
        };
    }

    /// <summary>
    /// Doordeweekse waarschuwing — clubneutraal en configuratiegedreven (#576).
    /// Nooit vaste veldnummers in de tekst: welke velden doordeweeks vrij zijn volgt uit de
    /// veldbeschikbaarheid en verschilt per club en per seizoen. Een hardcoded aanname
    /// ("alleen veld 5") is bij een andere clubconfiguratie feitelijk onjuist.
    /// </summary>
    public static string BouwWeekdayWarning(DateOnly date)
        => $"{date.ToString("dddd", NL)}: doordeweeks — kunstgrasvelden mogelijk in gebruik voor training. Controleer veldbeschikbaarheid.";

    public static bool IsWeekday(DateOnly date)
        => date.DayOfWeek >= DayOfWeek.Monday && date.DayOfWeek <= DayOfWeek.Thursday;

    /// <summary>
    /// Voegt bij een doordeweekse datum een waarschuwing toe aan een losse lijst. Bewust de enige
    /// overload hier: de andere (response-typed) overload van vóór #888 was per tier verschillend
    /// getypeerd (<c>CheckAvailabilityResponse</c> bestaat alleen op de tier zelf) en hoort dus bij
    /// elke tier se eigen <c>AvailabilityService</c>, niet in deze gedeelde klasse.
    /// </summary>
    public static void AddWeekdayWarning(List<string> waarschuwingen, DateOnly date)
    {
        if (IsWeekday(date)) waarschuwingen.Add(BouwWeekdayWarning(date));
    }
}

// ── Hulpklassen gedeeld tussen services ──

public class CandidateSlot
{
    public int VeldNummer { get; set; }
    public TimeOnly AanvangsTijd { get; set; }
    public TimeOnly EindTijd { get; set; }
    public decimal VeldFractie { get; set; }
}

public class IngeplandSlot
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
/// Extracted uit PlannerService (#475); verhuisd naar Planner.Shared zodat beide databasetiers
/// dezelfde implementatie gebruiken (#888) in plaats van een tweede kopie te bouwen die tegen
/// SQL-Server-specifieke modellen zou compileren.
/// </summary>
public class FieldScheduler
{
    private readonly List<VeldBeschikbaarheidInfo> _beschikbaarheid;
    private readonly List<VeldInfo> _velden;
    private readonly int _buffer;
    private readonly Dictionary<string, (int bufferVoor, int bufferNa)> _teamBuffers;
    private readonly Dictionary<int, List<IngeplandSlot>> _occupations = new();
    private static readonly TimeOnly StartTijd = new(9, 0);

    /// <summary>Vroegste tijd waarop de planner inplant — als streeftijd te gebruiken wanneer er
    /// geen voorkeurstijd is maar wel een voorkeursveld (#666).</summary>
    public static TimeOnly DagStart => StartTijd;

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

    /// <summary>
    /// Past een wedstrijd van <paramref name="fractie"/> veld tussen <paramref name="start"/> en
    /// <paramref name="end"/> op dit veld? Bewaakt twee dingen tegelijk (#666):
    ///
    /// <para><b>Capaciteit</b> — wedstrijden die elkaar in tijd overlappen delen het veld. Dat mag
    /// zolang de som van de veldfracties binnen 1.00 blijft (twee halve velden naast elkaar). Tussen
    /// zulke gelijktijdige wedstrijden hoort géén buffer: ze staan naast elkaar, niet achter elkaar.</para>
    ///
    /// <para><b>Buffer</b> — wedstrijden die elkaar niet overlappen gebruiken het veld ná elkaar. Daar
    /// moet de buffer tussen zitten: de grootste van de standaardbuffer en de teamspecifieke
    /// buffer-voor/na-regel.</para>
    /// </summary>
    private bool PastOpVeld(int veldNummer, TimeOnly start, TimeOnly end, decimal fractie,
        int nieuwBufVoor, string? teamNaam, out string subpositie)
    {
        subpositie = string.Empty;
        var occs = _occupations.TryGetValue(veldNummer, out var list) ? list : new List<IngeplandSlot>();
        int nieuwBufNa = TeamBufferNa(teamNaam);
        var bezet = new bool[4];

        foreach (var occ in occs)
        {
            bool overlapt = occ.AanvangsTijd < end && occ.EindTijd > start;
            if (overlapt)
            {
                // Gelijktijdig op hetzelfde veld: welke kwartbanen liggen al vol? Géén buffer hiertussen —
                // deze wedstrijden staan naast elkaar op het veld, niet achter elkaar.
                var occBanen = BanenVanSubpositie(occ.VeldSubpositie);
                for (int i = 0; i < 4; i++) bezet[i] |= occBanen[i];
                continue;
            }

            if (occ.EindTijd <= start)
            {
                // Bestaande wedstrijd gaat vooraf: gat = grootste van haar BufferNa en onze BufferVoor.
                int buf = Math.Max(TeamBufferNa(occ.TeamNaam), nieuwBufVoor);
                if (start < occ.EindTijd.AddMinutes(buf)) return false;
            }
            else
            {
                // Bestaande wedstrijd volgt: gat = grootste van onze BufferNa en haar BufferVoor.
                int buf = Math.Max(nieuwBufNa, TeamBufferVoor(occ.TeamNaam));
                if (occ.AanvangsTijd < end.AddMinutes(buf)) return false;
            }
        }

        var vrij = EersteVrijeSubpositie(bezet, fractie);
        if (vrij == null) return false;
        subpositie = vrij;
        return true;
    }

    private IngeplandSlot? FindBestEarliestSlot(decimal fractie, int duurMinuten, int nieuwBufVoor, string? teamNaam = null)
    {
        var sorted = _velden.OrderByDescending(v => v.IsKunstgras).ThenBy(v => v.VeldNummer).ToList();
        IngeplandSlot? best = null;
        foreach (var veld in sorted)
        {
            var besch = _beschikbaarheid.FirstOrDefault(b => b.VeldNummer == veld.VeldNummer);
            if (besch == null) continue;
            var van = besch.BeschikbaarVanaf < StartTijd ? StartTijd : besch.BeschikbaarVanaf;
            var slot = FindEarliestSlot(veld.VeldNummer, fractie, duurMinuten, van, besch.BeschikbaarTot, nieuwBufVoor, teamNaam);
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
        var best = FindBestEarliestSlot(fractie, duurMinuten, nieuwBufVoor, teamNaam);
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
    /// gezocht. De kandidaatlijst hieronder loopt van de voorkeurstijd naar buiten (±5, ±10, …),
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
                // (#666): een team met 08:30 als voorkeurstijd mag niet stilzwijgend naar 09:00
                // geschoven worden terwijl het veld al om 08:00 open was. De 09:00-ondergrens blijft
                // wél gelden voor wedstrijden zónder voorkeurstijd (FindAndOccupyNextSlot).
                var van   = besch.BeschikbaarVanaf;
                var start = PlannerShared.RondAfOp5Min(kandidaatTijd < van ? van : kandidaatTijd);
                var end   = start.AddMinutes(duurMinuten);
                if (end > besch.BeschikbaarTot || end <= start) continue;
                if (!PastOpVeld(veld.VeldNummer, start, end, fractie, nieuwBufVoor, teamNaam, out var subpos)) continue;
                var slot = new IngeplandSlot { VeldNummer = veld.VeldNummer, AanvangsTijd = start, EindTijd = end, Fractie = fractie, VeldSubpositie = subpos, TeamNaam = teamNaam };
                _occupations[veld.VeldNummer].Add(slot);
                return slot;
            }
        }
        return FindAndOccupyNextSlot(fractie, duurMinuten, nieuwBufVoor, teamNaam);
    }

    private IngeplandSlot? FindEarliestSlot(int veldNummer, decimal fractie, int duurMinuten, TimeOnly van, TimeOnly tot, int nieuwBufVoor = -1, string? teamNaam = null)
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
            // Zelfde bufferbewuste check als het voorkeurstijd-pad — de kandidaattijden hierboven zijn
            // buffer-aware, maar de eindcontrole moet dat ook zijn (bijv. bij een wedstrijd die volgt).
            if (!PastOpVeld(veldNummer, start, end, fractie, nieuwBufVoor, teamNaam, out var subpos)) continue;
            return new IngeplandSlot { VeldNummer = veldNummer, AanvangsTijd = start, EindTijd = end, Fractie = fractie, VeldSubpositie = subpos };
        }
        return null;
    }

    // ── Veldindeling in banen (#666) ──
    //
    // Een veld bestaat uit vier kwartbanen: 0=A1, 1=A2, 2=B1, 3=B2. Een kwartveldwedstrijd bezet één
    // baan, een halfveldwedstrijd twee aangrenzende banen (A = 0+1, B = 2+3) en een heel veld alle vier.
    public static readonly string[] BaanLabels = ["A1", "A2", "B1", "B2"];

    /// <summary>Welke kwartbanen bezet een wedstrijd met deze subpositie? Leeg = heel veld.</summary>
    public static bool[] BanenVanSubpositie(string? subpositie)
    {
        var banen = new bool[4];
        switch ((subpositie ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "A1": banen[0] = true; break;
            case "A2": banen[1] = true; break;
            case "B1": banen[2] = true; break;
            case "B2": banen[3] = true; break;
            case "A":  banen[0] = banen[1] = true; break;
            case "B":  banen[2] = banen[3] = true; break;
            default:   banen[0] = banen[1] = banen[2] = banen[3] = true; break;
        }
        return banen;
    }

    /// <summary>Hoeveel kwartbanen heeft een wedstrijd van deze veldafmeting nodig?</summary>
    public static int BanenNodig(decimal fractie) => fractie switch
    {
        <= 0.26m => 1,
        <= 0.51m => 2,
        _ => 4
    };

    /// <summary>
    /// Eerste vrije plek voor een wedstrijd van <paramref name="fractie"/> veld, gegeven de al bezette
    /// banen. Geeft het subpositie-label terug ("A1", "B", "" voor een heel veld), of null als er geen
    /// plek is. Halfveldwedstrijden mogen alleen op A of B — niet op de banen 1+2 dwars door het midden.
    /// </summary>
    public static string? EersteVrijeSubpositie(bool[] bezet, decimal fractie)
    {
        int nodig = BanenNodig(fractie);
        if (nodig == 4)
            return bezet.Any(b => b) ? null : string.Empty;

        if (nodig == 2)
        {
            if (!bezet[0] && !bezet[1]) return "A";
            if (!bezet[2] && !bezet[3]) return "B";
            return null;
        }

        for (int i = 0; i < 4; i++)
            if (!bezet[i]) return BaanLabels[i];
        return null;
    }
}
