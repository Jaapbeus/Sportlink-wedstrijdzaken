namespace Planner.Shared.Infrastructure;

/// <summary>Uitkomst van <see cref="BekendeClubs.Zoek"/> (#1677).</summary>
public enum ClubZoekStatus
{
    /// <summary>De lijst is geladen en bevat de code.</summary>
    Bekend,
    /// <summary>De lijst is geladen maar bevat de code niet, of de vorm is ongeldig.</summary>
    Onbekend,
    /// <summary>Er is nog nooit een lijst succesvol geladen (eerste lading loopt nog, mislukte of duurde te lang).</summary>
    NietBeschikbaar,
}

/// <summary>Resultaat van een opzoeking in de bekende clubs (#1677).</summary>
/// <param name="Status">Bekend, onbekend of (nog) niet te toetsen.</param>
/// <param name="Code">Bij <see cref="ClubZoekStatus.Bekend"/> de opgeslagen schrijfwijze; bij
/// <see cref="ClubZoekStatus.NietBeschikbaar"/> de (syntactisch geldige) gevraagde waarde; anders <c>null</c>.</param>
public readonly record struct ClubZoekResultaat(ClubZoekStatus Status, string? Code)
{
    public static ClubZoekResultaat Bekend(string code) => new(ClubZoekStatus.Bekend, code);
    public static readonly ClubZoekResultaat Onbekend = new(ClubZoekStatus.Onbekend, null);
    public static ClubZoekResultaat NietBeschikbaar(string code) => new(ClubZoekStatus.NietBeschikbaar, code);

    /// <summary>
    /// De clubcode die voor het verzoek geldt, of <c>null</c> als de aanroeper de primaire club
    /// gebruikt. Kan de lijst (nog) niet getoetst worden, dan geldt de syntactisch geldige waarde
    /// zoals vóór #1677: een tijdelijk onbereikbare database wisselt nooit stil van club.
    /// </summary>
    public string? CodeVoorVerzoek => Status is ClubZoekStatus.Bekend or ClubZoekStatus.NietBeschikbaar ? Code : null;
}

/// <summary>
/// Kleine, gecachete lijst van de bekende clubcodes (de rijen van de instellingentabel), per tier
/// gevoed met een eigen laad-delegate (#1677). De clubcode uit het verzoek wordt hieraan getoetst
/// voordat ze wordt gebruikt (zie <see cref="ClubZoekResultaat.CodeVoorVerzoek"/> en
/// <see cref="ClubCodeVoorVerzoek"/>).
/// <para>
/// Foutgedrag en blokkeren: een verlopen lijst wordt direct teruggegeven terwijl precies één
/// achtergrondverversing loopt (stale-while-revalidate); een mislukte verversing laat de oude lijst
/// staan. Zolang er nog nooit een lijst geladen is, wachten alle lezers op dezelfde laadtaak,
/// hoogstens <see cref="EersteLadingTimeout"/>; daarna, en na een mislukte eerste lading tot de
/// volgende poging (na <see cref="FoutTerugval"/>), is het resultaat
/// <see cref="ClubZoekStatus.NietBeschikbaar"/>. Er wordt nooit een uitzondering doorgegeven.
/// </para>
/// <para>
/// Een syntactisch geldige maar onbekende waarde laat de lijst opnieuw laden (#1689), zodat een pas
/// toegevoegde club meteen herkend wordt: hoogstens één lading per <see cref="HerlaadOndergrens"/>
/// (gerekend vanaf de start van de vorige), alleen als de vorige lading slaagde, en de lezer wacht
/// daar even begrensd op als bij de eerste lading. Binnen de ondergrens is het resultaat direct
/// <see cref="ClubZoekStatus.Onbekend"/>; zo kan een reeks onbekende waarden de database niet belasten.
/// </para>
/// <para>
/// Hoofdletters: een exacte match gaat voor; anders telt een match zonder onderscheid in hoofdletters,
/// en bij meerdere zulke varianten (mogelijk op een tier met hoofdlettergevoelige vergelijking) altijd
/// de ordinaal kleinste, los van de volgorde waarin de rijen binnenkomen (#1689).
/// </para>
/// <para>
/// Logging: faalt het laden <see cref="StructureleFoutDrempel"/> keer achter elkaar, dan volgt één
/// waarschuwing met het aantal en het fouttype, zonder foutmelding of clubcode (die kan
/// verbindingsgegevens bevatten); daarna opnieuw bij elk tienvoud van dat aantal, en één regel bij herstel.
/// </para>
/// </summary>
public sealed class BekendeClubs
{
    public static readonly TimeSpan StandaardGeldigheid = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FoutTerugval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan EersteLadingTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HerlaadOndergrens = TimeSpan.FromSeconds(10);
    public const int StructureleFoutDrempel = 3;

    // Een clubcode is max 20 tekens [A-Za-z0-9_-] (kolomdefinitie): iets anders is nooit bekend.
    private static readonly System.Text.RegularExpressions.Regex GeldigeVorm =
        new("^[A-Za-z0-9_-]{1,20}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly Func<IReadOnlyCollection<string>> _laad;
    private readonly TimeSpan _geldigheid;
    private readonly TimeSpan _eersteLadingTimeout;
    private readonly Func<DateTime> _nuUtc;
    private readonly object _slot = new();
    private ClubLijst _clubs = ClubLijst.Leeg;
    private bool _geladen;
    private bool _laatsteLadingGeslaagd;
    private bool _verversingBezig;
    private int _opeenvolgendeFouten;
    private Task _lopend = Task.CompletedTask;
    private DateTime _volgendeLadingUtc = DateTime.MinValue;
    private DateTime _laatsteStartUtc = DateTime.MinValue;

    public BekendeClubs(Func<IReadOnlyCollection<string>> laad, TimeSpan? geldigheid = null,
        Func<DateTime>? nuUtc = null, TimeSpan? eersteLadingTimeout = null)
    {
        _laad = laad;
        _geldigheid = geldigheid ?? StandaardGeldigheid;
        _nuUtc = nuUtc ?? (() => DateTime.UtcNow);
        _eersteLadingTimeout = eersteLadingTimeout ?? EersteLadingTimeout;
    }

    /// <summary>Doel van de geaggregeerde foutlog; per tier bij het opstarten gekoppeld (#1689).</summary>
    public Microsoft.Extensions.Logging.ILogger Log { get; set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>Leest de eerste kolom van <paramref name="sql"/> als lijst clubcodes; sluit de verbinding.</summary>
    public static IReadOnlyCollection<string> LeesCodes(System.Data.Common.DbConnection verbinding, string sql)
    {
        using (verbinding)
        {
            verbinding.Open();
            using var cmd = verbinding.CreateCommand();
            cmd.CommandTimeout = 5;
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var codes = new List<string>();
            while (r.Read()) if (!r.IsDBNull(0)) codes.Add(r.GetString(0));
            return codes;
        }
    }

    /// <summary>
    /// De clubcode voor een verzoek met headerwaarde <paramref name="headerWaarde"/>: de getoetste
    /// <see cref="ClubZoekResultaat.CodeVoorVerzoek"/>, anders <paramref name="primaireClub"/>
    /// (alleen dan aangeroepen).
    /// </summary>
    public string ClubCodeVoorVerzoek(string? headerWaarde, Func<string> primaireClub)
        => Zoek(headerWaarde).CodeVoorVerzoek ?? primaireClub();

    /// <summary>Toetst <paramref name="clubCode"/> aan de bekende clubs; een ongeldige vorm is altijd onbekend.</summary>
    public ClubZoekResultaat Zoek(string? clubCode)
    {
        if (string.IsNullOrWhiteSpace(clubCode) || !GeldigeVorm.IsMatch(clubCode)) return ClubZoekResultaat.Onbekend;
        var (clubs, geladen) = Momentopname();
        if (!geladen) return ClubZoekResultaat.NietBeschikbaar(clubCode);
        if (clubs.Zoek(clubCode) is { } bekend) return ClubZoekResultaat.Bekend(bekend);
        if (!HerlaadVoorOnbekend()) return ClubZoekResultaat.Onbekend;
        lock (_slot) clubs = _clubs;
        return clubs.Zoek(clubCode) is { } nieuw ? ClubZoekResultaat.Bekend(nieuw) : ClubZoekResultaat.Onbekend;
    }

    /// <summary>Wacht tot een lopende verversing klaar is; bedoeld voor tests en afsluiten.</summary>
    public bool WachtOpVerversing(TimeSpan max)
    {
        Task taak;
        lock (_slot) taak = _lopend;
        return taak.Wait(max);
    }

    private (ClubLijst Clubs, bool Geladen) Momentopname()
    {
        Task taak;
        lock (_slot)
        {
            if (_nuUtc() >= _volgendeLadingUtc && !_verversingBezig) StartVerversing();
            if (_geladen) return (_clubs, true);
            taak = _lopend;
        }
        // Nog nooit geladen: wacht (begrensd) op de gedeelde laadtaak, ook als een andere lezer hem startte.
        taak.Wait(_eersteLadingTimeout);
        lock (_slot) return (_clubs, _geladen);
    }

    // Onbekende waarde: wacht begrensd op een lopende lading, of start er een als de ondergrens
    // verstreken is en de vorige slaagde. false = geen nieuwe lijst te verwachten.
    private bool HerlaadVoorOnbekend()
    {
        Task taak;
        lock (_slot)
        {
            if (!_verversingBezig)
            {
                if (!_laatsteLadingGeslaagd || _nuUtc() < _laatsteStartUtc + HerlaadOndergrens) return false;
                StartVerversing();
            }
            taak = _lopend;
        }
        taak.Wait(_eersteLadingTimeout);
        return true;
    }

    // Aanroepen binnen _slot. De wachtende lezers wachten op een TaskCompletionSource, zodat een
    // Wait nooit de lading zelf inline (en dus onbegrensd) op de eigen thread uitvoert.
    private void StartVerversing()
    {
        _verversingBezig = true;
        _laatsteStartUtc = _nuUtc();
        var klaar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lopend = klaar.Task;
        _ = Task.Run(() =>
        {
            try { Ververs(); }
            finally { klaar.TrySetResult(); }
        });
    }

    private void Ververs()
    {
        try
        {
            var nieuw = ClubLijst.Van(_laad());
            int hersteldNa;
            lock (_slot)
            {
                _clubs = nieuw;
                _geladen = true;
                _laatsteLadingGeslaagd = true;
                _volgendeLadingUtc = _nuUtc() + _geldigheid;
                hersteldNa = _opeenvolgendeFouten;
                _opeenvolgendeFouten = 0;
            }
            if (hersteldNa >= StructureleFoutDrempel)
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Log,
                    "Laden van de clublijst is hersteld na {AantalMislukt} opeenvolgende mislukte pogingen.", hersteldNa);
        }
        catch (Exception ex)
        {
            int aantal;
            lock (_slot)
            {
                _laatsteLadingGeslaagd = false;
                _volgendeLadingUtc = _nuUtc() + FoutTerugval;
                aantal = ++_opeenvolgendeFouten;
            }
            if (IsMeldmoment(aantal))
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(Log,
                    "Laden van de clublijst faalt structureel: {AantalMislukt} opeenvolgende pogingen mislukt (laatste fouttype {FoutType}). " +
                    "Clubcodes worden getoetst aan de laatst geladen lijst, of zonder lijst alleen op vorm.",
                    aantal, ex.GetType().Name);
        }
        finally
        {
            lock (_slot) _verversingBezig = false;
        }
    }

    // Drempel, daarna elk tienvoud: geaggregeerd in plaats van per poging.
    private static bool IsMeldmoment(int aantal)
    {
        if (aantal < StructureleFoutDrempel || aantal % StructureleFoutDrempel != 0) return false;
        var factor = aantal / StructureleFoutDrempel;
        while (factor % 10 == 0) factor /= 10;
        return factor == 1;
    }

    /// <summary>Onveranderlijke momentopname: exacte schrijfwijzen plus een vaste keuze zonder hoofdletteronderscheid.</summary>
    private sealed class ClubLijst
    {
        public static readonly ClubLijst Leeg = Van([]);

        private readonly HashSet<string> _exact;
        private readonly Dictionary<string, string> _zonderHoofdletters;

        private ClubLijst(HashSet<string> exact, Dictionary<string, string> zonderHoofdletters)
        {
            _exact = exact;
            _zonderHoofdletters = zonderHoofdletters;
        }

        public static ClubLijst Van(IEnumerable<string> codes)
        {
            var exact = new HashSet<string>(StringComparer.Ordinal);
            var zonder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ruw in codes)
            {
                if (string.IsNullOrWhiteSpace(ruw)) continue;
                var code = ruw.Trim();
                exact.Add(code);
                if (!zonder.TryGetValue(code, out var huidig) || string.CompareOrdinal(code, huidig) < 0)
                    zonder[code] = code;
            }
            return new ClubLijst(exact, zonder);
        }

        public string? Zoek(string code)
            => _exact.Contains(code) ? code : _zonderHoofdletters.TryGetValue(code, out var gevonden) ? gevonden : null;
    }
}
