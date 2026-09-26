using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Endpoints.Sportlink;

/// <summary>
/// De endpoint-orkestratie die elk Sportlink Web Extension-endpoint en elke Sportlink-timer op
/// beide tiers deelde als woordelijke kopie (#1122, #1266) — routeparameter/DI-plumbing en de
/// vertaling naar <see cref="IActionResult"/>, niet de beslislogica zelf (die stond al gedeeld in
/// <see cref="SportlinkEndpointCore"/>, Planner.Shared).
/// <para>
/// Gemeten bij #1271: 775 van de 907 regels van de negen bestanden in <c>Sportlink/</c> waren
/// woordelijk identiek tussen de tiers, en de twee bestanden met de minste overlap waren precies
/// de twee waar de databasetoegang zelf zit — de databasevraag is dus een kleine minderheid van
/// de code, en de rest hoort hier.
/// </para>
/// <para>
/// <b>Waarom een apart project en niet <c>Planner.Shared</c>?</b> Deze klasse gebruikt
/// <see cref="HttpRequest"/>, <see cref="IActionResult"/> en <see cref="FunctionContext"/> —
/// ASP.NET Core en de Azure Functions Worker. Die afhankelijkheid blijft bewust buiten
/// <c>Planner.Shared</c>, zelfde grens als <c>ThemeCore</c> (#1248) en <c>FeedbackCore</c> (#1130).
/// </para>
/// <para>
/// <b>Waarom geen <c>ISportlinkMutationAuditService</c> hier?</b> Die interface bestaat vandaag
/// als twee identieke kopieën, één per tier-namespace (<c>FunctionApp.Postgres.Sportlink</c> en
/// <c>SportlinkFunction.Sportlink</c>), met eigen implementaties en DI-registraties. Die
/// samenvoegen raakt <c>Program.cs</c> van beide tiers en is een aparte afweging — zie #1271. In
/// plaats daarvan neemt <see cref="RondMutatieAfAsync{T}"/> de audit-afronding als delegate, zodat
/// deze klasse zelf geen weet heeft van welk auditcontract een tier gebruikt.
/// </para>
/// </summary>
public static class SportlinkEndpointSupportCore
{
    /// <summary>De ene functionele rol waarmee deze app in Sportlink Club schrijft (#988).</summary>
    public const string RolWedstrijdzaken = SportlinkEndpointCore.RolWedstrijdzaken;

    /// <summary>Vertaalt een gedeelde foutuitkomst naar de HTTP-respons.</summary>
    public static IActionResult NaarActionResult(SportlinkEndpointFout fout)
        => new ObjectResult(new { error = fout.Foutmelding }) { StatusCode = fout.HttpStatus };

    /// <summary>Toggle (#988) + EgressGuard (#857): <c>null</c> als de aanroep door mag, anders de
    /// 409/503-fout die de client toont.</summary>
    public static IActionResult? ControleerToggleEnEgress(
        Func<string, string?> leesInstelling, Func<bool> egressToegestaan)
    {
        var fout = SportlinkEndpointCore.ControleerToggleEnEgress(leesInstelling, egressToegestaan);
        return fout == null ? null : NaarActionResult(fout);
    }

    /// <summary>Dry-run-stand van deze club (#998) — fail-safe, zie
    /// <see cref="SportlinkEndpointCore.IsDryRunActief"/>.</summary>
    public static bool IsDryRunActief(Func<string, string?> leesInstelling)
        => SportlinkEndpointCore.IsDryRunActief(leesInstelling);

    /// <summary>
    /// Voert een Sportlink-endpoint uit met BEIDE poorten: eerst de functionele rol
    /// <c>Wedstrijdzaken</c> (<paramref name="requireWedstrijdzaken"/>), daarna de gewone
    /// admin-controle van de tier (<paramref name="adminExecuteAsync"/>). Sinds #1272 gebruiken
    /// beide tiers dezelfde volgorde — geen tierverschil meer, alleen nog een andere
    /// <c>EasyAuthHelper</c>/<c>AdminEndpoint</c> per tier.
    /// </summary>
    public static Task<IActionResult> ExecuteWedstrijdzakenAsync(
        HttpRequest req,
        ILogger log,
        string errorContext,
        Func<string, Task<IActionResult>> work,
        Func<HttpRequest, IActionResult?> requireWedstrijdzaken,
        Func<HttpRequest, ILogger, string, Func<string, Task<IActionResult>>, Task<IActionResult>> adminExecuteAsync)
    {
        var rolFout = requireWedstrijdzaken(req);
        return rolFout != null
            ? Task.FromResult(rolFout)
            : adminExecuteAsync(req, log, errorContext, work);
    }

    /// <summary>De 503 voor "client niet in DI geregistreerd", als losse respons — voor de paden die
    /// de client zelf uit <see cref="FunctionContext.InstanceServices"/> halen in plaats van via
    /// <see cref="ClientOfFout"/>.</summary>
    public static IActionResult ClientNietGeconfigureerdFout()
        => NaarActionResult(SportlinkEndpointCore.ClientNietGeconfigureerdFout);

    /// <summary>De <see cref="ISportlinkClubClient"/> uit DI, of een 503 als hij niet geregistreerd
    /// is (Program.cs registreert hem alleen als de EgressGuard het toestaat).</summary>
    public static (ISportlinkClubClient? Client, IActionResult? Fout) ClientOfFout(FunctionContext context)
    {
        var client = context.InstanceServices.GetService<ISportlinkClubClient>();
        return client == null
            ? (null, NaarActionResult(SportlinkEndpointCore.ClientNietGeconfigureerdFout))
            : (client, null);
    }

    /// <summary>Timer-variant van de drie controles hierboven: logt waarom er niets gebeurt en geeft
    /// <c>null</c> terug, zodat elke timer met één regel kan afbreken.</summary>
    public static ISportlinkClubClient? ClientVoorTimer(
        FunctionContext context, ILogger log, string taak,
        Func<string, string?> leesInstelling, Func<bool> egressToegestaan)
    {
        switch (SportlinkEndpointCore.BepaalTimerStatus(leesInstelling, egressToegestaan))
        {
            case SportlinkTimerStatus.ExtensieUit:
                log.LogInformation(SportlinkEndpointCore.TimerExtensieUitLog, taak);
                return null;
            case SportlinkTimerStatus.EgressGeblokkeerd:
                log.LogInformation(SportlinkEndpointCore.TimerEgressGeblokkeerdLog, taak);
                return null;
        }

        var client = context.InstanceServices.GetService<ISportlinkClubClient>();
        if (client == null)
            log.LogWarning(SportlinkEndpointCore.TimerClientOntbreektLog, taak);
        return client;
    }

    /// <summary>Vertaalt <see cref="SportlinkClubCallStatus"/> naar een HTTP-foutrespons — nooit de
    /// onderliggende Sportlink-foutdetails 1-op-1 doorzetten (CISO-regel). <c>null</c> bij <c>Ok</c>.</summary>
    public static IActionResult? VertaalStatusNaarFout(SportlinkClubCallStatus status)
    {
        var fout = SportlinkEndpointCore.VertaalStatusNaarFout(status);
        return fout == null ? null : NaarActionResult(fout);
    }

    /// <summary>Request-body als DTO; <c>null</c> bij een lege body.</summary>
    public static async Task<T?> LeesBodyAsync<T>(HttpRequest req) where T : class
        => JsonConvert.DeserializeObject<T>(await new StreamReader(req.Body).ReadToEndAsync());

    /// <summary>Audit-<c>resultaat</c> voor een mutatie-uitkomst (#998, uitgebreid #994).</summary>
    public static string BepaalAuditResultaat(SportlinkMutationResult r)
        => SportlinkEndpointCore.BepaalAuditResultaat(r);

    /// <summary>
    /// De afronding die élke mutatie deelt: transportfout → audit "Failure" + vertaalde fout; lege
    /// respons → audit "Failure" + 502; anders audit met <see cref="BepaalAuditResultaat"/> en
    /// <paramref name="ok"/>. Altijd HTTP 200 bij een inhoudelijke afwijzing door Sportlink —
    /// <c>IsSuccess</c>/<c>Violations</c> dragen de uitkomst (consistent met AdminApiClient).
    /// <paramref name="voltooiAuditAsync"/> is de tier-eigen audit-afronding (resultaat,
    /// samenvatting) → Task; <c>null</c> als er geen audit-record is om af te ronden.
    /// </summary>
    public static async Task<IActionResult> RondMutatieAfAsync<T>(
        SportlinkClubResponse<T> mutationResult,
        Func<string, string?, Task>? voltooiAuditAsync,
        Func<T, SportlinkMutationResult> naarMutatieResultaat,
        Func<T, IActionResult> ok)
        where T : class
    {
        var afronding = SportlinkEndpointCore.BepaalMutatieAfronding(mutationResult, naarMutatieResultaat);
        if (voltooiAuditAsync != null)
            await voltooiAuditAsync(afronding.AuditResultaat, afronding.AuditSamenvatting);
        return afronding.Fout != null ? NaarActionResult(afronding.Fout) : ok(afronding.Data!);
    }
}
