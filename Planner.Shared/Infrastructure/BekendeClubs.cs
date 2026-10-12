namespace Planner.Shared.Infrastructure;

/// <summary>
/// Kleine, gecachete lijst van de bekende clubcodes (de rijen van de instellingentabel), per tier
/// gevoed met een eigen laad-delegate (#1677). De <c>X-Club-Code</c>-waarde wordt hieraan getoetst
/// voordat ze als clubcode wordt gebruikt; een onbekende waarde geeft <c>null</c> zodat de
/// aanroeper op de primaire club terugvalt.
/// <para>
/// Foutgedrag en blokkeren: een verlopen lijst wordt direct teruggegeven terwijl precies één
/// achtergrondverversing loopt (stale-while-revalidate); een mislukte verversing laat de oude lijst
/// staan. Alleen de allereerste lading is blokkerend, voor één lader en hoogstens
/// <see cref="EersteLadingTimeout"/>; gelijktijdige lezers wachten niet maar krijgen "onbekend"
/// (de aanroeper valt dan terug op de primaire club). Mislukt of duurt de eerste lading te lang, dan
/// geldt tot de volgende poging (na <see cref="FoutTerugval"/>) dezelfde terugval. Er wordt nooit
/// een uitzondering doorgegeven.
/// </para>
/// </summary>
public sealed class BekendeClubs
{
    public static readonly TimeSpan StandaardGeldigheid = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FoutTerugval = TimeSpan.FromSeconds(15);

    // Een ClubCode is max 20 tekens [A-Za-z0-9_-] (kolomdefinitie): iets anders is nooit bekend.
    private static readonly System.Text.RegularExpressions.Regex GeldigeVorm =
        new("^[A-Za-z0-9_-]{1,20}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static readonly TimeSpan EersteLadingTimeout = TimeSpan.FromSeconds(5);

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

    /// <summary>De bekende clubcode (zoals opgeslagen) of <c>null</c> als de waarde niet bekend is.</summary>
    public string? Zoek(string? clubCode)
    {
        if (string.IsNullOrWhiteSpace(clubCode) || !GeldigeVorm.IsMatch(clubCode)) return null;
        var clubs = Momentopname();
        return clubs.TryGetValue(clubCode.Trim(), out var bekend) ? bekend : null;
    }

    /// <summary>Wacht tot een lopende verversing klaar is; bedoeld voor tests en afsluiten.</summary>
    public bool WachtOpVerversing(TimeSpan max)
    {
        Task taak;
        lock (_slot) taak = _lopend;
        return taak.Wait(max);
    }

    private Dictionary<string, string> Momentopname()
    {
        bool eerste;
        Task taak;
        lock (_slot)
        {
            if (_nuUtc() < _volgendeLadingUtc || _verversingBezig) return _clubs;
            _verversingBezig = true;
            eerste = !_geladen;
            _lopend = taak = Task.Run(Ververs);
        }
        // Alleen de allereerste lading wacht (begrensd); anders blijft de oude lijst direct beschikbaar.
        if (eerste) taak.Wait(_eersteLadingTimeout);
        lock (_slot) return _clubs;
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
