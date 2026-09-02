namespace Database.Postgres;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/LeeftijdNormalisatie.SqlExpr</c> (#888).
/// Alleen de SQL-generatie is vertaald — de pure C#-methode <c>Normaliseer</c> heeft geen
/// SQL Server-afhankelijkheid en is (nog) niet naar een gedeeld project verhuisd; zie de
/// toelichting in ARCHITECTUUR-DATABASE-TIERS.md over waarom dat bewust buiten deze PR valt.
/// <para>
/// Vertaling: <c>+</c> (stringconcat) → <c>||</c>, <c>LTRIM(RTRIM(...))</c> → <c>TRIM(...)</c>.
/// <c>LIKE '%Meiden'</c> → <c>ILIKE '%Meiden'</c>: SQL Server's default collatie
/// (<c>Latin1_General_CI_AS</c>) maakt <c>LIKE</c> daar al hoofdletterongevoelig; Postgres'
/// <c>LIKE</c> is dat niet. Zelfde soort fix als <c>~</c> → <c>~*</c> in
/// <see cref="PostgresPlannerViewGenerator"/> (#819) — de systemische collatie-/
/// hoofdlettergevoeligheidskwestie voor de hele Postgres-tier blijft #820's scope; dit is een
/// lokale, empirisch-gemotiveerde fix voor déze ene expressie.
/// </para>
/// </summary>
public static class PostgresLeeftijdNormalisatie
{
    public static string SqlExpr(string kolom) => $@"
        CASE
            WHEN UPPER(TRIM({kolom})) = 'SENIOREN'
                THEN '1-99'
            WHEN UPPER(TRIM({kolom})) IN ('SENIOREN VROUWEN', 'SENIOREN VR')
                THEN 'VR'
            WHEN {kolom} ILIKE '%Meiden'
                THEN 'MO' || TRIM(REPLACE(REPLACE(REPLACE({kolom}, 'JO', ''), 'MO', ''), ' Meiden', ''))
            ELSE
                REPLACE(REPLACE(REPLACE({kolom}, 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
        END";
}
