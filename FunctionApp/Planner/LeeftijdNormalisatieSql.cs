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
///
/// <para>
/// <b>#1332:</b> cijfer-extractie i.p.v. blinde substring-strip — zelfde reden en zelfde fix als in
/// <see cref="Planner.Shared.LeeftijdNormalisatie.Normaliseer"/> en
/// <see cref="Database.Postgres.PostgresLeeftijdNormalisatie.SqlExpr"/>: Sportlinks volwoord-variant
/// "Onder 13 Meiden" heeft geen "JO"/"MO"-prefix, dus de oude keten leverde "MOOnder 13" i.p.v.
/// "MO13". T-SQL kent geen ingebouwde regex; <c>TRANSLATE</c> (Azure SQL Database, sinds
/// compatibiliteitsniveau 130) mapt elke letter en spatie naar hetzelfde vulteken, dat vervolgens
/// wegvalt via <c>REPLACE</c> — het resultaat is het cijfer, net als Postgres'
/// <c>regexp_replace(kolom, '\D', '', 'g')</c>.
/// </para>
/// </summary>
internal static class LeeftijdNormalisatieSql
{
    /// <summary>Alle letters (hoofd- en kleine letters) plus spatie — invoer voor <c>TRANSLATE</c>.</summary>
    private const string AlleLettersEnSpatie = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz ";

    /// <summary>
    /// SQL-expressie die een kolom normaliseert naar Speeltijden-sleutel.
    /// Gebruik: INNER JOIN ... ON s.[Leeftijd] = LeeftijdNormalisatieSql.SqlExpr("t.[leeftijdscategorie]")
    /// </summary>
    internal static string SqlExpr(string kolom)
    {
        var alleenCijfers = $"REPLACE(TRANSLATE({kolom}, '{AlleLettersEnSpatie}', " +
            $"REPLICATE('#', {AlleLettersEnSpatie.Length})), '#', '')";

        return $@"
        CASE
            WHEN UPPER(LTRIM(RTRIM({kolom}))) = 'SENIOREN'
                THEN '1-99'
            WHEN UPPER(LTRIM(RTRIM({kolom}))) IN ('SENIOREN VROUWEN', 'SENIOREN VR')
                THEN 'VR'
            WHEN {alleenCijfers} <> ''
                THEN (CASE WHEN {kolom} LIKE '%Meiden%' OR {kolom} LIKE '%Meisjes%'
                           THEN 'MO' ELSE 'JO' END) + {alleenCijfers}
            ELSE
                REPLACE(REPLACE(REPLACE({kolom}, 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
        END";
    }
}
