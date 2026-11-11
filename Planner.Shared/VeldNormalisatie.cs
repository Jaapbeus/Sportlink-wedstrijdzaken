namespace Planner.Shared;

/// <summary>
/// Normaliseert een Sportlink-veldstring voor vergelijking — tier-agnostisch (#819).
/// <para>
/// Puur tekstlogica zonder databaseafhankelijkheid, dus geen "gedeelde DB-providerabstractie"
/// (de architectuurregel die aparte, parallelle implementatiebomen per tier voorschrijft —
/// zie <c>docs/ARCHITECTUUR-DATABASE-TIERS.md</c> §2 — gaat over SQL-generatie/-executie, niet
/// over herbruikbare pure functies). Elke tier die deze veldstring-matching nodig heeft
/// (SQL Server via <c>FunctionApp</c>, Postgres via <c>Database.Postgres</c>) roept dezelfde
/// implementatie aan, zodat er nooit een derde, onafhankelijke kopie van deze matching-logica
/// ontstaat naast de bestaande twee (C# en de SQL Server-view).
/// </para>
/// </summary>
public static class VeldNormalisatie
{
    /// <summary>Getrimd, lowercase, dubbele spaties samengevouwen.</summary>
    public static string Normaliseer(string? veld)
    {
        if (string.IsNullOrWhiteSpace(veld)) return string.Empty;
        return veld.Trim().ToLowerInvariant().Replace("  ", " ");
    }
}
