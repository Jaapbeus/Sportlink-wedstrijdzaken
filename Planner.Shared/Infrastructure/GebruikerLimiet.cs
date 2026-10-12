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
    private readonly Dictionary<string, Queue<(DateTime Tijdstip, long Id)>> _perSleutel = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private long _volgnummer;

    public int MaxPerVenster { get; } = maxPerVenster;

    /// <summary>Een genomen slot; alleen bruikbaar om precies dít slot terug te geven.</summary>
    public readonly record struct Slot(string Sleutel, long Id);

    /// <summary>Telt de aanroep mee en geeft <c>true</c>; <c>false</c> als de limiet voor deze sleutel is bereikt.</summary>
    public bool TryAcquire(string? sleutel) => TryAcquire(sleutel, out _);

    /// <summary>
    /// Als <see cref="TryAcquire(string?)"/>, en geeft het genomen slot mee zodat precies dát slot
    /// later met <see cref="TerugGeven(Slot)"/> kan worden vrijgegeven.
    /// </summary>
    public bool TryAcquire(string? sleutel, out Slot slot)
    {
        var key = string.IsNullOrWhiteSpace(sleutel) ? "onbekend" : sleutel;
        slot = default;
        lock (_lock)
        {
            var nu = _nu();
            var cutoff = nu - venster;
            if (!_perSleutel.TryGetValue(key, out var wachtrij))
            {
                if (_perSleutel.Count > OpruimDrempel)
                    // Alleen sleutels waarvan ALLE items verlopen zijn: een wachtrij met nog geldige items hoort bij een
                    // lopende limiet en mag niet worden weggegooid (dat zou de teller resetten).
                    foreach (var oud in _perSleutel.Where(kv => kv.Value.Count == 0 || kv.Value.Last().Tijdstip < cutoff).Select(kv => kv.Key).ToList())
                        _perSleutel.Remove(oud);
                wachtrij = _perSleutel[key] = new Queue<(DateTime, long)>();
            }
            while (wachtrij.TryPeek(out var eerste) && eerste.Tijdstip < cutoff)
                wachtrij.Dequeue();
            if (wachtrij.Count >= MaxPerVenster) return false;
            var id = ++_volgnummer;
            wachtrij.Enqueue((nu, id));
            slot = new Slot(key, id);
            return true;
        }
    }

    /// <summary>
    /// Geeft precies dit slot terug, bijv. als de handeling waarvoor het werd genomen niet is gelukt.
    /// Nooit een ander slot: bij gelijktijdige aanroepen van dezelfde sleutel zou het wissen van "het
    /// laatste" slot het tijdstip van een andere, geslaagde aanroep weghalen, waarna de limiet in een
    /// later venster kan worden overschreden. Geen effect als het slot al uit het venster is verdwenen
    /// (het telt dan al niet meer mee) of al is teruggegeven.
    /// </summary>
    public void TerugGeven(Slot slot)
    {
        if (slot.Sleutel is null) return;
        lock (_lock)
        {
            if (!_perSleutel.TryGetValue(slot.Sleutel, out var wachtrij) || !wachtrij.Any(s => s.Id == slot.Id))
                return;
            _perSleutel[slot.Sleutel] = new Queue<(DateTime, long)>(wachtrij.Where(s => s.Id != slot.Id));
        }
    }
}
