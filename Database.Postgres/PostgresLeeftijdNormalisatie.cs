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
            WHEN UPPER(TRIM({kolom})) = '1-99'
                THEN '1-99'
            WHEN UPPER(TRIM({kolom})) IN ('SENIOREN VROUWEN', 'SENIOREN VR')
                THEN 'VR'
            WHEN regexp_replace({kolom}, '\D', '', 'g') <> ''
                THEN (CASE WHEN {kolom} ILIKE '%Meiden%' OR {kolom} ILIKE '%Meisjes%'
                                OR UPPER(TRIM({kolom})) LIKE 'MO%'
                           THEN 'MO' ELSE 'JO' END) || regexp_replace({kolom}, '\D', '', 'g')
            ELSE
                REPLACE(REPLACE(REPLACE({kolom}, 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
        END";

    /// <summary>
    /// Speeltijdsleutel uit categorie <i>én</i> teamnaam (#1863): een O23-team ("[club] O23-1", "JO23-2") krijgt
    /// <c>JO23</c> als die rij voor de club bestaat met een duur &gt; 0, ook als Sportlink het team als "Senioren"
    /// (<c>1-99</c>) aanlevert. Anders de gewone <see cref="SqlExpr"/>: nooit duur 0 door deze regel. De grens van
    /// "O23" is dezelfde als <see cref="Planner.Shared.TeamNaamNormalisatie.IsO23Team"/> (geen "MO23", geen "O230").
    /// </summary>
    public static string SpeeltijdSleutelSqlExpr(string categorieKolom, string teamNaamKolom, string clubCodeExpr) => $@"
        CASE
            WHEN {O23Jo23Voorwaarde(teamNaamKolom, clubCodeExpr)}
                THEN 'JO23'
            ELSE {SqlExpr(categorieKolom)}
        END";

    /// <summary>
    /// De enige Postgres-formulering van de O23-regel (#1863): het team is een O23-team én de club heeft een
    /// <c>JO23</c>-speeltijd met duur &gt; 0. Gedeeld door <see cref="SpeeltijdSleutelSqlExpr"/> en
    /// <see cref="PostgresPlannerViewGenerator"/>, zodat er in deze tier één kopie van het teamnaampatroon is.
    /// SQL kan <see cref="Planner.Shared.TeamNaamNormalisatie.IsO23Team"/> niet aanroepen; de pariteit met C#
    /// bewaakt <c>PostgresLeeftijdNormalisatieSqlIntegrationTests</c>.
    /// </summary>
    public static string O23Jo23Voorwaarde(string teamNaamKolom, string clubCodeExpr) =>
        $"{teamNaamKolom} ~* '(^|[^[:alnum:]])J?O23([^0-9]|$)' "
        + $"AND EXISTS (SELECT 1 FROM public.speeltijden jo23 WHERE jo23.leeftijd = 'JO23' AND jo23.wedstrijdtotaal > 0 AND jo23.clubcode = {clubCodeExpr})";
}
