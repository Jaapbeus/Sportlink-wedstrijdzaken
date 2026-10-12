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
/// gevoed met een eigen laad-delegate (#1677). De <c>X-Club-Code</c>-waarde wordt hieraan getoetst
/// voordat ze als clubcode wordt gebruikt (zie <see cref="ClubZoekResultaat.CodeVoorVerzoek"/>).
/// <para>
/// Foutgedrag en blokkeren: een verlopen lijst wordt direct teruggegeven terwijl precies één
/// achtergrondverversing loopt (stale-while-revalidate); een mislukte verversing laat de oude lijst
/// staan. Zolang er nog nooit een lijst geladen is, wachten alle lezers op dezelfde laadtaak,
/// hoogstens <see cref="EersteLadingTimeout"/>; daarna, en na een mislukte eerste lading tot de
/// volgende poging (na <see cref="FoutTerugval"/>), is het resultaat
/// <see cref="ClubZoekStatus.NietBeschikbaar"/>. Er wordt nooit een uitzondering doorgegeven.
/// </para>
/// </summary>
public sealed class BekendeClubs
{
    public static readonly TimeSpan StandaardGeldigheid = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FoutTerugval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan EersteLadingTimeout = TimeSpan.FromSeconds(5);

    // Een ClubCode is max 20 tekens [A-Za-z0-9_-] (kolomdefinitie): iets anders is nooit bekend.
    private static readonly System.Text.RegularExpressions.Regex GeldigeVorm =
        new("^[A-Za-z0-9_-]{1,20}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly Func<IReadOnlyCollection<string>> _laad;
    private readonly TimeSpan _geldigheid;
    private readonly TimeSpan _eersteLadingTimeout;
    private readonly Func<DateTime> _nuUtc;
    private readonly object _slot = new();
    private Dictionary<string, string> _clubs = new(StringComparer.OrdinalIgnoreCase);
    private bool _geladen;
    private bool _verversingBezig;
    private Task _lopend = Task.CompletedTask;
    private DateTime _volgendeLadingUtc = DateTime.MinValue;

    public BekendeClubs(Func<IReadOnlyCollection<string>> laad, TimeSpan? geldigheid = null,
        Func<DateTime>? nuUtc = null, TimeSpan? eersteLadingTimeout = null)
    {
        _laad = laad;
        _geldigheid = geldigheid ?? StandaardGeldigheid;
        _nuUtc = nuUtc ?? (() => DateTime.UtcNow);
        _eersteLadingTimeout = eersteLadingTimeout ?? EersteLadingTimeout;
    }

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

    /// <summary>Toetst <paramref name="clubCode"/> aan de bekende clubs; een ongeldige vorm is altijd onbekend.</summary>
    public ClubZoekResultaat Zoek(string? clubCode)
    {
        if (string.IsNullOrWhiteSpace(clubCode) || !GeldigeVorm.IsMatch(clubCode)) return ClubZoekResultaat.Onbekend;
        var (clubs, geladen) = Momentopname();
        if (!geladen) return ClubZoekResultaat.NietBeschikbaar(clubCode);
        return clubs.TryGetValue(clubCode, out var bekend) ? ClubZoekResultaat.Bekend(bekend) : ClubZoekResultaat.Onbekend;
    }

    /// <summary>Wacht tot een lopende verversing klaar is; bedoeld voor tests en afsluiten.</summary>
    public bool WachtOpVerversing(TimeSpan max)
    {
        Task taak;
        lock (_slot) taak = _lopend;
        return taak.Wait(max);
    }

    private (Dictionary<string, string> Clubs, bool Geladen) Momentopname()
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

    // Aanroepen binnen _slot. De wachtende lezers wachten op een TaskCompletionSource, zodat een
    // Wait nooit de lading zelf inline (en dus onbegrensd) op de eigen thread uitvoert.
    private void StartVerversing()
    {
        _verversingBezig = true;
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
            var nieuw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in _laad())
                if (!string.IsNullOrWhiteSpace(code)) nieuw[code.Trim()] = code.Trim();
            lock (_slot)
            {
                _clubs = nieuw;
                _geladen = true;
                _volgendeLadingUtc = _nuUtc() + _geldigheid;
            }
        }
        catch (Exception)
        {
            lock (_slot) _volgendeLadingUtc = _nuUtc() + FoutTerugval;
        }
        finally
        {
            lock (_slot) _verversingBezig = false;
        }
    }
}
