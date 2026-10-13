namespace BlazorAdmin.Services;

/// <summary>
/// Welke menu-items de gebruiker ziet (#1647): een admin ziet alles; een andere rol alleen de sleutels die de
/// server teruggaf op basis van "Rechten per rol" (<c>GET api/planner/menu-toegang</c>). Uitsluitend UI-gemak —
/// elk endpoint autoriseert zelf server-side, dus een verborgen item is nooit de enige beveiliging.
/// Zolang de sleutels niet (of mislukt) geladen zijn ziet een niet-admin niets behalve het Dashboard,
/// zodat een storing het menu nooit opener maakt.
/// </summary>
public sealed class MenuZichtbaarheid
{
    private readonly bool _isAdmin;
    private readonly HashSet<string>? _sleutels;

    public MenuZichtbaarheid(bool isAdmin, IEnumerable<string>? sleutels)
    {
        _isAdmin = isAdmin;
        _sleutels = sleutels is null ? null : new HashSet<string>(sleutels, StringComparer.Ordinal);
    }

    public bool Mag(string sleutel) =>
        _isAdmin
        || (_sleutels is not null && _sleutels.Contains(sleutel))
        || (_sleutels is null && sleutel == MenuKeys.Dashboard);

    /// <summary>Een groep (met kopje) is alleen zichtbaar als minstens één item erin zichtbaar is.</summary>
    public bool MagEen(params string[] sleutels) => sleutels.Any(Mag);
}
