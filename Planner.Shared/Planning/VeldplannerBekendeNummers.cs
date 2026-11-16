namespace Planner.Shared.Planning;

/// <summary>Hergebruikt geverifieerde sleutels uit de eigen dagregels vóór een externe detailaanroep.</summary>
public static class VeldplannerBekendeNummers
{
    // De dagrepositories leveren alleen PublicMatchId == VerifiedPublicMatchId, gekoppeld op club + wedstrijdcode.
    // Nummer 1 (clubwedstrijd), nul en ontbrekende nummers blijven via de externe detailaanroep lopen.
    public static Func<string, Task<long?>> Opvrager(IEnumerable<EigenSleutel> sleutels, Func<string, Task<long?>> onbekend)
    {
        var bekend = sleutels.Where(s => !string.IsNullOrWhiteSpace(s.PublicMatchId) && s.Wedstrijdnummer.HasValue)
            .GroupBy(s => s.PublicMatchId!, StringComparer.Ordinal)
            .Where(g => g.All(s => s.Wedstrijdnummer > 1) && g.Select(s => s.Wedstrijdnummer).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Wedstrijdnummer, StringComparer.Ordinal);
        return sleutel => bekend.TryGetValue(sleutel, out var nummer) ? Task.FromResult(nummer) : onbekend(sleutel);
    }
}
