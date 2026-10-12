namespace Planner.Shared.Infrastructure;

/// <summary>
/// Glijdend-venster-limiet per sleutel (bijv. de Entra object-ID van de aanroeper), gedeeld door
/// beide databasetiers (#1671). De teller is in-memory en geldt per instance; het is een rem op
/// herhaald gebruik, geen globale kwotering.
/// </summary>
public sealed class GebruikerLimiet(int maxPerVenster, TimeSpan venster, Func<DateTime>? nu = null)
{
    private const int OpruimDrempel = 500;
    private readonly Func<DateTime> _nu = nu ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, Queue<DateTime>> _perSleutel = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int MaxPerVenster { get; } = maxPerVenster;

    /// <summary>Telt de aanroep mee en geeft <c>true</c>; <c>false</c> als de limiet voor deze sleutel is bereikt.</summary>
    public bool TryAcquire(string? sleutel)
    {
        var key = string.IsNullOrWhiteSpace(sleutel) ? "onbekend" : sleutel;
        lock (_lock)
        {
            var nu = _nu();
            var cutoff = nu - venster;
            if (!_perSleutel.TryGetValue(key, out var wachtrij))
            {
                if (_perSleutel.Count > OpruimDrempel)
                    // Alleen sleutels waarvan ALLE items verlopen zijn: een wachtrij met nog geldige items hoort bij een
                    // lopende limiet en mag niet worden weggegooid (dat zou de teller resetten).
                    foreach (var oud in _perSleutel.Where(kv => kv.Value.Count == 0 || kv.Value.Last() < cutoff).Select(kv => kv.Key).ToList())
                        _perSleutel.Remove(oud);
                wachtrij = _perSleutel[key] = new Queue<DateTime>();
            }
            while (wachtrij.TryPeek(out var eerste) && eerste < cutoff)
                wachtrij.Dequeue();
            if (wachtrij.Count >= MaxPerVenster) return false;
            wachtrij.Enqueue(nu);
            return true;
        }
    }

    /// <summary>
    /// Geeft het laatst genomen slot van deze sleutel terug, bijv. als de handeling waarvoor het slot
    /// werd genomen niet is gelukt. Geen effect bij een onbekende sleutel of een lege teller.
    /// </summary>
    public void TerugGeven(string? sleutel)
    {
        var key = string.IsNullOrWhiteSpace(sleutel) ? "onbekend" : sleutel;
        lock (_lock)
        {
            if (!_perSleutel.TryGetValue(key, out var wachtrij) || wachtrij.Count == 0)
                return;
            _perSleutel[key] = new Queue<DateTime>(wachtrij.Take(wachtrij.Count - 1));
        }
    }
}
