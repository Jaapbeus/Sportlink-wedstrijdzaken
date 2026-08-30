namespace Database.Postgres;

/// <summary>
/// Postgres-vertaling van <c>Database/planner/Views/AlleWedstrijdenOpVeld.sql</c> (#819).
/// <para>
/// <b>Architectuurbeslissing — veldresolutie blijft C#-only, niet SQL-side herbouwd.</b> De
/// SQL Server-view lost de ruwe Sportlink-veldstring ("veld 1 A") op naar veldnummer + subpositie
/// via een <c>OUTER APPLY</c> die woordelijk spiegelt aan <see cref="global::Planner.Shared.VeldResolver"/>
/// (voorheen <c>PlannerShared.ResolveVeld</c>) — dezelfde matching-logica bestaat daardoor vandaag
/// al twee keer (T-SQL en C#), bewaakt door <c>VeldResolutieDriftTests</c>. Een letterlijke
/// <c>LATERAL</c>-vertaling van die <c>OUTER APPLY</c> zou een derde, onafhankelijke kopie zijn.
/// In plaats daarvan levert deze view de <b>ruwe, ongeresolveerde</b> veldstring terug
/// (<c>veld_ruw</c>) voor Competitie-rijen; de aanroepende C#-laag resolveert die met exact
/// dezelfde <see cref="global::Planner.Shared.VeldResolver"/> die ook de SQL Server-tier gebruikt.
/// Netto-effect: het aantal onafhankelijke implementaties blijft op twee (C# + de bestaande SQL
/// Server-view) in plaats van drie. Zie <c>Planner.Shared/VeldResolver.cs</c> voor de volledige
/// motivatie.
/// </para>
/// <para>
/// Rijen uit de "Planner"-tak (<c>planner.geplandewedstrijden</c>) hebben al een resolved
/// <c>veldnummer</c> (het is daar een FK-kolom, geen vrije tekst) — voor die rijen is
/// <c>veld_ruw</c> altijd <c>NULL</c> en <c>veldnummer</c> altijd gevuld. Voor "Competitie"-rijen
/// is het omgekeerde waar: <c>veld_ruw</c> gevuld, <c>veldnummer</c> <c>NULL</c> totdat de
/// aanroepende C#-laag resolveert.
/// </para>
/// <para>
/// <b>Wat wél in SQL blijft:</b> de <c>AppSettings</c>-<c>LATERAL</c>-join (CROSS APPLY-equivalent),
/// de <c>Speeltijden</c>-join inclusief de G-team-detectie (SQL Server's <c>LIKE '... G[0-9]%'</c>
/// bracket-patroon → Postgres' regex-operator <c>~*</c>, want Postgres' native <c>LIKE</c> kent geen
/// bracket-karakterklasses — empirisch bevestigd, zie #819-issueverslag), de datum/tijd-rekenkunde,
/// en de <c>UNION ALL</c> met <c>planner.geplandewedstrijden</c>. Het filter
/// <c>s.WedstrijdTotaal IS NOT NULL</c> (Speeltijden-join moet matchen) blijft eveneens in SQL —
/// dat is een gewone equi-join, niet geraakt door de resolutie-verschuiving. Het filter
/// <c>v.VeldNummer IS NOT NULL</c> (veld moet resolven) verhuist mee naar de C#-laag, want de
/// SQL-side veldresolutie zelf is verhuisd.
/// </para>
/// <para>
/// <b>Empirisch gevonden bug: hoofdlettergevoeligheid.</b> Een eerste versie gebruikte de
/// hoofdlettergevoelige operator <c>~</c> — de un-skipped integratietest met teamnaam
/// <c>"VRC G7-1"</c> tegen clubcode <c>"vrc"</c> (representatief: de clubcode-kolom en
/// Sportlink-teamnamen komen uit verschillende bronnen en hun casing-conventies zijn niet
/// gegarandeerd gelijk) faalde stil — geen crash, gewoon nul rijen, want de G-tak matchte nooit en
/// de daaropvolgende <c>Speeltijden</c>-join op de generieke leeftijdscategorie vond ook niets.
/// SQL Server's origineel werkt hier ongemerkt door de standaard case-insensitive collatie
/// (<c>Latin1_General_CI_AS</c>); Postgres' <c>~</c> is hoofdlettergevoelig. Fix: <c>~*</c>
/// (hoofdletterongevoelige regex-match) — spiegelt de SQL Server-collatie voor déze ene
/// vergelijking. Dezelfde onderliggende aanname (SQL Server-collatie is overal case-insensitive,
/// Postgres' <c>=</c>/<c>~</c> standaard niet) geldt ook voor de overige gelijkheidsvergelijkingen
/// in deze view (<c>t.teamnaam = m.teamnaam</c>, de clubcode-joins, <c>s.clubcode = a.clubcode</c>)
/// — dat is het systemische probleem dat #820 aanpakt (collatie-/hoofdlettergevoeligheidsfix voor
/// de Postgres-tier als geheel). Deze view draagt dus hetzelfde restrisico op die overige
/// vergelijkingen totdat #820 een tier-brede oplossing levert (bijv. een case-insensitive collatie
/// op de relevante kolommen); alleen de hier empirisch bewezen G-team-regex is lokaal gefixt omdat
/// #819's eigen acceptatiecriterium ("identieke resultaatset voor gelijke brondata") dat vereist.
/// </para>
/// <para>
/// <b>G-team-regex-parity, bewuste overeenkomst met het origineel.</b> Zowel het origineel
/// (<c>LIKE a.ClubCode + ' G[0-9]%'</c>) als deze vertaling (<c>~* ('^' || a.clubcode || ' G[0-9]')</c>)
/// interpoleren de clubcode-kolomwaarde ongefilterd in het patroon. Bevat een clubcode ooit
/// een regex-metakarakter, dan wijkt het gedrag af van een letterlijke tekstmatch — dat is een
/// bestaand risico in de SQL Server-versie (daar met LIKE-wildcards) dat deze vertaling bewust
/// ongewijzigd overneemt, niet een nieuw geïntroduceerd risico.
/// </para>
/// <para>
/// <b>#855:</b> <c>his.matches</c>/<c>his.teams</c> leverden voorheen een PascalCase
/// <c>"ClubCode"</c>-kolom (gequote, dus letterlijk zo in de database) — deze view moest die kolom
/// daarom zelf ook gequote aanspreken (<c>m."ClubCode"</c>), inconsistent met de verder overal
/// lowercase/ongequote stijl in deze view. Nu <see cref="KnownEntities"/> lowercase <c>clubcode</c>
/// gebruikt, is dat niet meer nodig.
/// </para>
/// <para>
/// <b>Schema-scope:</b> steunt op <c>his.matches</c>/<c>his.teams</c> (#818,
/// <see cref="KnownEntities"/>) en de vier minimale configuratietabellen uit
/// <see cref="PostgresPlannerSupportSchema"/>.
/// </para>
/// </summary>
public static class PostgresPlannerViewGenerator
{
    public const string ViewName = "planner.alle_wedstrijden_op_veld_ruw";

    public static string CreateView => $$"""
        CREATE OR REPLACE VIEW {{ViewName}} AS
        SELECT
            (m.kaledatum)::date                                                     AS datum,
            (m.aanvangstijd)::time                                                  AS aanvangstijd,
            ((m.kaledatum)::date + (m.aanvangstijd)::time
                + (s.wedstrijdtotaal || ' minutes')::interval)                      AS eindtijd,
            m.veld                                                                  AS veld_ruw,
            NULL::integer                                                           AS veldnummer,
            COALESCE(s.veldafmeting, 1.00)                                          AS velddeelgebruik,
            t.leeftijdscategorie                                                    AS leeftijdscategorie,
            m.teamnaam                                                              AS teamnaam,
            m.wedstrijd                                                             AS wedstrijd,
            'Competitie'                                                            AS bron,
            COALESCE(m.clubcode, a.clubcode)                                        AS clubcode,
            (m.wedstrijdcode)::bigint                                               AS wedstrijdcode
        FROM his.matches m
        CROSS JOIN LATERAL (
            SELECT clubcode, accommodatie
            FROM public.appsettings
            WHERE syncenabled = true
            ORDER BY clubcode
            LIMIT 1
        ) a
        LEFT JOIN his.teams t
            ON t.teamnaam = m.teamnaam AND t.leeftijdscategorie IS NOT NULL AND t.leeftijdscategorie <> ''
           AND COALESCE(t.clubcode, a.clubcode) = COALESCE(m.clubcode, a.clubcode)
        LEFT JOIN public.speeltijden s
            ON s.leeftijd = CASE
                WHEN m.teamnaam ~* ('^' || a.clubcode || ' G[0-9]') THEN 'G'
                ELSE REPLACE(REPLACE(REPLACE(t.leeftijdscategorie, 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
            END
           AND s.clubcode = a.clubcode
        WHERE m.accommodatie LIKE '%' || a.accommodatie || '%'
          AND m.status <> 'Afgelast'
          AND m.aanvangstijd IS NOT NULL
          AND s.wedstrijdtotaal IS NOT NULL

        UNION ALL

        SELECT
            gw.datum                                                                AS datum,
            gw.aanvangstijd                                                         AS aanvangstijd,
            (gw.datum + gw.eindtijd)                                                AS eindtijd,
            NULL::text                                                              AS veld_ruw,
            gw.veldnummer                                                           AS veldnummer,
            gw.velddeelgebruik                                                      AS velddeelgebruik,
            gw.leeftijdscategorie                                                   AS leeftijdscategorie,
            gw.teamnaam                                                             AS teamnaam,
            COALESCE(gw.teamnaam, '') || ' - ' || COALESCE(gw.tegenstander, '')     AS wedstrijd,
            'Planner'                                                               AS bron,
            gw.clubcode                                                             AS clubcode,
            gw.sportlinkwedstrijdcode                                               AS wedstrijdcode
        FROM planner.geplandewedstrijden gw
        WHERE gw.status <> 'Geannuleerd'
          AND gw.isvervallen = false;
        """;
}
