namespace SportlinkFunction.Planner;

/// <summary>
/// Centraliseert de leeftijdscategorie-normalisatie naar Speeltijden-sleutels.
///
/// Sportlink kan meisjesteams als "JO15 Meiden" aanleveren (i.p.v. "MO15").
/// Deze utility zorgt dat zowel C#- als SQL-code consistent normaliseren. (#486)
///
/// Voorbeelden:
///   "JO15 Meiden" → "MO15"
///   "JO9 Meiden"  → "MO9"
///   "JO15"        → "JO15"
///   "Meisjes Onder 15" → "MO15" (fallback voor oudere Sportlink-formats)
///   "Vrouwen"     → "VR"
/// </summary>
internal static class LeeftijdNormalisatie
{
    /// <summary>Normaliseert een leeftijdscategorie-string naar de Speeltijden-sleutel.</summary>
    internal static string Normaliseer(string? cat)
    {
        if (string.IsNullOrWhiteSpace(cat)) return "";

        // "JO{n} Meiden" → "MO{n}" (Sportlink-specifiek formaat voor meisjesteams)
        if (cat.Contains("Meiden", StringComparison.OrdinalIgnoreCase))
        {
            var num = cat
                .Replace("JO", "", StringComparison.OrdinalIgnoreCase)
                .Replace("MO", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Meiden", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
            return $"MO{num}";
        }

        return cat
            .Replace("Onder ", "JO")
            .Replace("Meisjes ", "MO")
            .Replace("Vrouwen", "VR");
    }

    /// <summary>
    /// SQL-expressie die een kolom normaliseert naar Speeltijden-sleutel.
    /// Gebruik: INNER JOIN ... ON s.[Leeftijd] = LeeftijdNormalisatie.SqlExpr("t.[leeftijdscategorie]")
    /// </summary>
    internal static string SqlExpr(string kolom) => $@"
        CASE
            WHEN {kolom} LIKE '%Meiden'
                THEN 'MO' + LTRIM(RTRIM(REPLACE(REPLACE(REPLACE({kolom}, 'JO', ''), 'MO', ''), ' Meiden', '')))
            ELSE
                REPLACE(REPLACE(REPLACE({kolom}, 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
        END";
}
