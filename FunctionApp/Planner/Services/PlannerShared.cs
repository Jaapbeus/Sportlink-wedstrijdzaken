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

    // ── Sportlink-veldstring → veldnummer: één plek, gebruikt door élke consumer (#707) ──
    //
    // Sportlink levert het veld als "<veldnaam>[ <subpositie>]" ("veld 1 A"); dbo.Velden bevat
    // alleen de veldnaam zelf. Die vertaling gebeurde op meerdere plekken op verschillende
    // manieren: StartsWith zonder woordgrens in het herplanpad en een harde afkap op zes tekens
    // (LEFT(veld, 6) / veld[..6]) in het bezettingspad. Bij tien of meer velden liepen die uiteen:
    //
    //   • de matcher zag "veld 10" correct als veld 10;
    //   • de bezetting kapte "veld 10" af op "veld 1" en boekte de wedstrijd op veld 1.
    //
    // Gevolg: de eigen wedstrijd bleef als spookbezetting op veld 1 staan (die dag viel daar dicht)
    // én veld 10 kwam in de bezetting niet voor, dus veld 10 leek de hele dag vrij. Er kon dan een
    // tweede wedstrijd naast de bestaande op hetzelfde veld worden aangeboden — een dubbele boeking.
    //
    // Daarom loopt de vertaling nu voor alle consumenten via deze functie. De normalisatie zelf is
    // hergebruikt uit <see cref="AutoPlanService.NormaliseerVeld"/>; er is bewust geen tweede
    // variant naast bijgezet.
    //
    // #819: de daadwerkelijke matching-implementatie is verhuisd naar het tier-agnostische
    // Planner.Shared/VeldResolver.cs (puur tekstlogica, geen databaseafhankelijkheid) zodat de
    // Postgres-tier's planner-view (die het veldresolutie-deel bewust niet in SQL herbouwt, zie
    // Database.Postgres) dezelfde implementatie aanroept in plaats van een derde, onafhankelijke
    // kopie te introduceren naast deze C#-versie en de SQL Server-view. Deze methode is nu een
    // dunne delegatie — gedrag ongewijzigd.

    /// <summary>
    /// Splitst een Sportlink-veldstring in het veldnummer uit <c>dbo.Velden</c> en de subpositie
    /// die Sportlink erachter zet. Een treffer is een exact gelijke veldnaam óf een veldnaam
    /// gevolgd door een spatie en de subpositie — nooit een langer veldnummer, zodat "veld 10"
    /// niet op "veld 1" valt.
    /// </summary>
    /// <returns>
    /// Veldnummer, of <c>0</c> als geen enkel veld matcht (dezelfde sentinel als de oude
    /// lookup-miss), plus de subpositie in hoofdletters of <c>null</c> als die ontbreekt.
    /// </returns>
    internal static (int VeldNummer, string? Subpositie) ResolveVeld(
        string? sportlinkVeld, IEnumerable<(string? VeldNaam, int VeldNummer)> velden)
        => global::Planner.Shared.VeldResolver.Resolve(sportlinkVeld, velden);

    /// <inheritdoc cref="ResolveVeld(string?, IEnumerable{ValueTuple{string?, int}})"/>
    internal static (int VeldNummer, string? Subpositie) ResolveVeld(
        string? sportlinkVeld, IReadOnlyDictionary<string, int> veldenPerNaam)
        => global::Planner.Shared.VeldResolver.Resolve(sportlinkVeld, veldenPerNaam);

    /// <summary>
    /// Veldnummer bij de veldnaam zoals Sportlink die levert, of <c>0</c> als geen veld matcht.
    /// Zelfde matching als <see cref="ResolveVeld(string?, IEnumerable{ValueTuple{string?, int}})"/>
    /// — de bezetting en het herplanpad mogen nooit een eigen variant gebruiken.
    /// </summary>
    internal static int VindVeldNummer(string? sportlinkVeld, IEnumerable<VeldInfo> velden)
        => ResolveVeld(sportlinkVeld, velden.Select(v => ((string?)v.VeldNaam, v.VeldNummer))).VeldNummer;

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
    /// voor de veldnaam, dus het veldtype hoort hier gevuld te worden — niet per aanroeper. Dat was
    /// het gat: het beschikbaarheidspad vulde <c>VeldType</c> zelf ná deze conversie, maar het
    /// herplan-pad (de enige producent van <c>HerplanCheckResponse</c>) niet. Daar was
    /// <c>VeldType</c> dus altijd <c>null</c>, terwijl het e-mailantwoord er wél op filtert: stil
    /// kapot gedrag dat geen test en geen logregel zichtbaar maakte.</para>
    ///
    /// <para>Staat het veld niet in <paramref name="velden"/> (bijv. een inactief veld dat nog in een
    /// bezetting voorkomt), dan blijft <c>VeldType</c> <c>null</c> = onbekend. Filters mogen zo'n
    /// slot nooit wegfilteren — zie <see cref="VeldSoort.Onbekend"/>.</para>
    /// </summary>
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
            VeldType = veld?.VeldType,
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
    /// <c>BufferNa</c>/<c>BufferVoor</c> uit dbo.TeamRegels.</para>
    ///
    /// Deze check zat eerder alleen in <see cref="FindEarliestSlot"/>. Het pad dat op een voorkeurstijd
    /// plant keek uitsluitend naar capaciteit, waardoor wedstrijden rug-aan-rug werden ingepland met nul
    /// minuten ertussen — en de 60-minutenregel van een eerste elftal simpelweg werd overgeslagen.
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
    //
    // Dit vervangt de oude toewijzing die simpelweg télde hoeveel wedstrijden er al gelijktijdig op het
    // veld stonden ("de eerste krijgt A1, de tweede A2, ..."). Dat gaf twee soorten fouten:
    // een halfveldwedstrijd op A (banen 0+1) plus een kwartveldwedstrijd leverde "A2" op — precies bovenop
    // de eerste — en met A1 en B1 bezet en A2 vrij koos hij alsnog B1. De capaciteitscheck telde alleen
    // de fracties op, dus numeriek leek dat te passen terwijl de banen botsten.
    internal static readonly string[] BaanLabels = ["A1", "A2", "B1", "B2"];

    /// <summary>Welke kwartbanen bezet een wedstrijd met deze subpositie? Leeg = heel veld.</summary>
    internal static bool[] BanenVanSubpositie(string? subpositie)
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
    internal static int BanenNodig(decimal fractie) => fractie switch
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
    internal static string? EersteVrijeSubpositie(bool[] bezet, decimal fractie)
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
