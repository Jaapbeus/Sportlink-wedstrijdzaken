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
    private readonly Dictionary<string, Queue<DateTime>> _perSleutel = new(StringComparer.Ordinal);
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
                    foreach (var oud in _perSleutel.Where(kv => kv.Value.Count == 0 || kv.Value.Peek() < cutoff).Select(kv => kv.Key).ToList())
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
}
