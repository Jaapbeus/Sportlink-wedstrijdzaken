namespace Planner.Shared;

/// <summary>
/// Normaliseert een leeftijdscategorie naar de sleutel waarop de Speeltijden-tabel is
/// geïndexeerd. Sportlink kan meisjesteams als "JO15 Meiden" aanleveren in plaats van "MO15",
/// en oudere formats gebruiken "Onder 15"/"Meisjes Onder 15" (#486).
///
/// <para>
/// Voorbeelden: "JO15 Meiden" → "MO15", "JO9 Meiden" → "MO9", "JO15" → "JO15",
/// "Meisjes Onder 15" → "MO15", "Vrouwen" → "VR", "Senioren" → "1-99".
/// </para>
///
/// <para>
/// <b>Waarom dit hier staat en niet per tier (#889).</b> Deze methode is pure C# zonder enige
/// database-afhankelijkheid. Tot deze verhuizing stond hij in de SQL Server-tier, samen met de
/// SQL-generatie die er wél engine-specifiek is. Die combinatie hield stand zolang alleen de
/// SQL-kant een tweede tier nodig had (#888 bouwde daarvoor
/// <c>Database.Postgres.PostgresLeeftijdNormalisatie.SqlExpr</c>), maar zodra de eerste
/// Postgres-consument ook de <i>pure</i> logica nodig had — <c>TeamCanonicalisatieService</c> —
/// zou de enige andere uitweg een tweede, onafhankelijke kopie van deze regels zijn geweest.
/// Dat is precies de drift die <c>VeldResolutieDriftTests</c> voor de veldresolutie bewaakt.
/// </para>
///
/// <para>
/// De <b>SQL-generatie</b> is bewust <i>niet</i> meeverhuisd: die verschilt per engine
/// (<c>+</c> vs. <c>||</c>, <c>LTRIM(RTRIM(...))</c> vs. <c>TRIM(...)</c>,
/// <c>LIKE</c> vs. <c>ILIKE</c>) en leeft daarom per tier —
/// <c>SportlinkFunction.Planner.LeeftijdNormalisatieSql</c> respectievelijk
/// <c>Database.Postgres.PostgresLeeftijdNormalisatie</c>. Beide moeten dezelfde uitkomst geven
/// als deze methode; dat is de invariant om in de gaten te houden bij een wijziging hier.
/// </para>
/// </summary>
public static class LeeftijdNormalisatie
{
    /// <summary>Normaliseert een leeftijdscategorie-string naar de Speeltijden-sleutel.</summary>
    public static string Normaliseer(string? cat)
    {
        if (string.IsNullOrWhiteSpace(cat)) return "";

        var trimmed = cat.Trim();

        // Senioren-categorieen gebruiken vaste Speeltijden-sleutels.
        if (trimmed.Equals("Senioren", StringComparison.OrdinalIgnoreCase))
            return "1-99";

        if (trimmed.Equals("Senioren Vrouwen", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Senioren VR", StringComparison.OrdinalIgnoreCase))
            return "VR";

        // "JO{n} Meiden" → "MO{n}" (Sportlink-specifiek formaat voor meisjesteams)
        if (trimmed.Contains("Meiden", StringComparison.OrdinalIgnoreCase))
        {
            var num = trimmed
                .Replace("JO", "", StringComparison.OrdinalIgnoreCase)
                .Replace("MO", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Meiden", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
            return $"MO{num}";
        }

        return trimmed
            .Replace("Onder ", "JO")
            .Replace("Meisjes ", "MO")
            .Replace("Vrouwen", "VR");
    }
}
