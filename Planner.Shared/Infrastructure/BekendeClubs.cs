namespace Planner.Shared.Infrastructure;

/// <summary>
/// Kleine, gecachete lijst van de bekende clubcodes (de rijen van de instellingentabel), per tier
/// gevoed met een eigen laad-delegate (#1677). De <c>X-Club-Code</c>-waarde wordt hieraan getoetst
/// voordat ze als clubcode wordt gebruikt; een onbekende waarde geeft <c>null</c> zodat de
/// aanroeper op de primaire club terugvalt.
/// <para>
/// Foutgedrag: een mislukte lading blokkeert nooit en gooit nooit door. Is er een eerdere lijst,
/// dan blijft die gelden; anders is de lijst leeg en geldt de terugval op de primaire club. Na een
/// fout wordt pas na <see cref="FoutTerugval"/> opnieuw geprobeerd.
/// </para>
/// </summary>
public sealed class BekendeClubs
{
    public static readonly TimeSpan StandaardGeldigheid = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FoutTerugval = TimeSpan.FromSeconds(15);

    // Een ClubCode is max 20 tekens [A-Za-z0-9_-] (kolomdefinitie): iets anders is nooit bekend.
    private static readonly System.Text.RegularExpressions.Regex GeldigeVorm =
        new("^[A-Za-z0-9_-]{1,20}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly Func<IReadOnlyCollection<string>> _laad;
    private readonly TimeSpan _geldigheid;
    private readonly Func<DateTime> _nuUtc;
    private readonly object _slot = new();
    private Dictionary<string, string> _clubs = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _volgendeLadingUtc = DateTime.MinValue;

    public BekendeClubs(Func<IReadOnlyCollection<string>> laad, TimeSpan? geldigheid = null, Func<DateTime>? nuUtc = null)
    {
        _laad = laad;
        _geldigheid = geldigheid ?? StandaardGeldigheid;
        _nuUtc = nuUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>Leest de eerste kolom van <paramref name="sql"/> als lijst clubcodes; sluit de verbinding.</summary>
    public static IReadOnlyCollection<string> LeesCodes(System.Data.Common.DbConnection verbinding, string sql)
    {
        using (verbinding)
        {
            verbinding.Open();
            using var cmd = verbinding.CreateCommand();
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

    private Dictionary<string, string> Momentopname()
    {
        lock (_slot)
        {
            var nu = _nuUtc();
            if (nu < _volgendeLadingUtc) return _clubs;
            try
            {
                var nieuw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var code in _laad())
                    if (!string.IsNullOrWhiteSpace(code)) nieuw[code.Trim()] = code.Trim();
                _clubs = nieuw;
                _volgendeLadingUtc = nu + _geldigheid;
            }
            catch (Exception)
            {
                _volgendeLadingUtc = nu + FoutTerugval;
            }
            return _clubs;
        }
    }
}
