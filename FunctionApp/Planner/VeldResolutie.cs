namespace SportlinkFunction.Planner;

/// <summary>
/// SQL-tegenhanger van <see cref="Services.PlannerShared.ResolveVeld"/> — Sportlink-veldstring naar
/// het veldnummer uit <c>dbo.Velden</c> (#719).
///
/// <para>
/// <b>Waarom dit bestaat.</b> Sportlink levert het veld als <c>"&lt;veldnaam&gt;[ &lt;subpositie&gt;]"</c>
/// ("veld 1 A"), terwijl <c>dbo.Velden</c> alleen de veldnaam bevat. In C# gaat die vertaling sinds #707
/// via één functie; op het database-fallbackpad stond nog <c>RTRIM(LEFT(m.[veld], 6))</c>. Die afkap op
/// zes tekens vereist dat élke veldnaam maximaal zes tekens is én in de eerste zes uniek, en dat is
/// twee keer niet waar:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>"veld 10"</c> wordt afgekapt tot <c>"veld 1"</c>, dus een wedstrijd van een ánder team op
///     veld 10 komt als bezetting op veld 1 te staan. Veld 10 lijkt daarmee de hele dag vrij en er kan
///     een tweede wedstrijd op hetzelfde veld en tijdstip worden ingepland — een dubbele boeking.
///   </description></item>
///   <item><description>
///     Een veldnaam langer dan zes tekens (<c>"hoofdveld"</c>) matcht nooit en valt volledig uit de
///     bezetting weg.
///   </description></item>
/// </list>
///
/// <para>
/// Het real-time pad (<c>SportlinkApiClient</c>) is bij #707 al omgezet en sluit de eigen wedstrijd uit
/// op wedstrijdcode, wat immuun is voor elk veldnaam-probleem. Het fallbackpad — <c>useRealtimeApi = 0</c>
/// of een falende Sportlink-API — gebruikte nog de truncatie.
/// </para>
///
/// <para>
/// <b>Let op bij wijzigen.</b> Dezelfde matching staat op drie plekken en die moeten gelijk blijven:
/// deze klasse, <c>Database/planner/Views/AlleWedstrijdenOpVeld.sql</c> en de kopie van die view in
/// <c>Database/Script.PostDeployment1.sql</c>. CI rolt alleen dat laatste script uit, dus een wijziging
/// die alleen in het DB-project landt verdwijnt geruisloos. <c>VeldResolutieDriftTests</c> bewaakt dit.
/// </para>
/// <para>
/// <b>Postgres-tier (#819, #864):</b> daar is bewust GEEN vierde SQL-kopie van deze truncatie
/// bijgekomen — <c>Database.Postgres/PostgresPlannerViewGenerator.cs</c> levert de ruwe,
/// ongeresolveerde veldstring terug en laat de resolutie volledig aan
/// <c>Planner.Shared.VeldResolver</c> (tier-agnostisch, gedeeld tussen beide tiers). Dat bestand
/// staat wél in <c>VeldResolutieDriftTests</c> als vierde plek: geen kopie van de truncatie-bug
/// vandaag, maar een tripwire mocht iemand de resolutie ooit weer SQL-side willen doen.
/// </para>
/// </summary>
internal static class VeldResolutie
{
    /// <summary>
    /// Genormaliseerde veldstring: getrimd en dubbele spaties samengevouwen. Gelijk aan
    /// <c>AutoPlanService.NormaliseerVeld</c>; lowercasen is niet nodig omdat de databasecollatie
    /// case-insensitive is (<c>Latin1_General_CI_AS</c>) en <c>=</c> dus al ongevoelig is voor kast.
    /// </summary>
    internal static string SqlNormaliseer(string kolom)
        => $"LTRIM(RTRIM(REPLACE(ISNULL({kolom}, ''), '  ', ' ')))";

    /// <summary>
    /// <c>OUTER APPLY</c> die het veldnummer en de subpositie oplevert bij een Sportlink-veldstring.
    /// Levert de kolommen <c>VeldNummer</c> en <c>Subpositie</c> onder het opgegeven alias.
    ///
    /// <para>
    /// Een treffer is een exact gelijke veldnaam, óf een veldnaam gevolgd door een spatie en de
    /// subpositie — nooit een langer veldnummer, zodat <c>"veld 10"</c> niet op <c>"veld 1"</c> valt.
    /// Langste veldnaam eerst: bestaat naast "veld 1" ook "veld 1 achter", dan hoort "veld 1 achter B"
    /// bij dat tweede veld en is "achter" geen subpositie van veld 1. Identiek aan
    /// <see cref="Services.PlannerShared.ResolveVeld"/>.
    /// </para>
    ///
    /// <para>
    /// Bewust géén <c>LIKE naam + ' %'</c>: een veldnaam met <c>%</c>, <c>_</c> of <c>[</c> erin zou dan
    /// als patroon worden gelezen en verkeerde velden matchen.
    /// </para>
    /// </summary>
    /// <param name="veldKolom">Kolom met de Sportlink-veldstring, bijv. <c>"m.[veld]"</c>.</param>
    /// <param name="clubCodeExpr">Expressie voor de ClubCode-scope, bijv. <c>"a.[ClubCode]"</c> of een parameternaam.</param>
    /// <param name="alias">Alias waaronder het resultaat beschikbaar komt.</param>
    internal static string SqlOuterApply(string veldKolom, string clubCodeExpr, string alias = "v")
    {
        var gezocht = SqlNormaliseer(veldKolom);
        const string naam = "vn.[Naam]";

        return $@"
    OUTER APPLY (
        SELECT TOP 1
            vv.[VeldNummer],
            vv.[VeldNaam],
            NULLIF(LTRIM(SUBSTRING({gezocht}, LEN({naam}) + 1, 100)), '') AS [Subpositie]
        FROM [dbo].[Velden] vv
        CROSS APPLY (SELECT {SqlNormaliseer("vv.[VeldNaam]")} AS [Naam]) vn
        WHERE vv.[ClubCode] = {clubCodeExpr}
          AND LEN({naam}) > 0
          AND (
                {gezocht} = {naam}
                OR (
                     LEN({gezocht}) > LEN({naam})
                     AND LEFT({gezocht}, LEN({naam})) = {naam}
                     AND SUBSTRING({gezocht}, LEN({naam}) + 1, 1) = ' '
                   )
              )
        ORDER BY LEN({naam}) DESC
    ) {alias}";
    }
}
