namespace Database.Postgres;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/LeeftijdNormalisatie.SqlExpr</c> (#888).
/// Alleen de SQL-generatie is vertaald — de pure C#-methode <c>Normaliseer</c> heeft geen
/// SQL Server-afhankelijkheid en is (nog) niet naar een gedeeld project verhuisd; zie de
/// toelichting in ARCHITECTUUR-DATABASE-TIERS.md over waarom dat bewust buiten deze PR valt.
/// <para>
/// Vertaling: <c>+</c> (stringconcat) → <c>||</c>, <c>LTRIM(RTRIM(...))</c> → <c>TRIM(...)</c>.
/// <c>LIKE '%Meiden'</c> → <c>ILIKE '%Meiden%'</c>: SQL Server's default collatie
/// (<c>Latin1_General_CI_AS</c>) maakt <c>LIKE</c> daar al hoofdletterongevoelig; Postgres'
/// <c>LIKE</c> is dat niet. Zelfde soort fix als <c>~</c> → <c>~*</c> in
/// <see cref="PostgresPlannerViewGenerator"/> (#819) — de systemische collatie-/
/// hoofdlettergevoeligheidskwestie voor de hele Postgres-tier blijft #820's scope; dit is een
/// lokale, empirisch-gemotiveerde fix voor déze ene expressie.
/// </para>
/// <para>
/// <b>#1332:</b> cijfer-extractie i.p.v. blinde substring-strip. De oude
/// <c>REPLACE(REPLACE(REPLACE(kolom,'JO',''),'MO',''),' Meiden','')</c>-keten nam aan dat het
/// cijfer al direct na "JO"/"MO" stond; voor Sportlinks volwoord-variant "Onder 13 Meiden" (geen
/// "JO"/"MO"-prefix) leverde dat de onbestaande sleutel "MOOnder 13" op i.p.v. "MO13" — precies
/// de wedstrijden die daardoor stil uit de Dagplanning-Gantt verdwenen (duur bleef 0). Zie
/// <see cref="Planner.Shared.LeeftijdNormalisatie.Normaliseer"/> voor de C#-tegenhanger; beide
/// moeten dezelfde uitkomst geven.
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
            WHEN regexp_replace({kolom}, '\D', '', 'g') <> ''
                THEN (CASE WHEN {kolom} ILIKE '%Meiden%' OR {kolom} ILIKE '%Meisjes%'
                           THEN 'MO' ELSE 'JO' END) || regexp_replace({kolom}, '\D', '', 'g')
            ELSE
                REPLACE(REPLACE(REPLACE({kolom}, 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
        END";
}
