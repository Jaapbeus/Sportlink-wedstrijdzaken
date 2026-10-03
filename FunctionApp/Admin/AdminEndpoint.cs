using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// Standaard wrapper voor admin-endpoints. Garandeert:
/// - RequireAdmin guard (auth check — altijd als eerste)
/// - CorrelationId-scope voor request-tracing
/// - WaitForDatabase readiness check
/// - Uniforme 500-fallback
///
/// Gebruik: return await AdminEndpoint.ExecuteAsync(req, log, "context", async clubCode => { ... });
/// Specifieke exceptions (SqlException conflict etc.) worden BINNEN de delegate afgehandeld. (#467)
/// <para>
/// <b>Sinds #1350 de enige autorisatiepoort voor admin-endpoints op deze tier.</b> Elk
/// HTTP-endpoint met de admin-rol loopt via <see cref="ExecuteAsync"/> of
/// <see cref="ExecuteZonderDatabaseAsync"/>; de Sportlink-endpoints via
/// <c>SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync</c>, dat sinds #1400 op
/// <see cref="ExecuteWedstrijdzakenOfAdminAsync"/> uitkomt (admin ÓF Wedstrijdzaken — niet meer
/// uitsluitend admin, zie de toelichting daar). Een losse <c>EasyAuthHelper.RequireAdmin</c>-aanroep
/// in een endpoint wordt door <c>scripts/ci/check-endpoint-autorisatie.sh</c> geweigerd, en
/// <c>FunctionApp.Tests/Admin/EndpointAutorisatieTests.cs</c> bewijst per endpoint dat de poort
/// dicht zit zonder rol en open gaat mét rol.
/// </para>
/// </summary>
internal static class AdminEndpoint
{
    /// <summary>
    /// Testhaak (#1350) — uitsluitend bereikbaar via <c>InternalsVisibleTo</c>. Staat hij, dan keert
    /// elke aanroep die de autorisatiepoort passeert meteen terug met het resultaat van de haak: vóór
    /// de databasewacht en vóór het werk van het endpoint. Daarmee kan de autorisatietest per
    /// endpoint bewijzen dat een admin de poort passeert én dat het endpoint via deze wrapper loopt —
    /// zonder database en zonder neveneffect (een DELETE- of sync-endpoint mag in een test nooit
    /// echt werk doen; in de CI-job met een levende database zou dat anders wél gebeuren).
    /// De haak zit ná de poort en kan die dus nooit verzwakken; productie heeft geen pad dat hem zet.
    /// </summary>
    internal static Func<string, IActionResult>? PoortGepasseerdVoorTests;

    internal static async Task<IActionResult> ExecuteAsync(
        HttpRequest req,
        ILogger log,
        string errorContext,
        Func<string, Task<IActionResult>> work)
    {
        var (correlationId, authResult) = Poort(req);
        if (authResult != null) return authResult;
        if (PoortGepasseerdVoorTests is { } haak) return haak(errorContext);

        using var _ = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            return await work(clubCode);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Context} mislukt [correlationId={CorrelationId}]", errorContext, correlationId);
            return new ObjectResult(new { error = "Interne fout" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Zelfde poort als <see cref="ExecuteAsync"/>, zonder databasewacht en zonder clubcode — voor
    /// endpoints die geen database nodig hebben (#1350: tot #764 de feedback-endpoints; die bewaren
    /// sinds #764 elke melding en lopen daarom via <see cref="ExecuteAuthenticatedAsync"/>). Admin-only. Bewust een aparte methode en géén optionele
    /// parameter op <see cref="ExecuteAsync"/>: een vlag die stilzwijgend een stap overslaat is
    /// dezelfde valkuil als de <c>requireRole</c>-parameter die #1272 verwijderde.
    /// </summary>
    internal static async Task<IActionResult> ExecuteZonderDatabaseAsync(
        HttpRequest req,
        ILogger log,
        string errorContext,
        Func<Task<IActionResult>> work)
    {
        var (correlationId, authResult) = Poort(req);
        if (authResult != null) return authResult;
        if (PoortGepasseerdVoorTests is { } haak) return haak(errorContext);

        using var _ = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Context} mislukt [correlationId={CorrelationId}]", errorContext, correlationId);
            return new ObjectResult(new { error = "Interne fout" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Zelfde poort als <see cref="ExecuteAsync"/>, maar accepteert elke ingelogde rol
    /// (admin + user) in plaats van uitsluitend admin (#1330: Teambegeleiding-scherm bewust weer
    /// open voor alle gebruikers, teruggedraaid uit de mei-2026-beperking). Bewust een aparte,
    /// expliciet genoemde methode en géén parameter op <see cref="ExecuteAsync"/> — dat is exact de
    /// valkuil die de #1272-comment op <see cref="PoortGeauthenticeerd"/> hieronder beschrijft: een
    /// optionele rol-parameter kan stilzwijgend een striktere controle vervangen. Een tweede,
    /// met naam zichtbare poort kan dat niet: een aanroeper kiest hem altijd bewust.
    /// <see cref="check-endpoint-autorisatie.sh"/> (#1350) herkent deze naam expliciet als wrapper,
    /// en <c>EndpointAutorisatieTests.MetAlleenUserRol_MagBijAuthenticatedEndpoints</c> bewijst dat
    /// uitsluitend de hier genoemde endpoints met de rol <c>user</c> door mogen — elk ander
    /// endpoint blijft admin-only.
    /// </summary>
    internal static async Task<IActionResult> ExecuteAuthenticatedAsync(
        HttpRequest req,
        ILogger log,
        string errorContext,
        Func<string, Task<IActionResult>> work)
    {
        var (correlationId, authResult) = PoortGeauthenticeerd(req);
        if (authResult != null) return authResult;
        if (PoortGepasseerdVoorTests is { } haak) return haak(errorContext);

        using var _ = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            return await work(clubCode);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Context} mislukt [correlationId={CorrelationId}]", errorContext, correlationId);
            return new ObjectResult(new { error = "Interne fout" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Zelfde poort als <see cref="ExecuteAsync"/>, maar voor Sportlink-endpoints: accepteert de
    /// functionele rol <c>Wedstrijdzaken</c> naast <c>admin</c> in plaats van uitsluitend admin
    /// (#1400, fix van de AND-gate-bevinding uit #1379). Vóór deze wijziging accepteerde de EERSTE
    /// poort van <c>SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync</c> al Wedstrijdzaken (#1376),
    /// maar strandde zo'n gebruiker alsnog op deze TWEEDE poort, die tot nu toe altijd
    /// <see cref="EasyAuthHelper.RequireAdmin"/> aanriep. Bewust een aparte, met naam zichtbare
    /// methode — zelfde reden als bij <see cref="ExecuteAuthenticatedAsync"/> hierboven. Uitsluitend
    /// bedoeld als tweede-poort-delegate voor <c>SportlinkEndpointSupport</c>; geen los endpoint
    /// roept dit rechtstreeks aan.
    /// </summary>
    internal static async Task<IActionResult> ExecuteWedstrijdzakenOfAdminAsync(
        HttpRequest req,
        ILogger log,
        string errorContext,
        Func<string, Task<IActionResult>> work)
    {
        var (correlationId, authResult) = PoortWedstrijdzaken(req);
        if (authResult != null) return authResult;
        if (PoortGepasseerdVoorTests is { } haak) return haak(errorContext);

        using var _ = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            return await work(clubCode);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Context} mislukt [correlationId={CorrelationId}]", errorContext, correlationId);
            return new ObjectResult(new { error = "Interne fout" }) { StatusCode = 500 };
        }
    }

    // De ene plek waar de admin-poort staat. #1272: de optionele requireRole-parameter van #991 is
    // hier weg. Die verving de admin-controle in plaats van er bovenop te komen, wat §3.4 van
    // docs/SPORTLINK-WEB-EXTENSION.md tegensprak. De Sportlink-endpoints gebruiken nu
    // SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync, dat sinds #1400 ExecuteWedstrijdzakenOfAdminAsync
    // als tweede poort gebruikt — niet meer deze admin-only poort. De parameter is verwijderd en
    // niet alleen ongebruikt gelaten: een optionele parameter die stilzwijgend een autorisatiecontrole
    // vervangt, is een valkuil die vanzelf een tweede keer gebruikt wordt.
    private static (string CorrelationId, IActionResult? AuthResult) Poort(HttpRequest req)
    {
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        return (correlationId, EasyAuthHelper.RequireAdmin(req));
    }

    // De ene plek waar de "elke ingelogde rol"-poort staat (#1330) — zelfde vorm als Poort()
    // hierboven, bewust niet dezelfde methode met een parameter (zie de #1272-toelichting daar).
    // #1350 verwijderde EasyAuthHelper.RequireAuthenticated bewust volledig (niet alleen afgeraden) —
    // deze poort roept daarom rechtstreeks RequireRole aan met beide rollen, en blijft de ENIGE
    // plek in de hele codebase die dat doet. AdminEndpoint.cs zelf bevat geen [Function(...)] en
    // valt dus buiten het bereik van scripts/ci/check-endpoint-autorisatie.sh (zie de klasse-doc
    // hierboven) — deze aanroep omzeilt die guard dus niet, hij staat er gewoon los van.
    private static (string CorrelationId, IActionResult? AuthResult) PoortGeauthenticeerd(HttpRequest req)
    {
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        return (correlationId, EasyAuthHelper.RequireRole(req, "admin", "user"));
    }

    // De poort voor Sportlink-endpoints (#1400) — zelfde vorm als Poort()/PoortGeauthenticeerd()
    // hierboven, bewust een eigen methode i.p.v. een parameter (zie de #1272-toelichting bij Poort()).
    private static (string CorrelationId, IActionResult? AuthResult) PoortWedstrijdzaken(HttpRequest req)
    {
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        return (correlationId, EasyAuthHelper.RequireWedstrijdzaken(req));
    }
}
