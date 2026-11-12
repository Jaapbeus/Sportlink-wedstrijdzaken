using Microsoft.Extensions.Logging;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Sync;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-omhulsel van de gedeelde reply-afhandeling (#972, #1760). De beslislogica staat in
/// <see cref="EmailReplyAfhandeling"/> (<c>Planner.Shared</c>); hier blijft alleen wat per tier verschilt:
/// de opslag (<see cref="SqlReplyPersistentie"/> op de Postgres-connectiestring), de begeleiding-BCC en
/// KNVB-kalenderbijlage (#561/#1141: <see cref="AllstarsTestDataRepository.GetTeamleiderContactAsync"/>,
/// <see cref="KnvbPdfService"/>; fail-safe) en het uitlezen van de Graph-statuscode (#1133).
/// </summary>
internal sealed class EmailReplyPolicyService
{
    /// <summary>De opslag van de reply-afhandeling; standaard de Postgres-repository, een test geeft een fake mee (geen database nodig).</summary>
    private readonly Func<string, IReplyPersistentie> _persistentie;
    private readonly Func<int, TeamContactDoorstuurVerzoek, Task<TeamContactDoorstuurUitkomst>>? _teamContactDoorstuur;

    internal EmailReplyPolicyService(
        Func<string, IReplyPersistentie>? persistentie = null,
        Func<int, TeamContactDoorstuurVerzoek, Task<TeamContactDoorstuurUitkomst>>? teamContactDoorstuur = null)
    {
        _persistentie = persistentie ?? (cs => new SqlReplyPersistentie(cs));
        _teamContactDoorstuur = teamContactDoorstuur;
    }

    internal Task<ReplyVerwerkingUitkomst> HandelReplyFlowAfAsync(
        string connectionString,
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        bool reviewMode,
        string? reviewRecipient,
        IEmailGraphService graphService,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ILogger log,
        TraceBuilder? trace = null,
        bool zekerheidspoortActief = true,
        string? clubCode = null)
    {
        var haken = new ReplyTierHaken(
            async team => (await AllstarsTestDataRepository.GetTeamleiderContactAsync(connectionString, team))?.Emailadres,
            async regio =>
            {
                var seizoen = await PostgresSeasonHelper.GetCurrentKnvbSeizoenAsync(log);
                return string.IsNullOrWhiteSpace(seizoen) ? null : await KnvbPdfService.GetKalenderPdfAsync(regio, seizoen, log);
            },
            ExtraheerGraphStatusCode,
            _teamContactDoorstuur ?? TeamContactDoorstuurAfhandeling.MaakStandaardDoorstuur(
                () => new PostgresTeamContactDoorstuurPersistentie(connectionString, clubCode ?? VereisClubCode()),
                async team => (await AllstarsTestDataRepository.GetTeamleiderContactAsync(
                    connectionString, team, clubCode ?? VereisClubCode()))?.Emailadres,
                () => PostgresAppSettings.GetSetting("plannerEmailAdres"),
                graphService.StuurTeamContactDoorAsync,
                log));

        return EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId, new ReplyBericht(email.MessageId, email.Afzender, email.ConversationId, email.AfzenderNaam, email.Body ?? ""), classificatie,
            plannerResponseJson, reviewMode, reviewRecipient, graphService, _persistentie(connectionString),
            bouwTemplateAntwoordAsync, sanitizeFoutMelding, haken, log, trace, zekerheidspoortActief);
    }

    /// <summary>
    /// Haalt de HTTP-statuscode uit een Graph-foutrespons (#1133), of <c>null</c> als de exception
    /// geen respons van Graph vertegenwoordigt — bijv. een time-out of verbindingsfout vóórdat er
    /// ooit een respons was.
    /// </summary>
    private static int? ExtraheerGraphStatusCode(Exception ex)
        => ex is Microsoft.Graph.Models.ODataErrors.ODataError odataError && odataError.ResponseStatusCode > 0
            ? odataError.ResponseStatusCode
            : null;

    private static string VereisClubCode()
    {
        var clubCode = PostgresAppSettings.GetSetting("clubCode");
        return !string.IsNullOrWhiteSpace(clubCode)
            ? clubCode
            : throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt");
    }
}
