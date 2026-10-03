namespace Planner.Shared.Feedback;

/// <summary>
/// Rate limiter voor feedback-submits — gedeeld tussen beide tiers omdat elke club-deployment maar
/// één tier tegelijk draait (#1130).
///
/// #610 — bewuste keuze: de teller is in-memory en geldt dus per Consumption-plan-instance, niet
/// globaal. Bij opschaling kan de effectieve limiet een veelvoud van <see cref="MaxSubmissiesPerVenster"/>
/// zijn. Acceptabel omdat dit endpoint admin-only is (<c>RequireAdmin</c>) en de limiet bedoeld is
/// als rem tegen per ongeluk doorklikken, niet als beveiligingsgrens tegen een aanvaller. Een
/// gedeelde store (SQL/Table Storage) zou een extra round-trip en onderhoud kosten zonder dat het
/// risico dat rechtvaardigt. Wordt dit ooit een publiek endpoint, dan is een gedeelde teller wél nodig.
/// </summary>
public static class FeedbackRateLimiter
{
    public const int MaxSubmissiesPerVenster = 5;
    private static readonly TimeSpan RateLimitVenster = TimeSpan.FromMinutes(10);
    private static readonly Queue<DateTime> _submits = new();
    private static readonly object _rateLock = new();

    // #764: nu elke ingelogde gebruiker de widget mag gebruiken, kost elke gebruiker die zonder rem
    // /validate of /preview aanroept een betaalde AI-aanroep. Teller per gebruiker, in-memory:
    // voor deze rem (bescherming tegen per ongeluk doorklikken en een lopende script) is dat
    // genoeg — de harde grens voor opslag zit per gebruiker in de database (zie
    // FeedbackEndpointCore). Een gelijkwaardige limiet geldt dus op beide tiers.
    public const int MaxAiAanroepenPerVenster = 30;
    private static readonly Dictionary<string, Queue<DateTime>> _perGebruiker = new();

    public static bool TryAcquireAiSlot(string gebruikerSleutel)
    {
        lock (_rateLock)
        {
            var nu = DateTime.UtcNow;
            var cutoff = nu - RateLimitVenster;
            if (!_perGebruiker.TryGetValue(gebruikerSleutel, out var wachtrij))
            {
                // Houd het geheugen begrensd: oude, lege sleutels meteen opruimen.
                if (_perGebruiker.Count > 500)
                    foreach (var sleutel in _perGebruiker.Where(kv => kv.Value.Count == 0 || kv.Value.Peek() < cutoff).Select(kv => kv.Key).ToList())
                        _perGebruiker.Remove(sleutel);
                wachtrij = _perGebruiker[gebruikerSleutel] = new Queue<DateTime>();
            }
            while (wachtrij.TryPeek(out var eerste) && eerste < cutoff)
                wachtrij.Dequeue();
            if (wachtrij.Count >= MaxAiAanroepenPerVenster) return false;
            wachtrij.Enqueue(nu);
            return true;
        }
    }

    public static bool TryAcquireSubmitSlot()
    {
        lock (_rateLock)
        {
            var cutoff = DateTime.UtcNow - RateLimitVenster;
            while (_submits.TryPeek(out var first) && first < cutoff)
                _submits.Dequeue();
            if (_submits.Count >= MaxSubmissiesPerVenster) return false;
            _submits.Enqueue(DateTime.UtcNow);
            return true;
        }
    }
}
