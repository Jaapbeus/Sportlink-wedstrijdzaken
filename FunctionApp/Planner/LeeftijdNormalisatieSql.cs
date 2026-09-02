namespace SportlinkFunction.Planner;

/// <summary>
/// SQL Server-tier-generatie van de leeftijdscategorie-normalisatie (#486).
///
/// <para>
/// <b>Alleen de SQL-expressie staat hier.</b> De pure C#-methode <c>Normaliseer</c> is naar
/// <see cref="Planner.Shared.LeeftijdNormalisatie"/> verhuisd (#889): die heeft geen
/// database-afhankelijkheid en werd door de eerste Postgres-consument
/// (<c>TeamCanonicalisatieService</c>) ook nodig — een tweede, onafhankelijke kopie van dezelfde
/// regels zou precies de drift opleveren die <c>VeldResolutieDriftTests</c> voor de veldresolutie
/// bewaakt. De SQL-generatie is bewust <i>niet</i> meeverhuisd: die verschilt per engine. De
/// Postgres-tegenhanger is <c>Database.Postgres.PostgresLeeftijdNormalisatie.SqlExpr</c> (#888).
/// </para>
///
/// <para>
/// <b>Invariant:</b> deze expressie en <see cref="Planner.Shared.LeeftijdNormalisatie.Normaliseer"/>
/// moeten dezelfde uitkomst geven. Wijzigt de een, dan de ander ook — en de Postgres-variant erbij.
/// </para>
/// </summary>
internal static class LeeftijdNormalisatieSql
{
    /// <summary>
    /// SQL-expressie die een kolom normaliseert naar Speeltijden-sleutel.
    /// Gebruik: INNER JOIN ... ON s.[Leeftijd] = LeeftijdNormalisatieSql.SqlExpr("t.[leeftijdscategorie]")
    /// </summary>
    internal static string SqlExpr(string kolom) => $@"
        CASE
            WHEN UPPER(LTRIM(RTRIM({kolom}))) = 'SENIOREN'
                THEN '1-99'
            WHEN UPPER(LTRIM(RTRIM({kolom}))) IN ('SENIOREN VROUWEN', 'SENIOREN VR')
                THEN 'VR'
            WHEN {kolom} LIKE '%Meiden'
                THEN 'MO' + LTRIM(RTRIM(REPLACE(REPLACE(REPLACE({kolom}, 'JO', ''), 'MO', ''), ' Meiden', '')))
            ELSE
                REPLACE(REPLACE(REPLACE({kolom}, 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
        END";
}
