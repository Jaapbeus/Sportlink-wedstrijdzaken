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
            WHEN UPPER(LTRIM(RTRIM({kolom}))) = '1-99'
                THEN '1-99'
            WHEN UPPER(LTRIM(RTRIM({kolom}))) IN ('SENIOREN VROUWEN', 'SENIOREN VR')
                THEN 'VR'
            WHEN {alleenCijfers} <> ''
                THEN (CASE WHEN {kolom} LIKE '%Meiden%' OR {kolom} LIKE '%Meisjes%'
                                OR UPPER(LTRIM(RTRIM({kolom}))) LIKE 'MO%'
                           THEN 'MO' ELSE 'JO' END) + {alleenCijfers}
            ELSE
                REPLACE(REPLACE(REPLACE({kolom}, 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
        END";
    }

    /// <summary>
    /// Speeltijdsleutel uit categorie <i>én</i> teamnaam (#1863): een O23-team ("[club] O23-1", "JO23-2") krijgt
    /// <c>JO23</c> als die rij voor de club bestaat met een duur &gt; 0, ook als Sportlink het team als "Senioren"
    /// (<c>1-99</c>) aanlevert. Anders de gewone <see cref="SqlExpr"/>: nooit duur 0 door deze regel. De grens van
    /// "O23" is dezelfde als <see cref="Planner.Shared.TeamNaamNormalisatie.IsO23Team"/> (geen "MO23", geen "O230").
    /// </summary>
    internal static string SpeeltijdSleutelSqlExpr(string categorieKolom, string teamNaamKolom, string clubCodeExpr) => $@"
        CASE
            WHEN (' ' + UPPER({teamNaamKolom}) + ' ' LIKE '%[^A-Z0-9]O23[^0-9]%'
                  OR ' ' + UPPER({teamNaamKolom}) + ' ' LIKE '%[^A-Z0-9]JO23[^0-9]%')
                 AND EXISTS (SELECT 1 FROM [dbo].[Speeltijden] jo23
                             WHERE jo23.[Leeftijd] = 'JO23' AND jo23.[WedstrijdTotaal] > 0 AND jo23.[ClubCode] = {clubCodeExpr})
                THEN 'JO23'
            ELSE {SqlExpr(categorieKolom)}
        END";
}
