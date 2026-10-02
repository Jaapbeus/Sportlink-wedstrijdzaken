using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Endpoints.Sportlink;

/// <summary>
/// Tier-onafhankelijke kern van <c>DELETE /api/sportlink/club-match/{publicMatchId}</c> (#1440): een
/// clubwedstrijd (oefenwedstrijd) verwijderen. Volgorde, gelijk aan de andere mutaties op een
/// bestaande wedstrijd: invoer controleren → toggle + EgressGuard → huidige wedstrijd ophalen →
/// <see cref="SportlinkMutationGuard"/> (<see cref="SportlinkMutationSoort.Verwijderen"/>) → audit
/// "Pending" → verwijderen → audit voltooien.
/// <para>
/// <b>Contract niet live bevestigd.</b> Methode, parameter en foutvorm komen uit Sportlinks publieke
/// frontend-bundle; de aanroep zelf staat hard op dry-run via
/// <c>SportlinkClubClient.ClubMatchDeleteLiveBevestigd</c>. Deze kern verandert daar niets aan.
/// </para>
/// <para>
/// De tierbestanden houden alleen de route-registratie en het tier-eigen auditcontract
/// (<paramref name="logPogingAsync"/> in <see cref="VerwijderAsync"/>), zelfde reden als bij
/// <see cref="SportlinkEndpointSupportCore.RondMutatieAfAsync{T}"/>.
/// </para>
/// </summary>
public static class ClubMatchVerwijderCore
{
    /// <summary>Waarde van <c>Actie</c> in de mutatie-audit.</summary>
    public const string AuditActie = "DeleteClubMatch";

    private static readonly Regex PublicMatchIdVorm = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Respons van het verwijder-endpoint. Spiegelt <c>BlazorAdmin.Models.OefenwedstrijdVerwijderResultaatDto</c>.</summary>
    public sealed record ClubMatchVerwijderResultaat(
        bool IsSuccess, IReadOnlyList<string>? Violations, bool IsDryRun, bool IsForcedDryRun, string PublicMatchId);

    /// <summary><c>true</c> als dit de vorm van een Sportlink-<c>PublicMatchId</c> heeft (letters, cijfers, '-' en '_').</summary>
    public static bool IsGeldigPublicMatchId(string? publicMatchId)
        => publicMatchId != null && PublicMatchIdVorm.IsMatch(publicMatchId);

    /// <summary>
    /// Het hele verwijder-endpoint achter de tier-poort.
    /// </summary>
    /// <param name="publicMatchId">Uit de route.</param>
    /// <param name="controleerToggleEnEgress">Tier-eigen toggle- en EgressGuard-controle.</param>
    /// <param name="clientOfFout">Tier-eigen client uit DI (of de 503).</param>
    /// <param name="rolNaam">De functionele rol (Wedstrijdzaken).</param>
    /// <param name="audit">Tier-eigen auditcontract; <c>null</c> als er geen auditservice is.</param>
    public static async Task<IActionResult> VerwijderAsync(
        string? publicMatchId,
        Func<IActionResult?> controleerToggleEnEgress,
        Func<(ISportlinkClubClient? Client, IActionResult? Fout)> clientOfFout,
        string rolNaam,
        VerwijderAudit? audit)
    {
        if (!IsGeldigPublicMatchId(publicMatchId))
            return new BadRequestObjectResult(new { error = "Ongeldig PublicMatchId." });

        var toggleFout = controleerToggleEnEgress();
        if (toggleFout != null) return toggleFout;
        var (client, clientFout) = clientOfFout();
        if (clientFout != null) return clientFout;

        var match = await client!.GetMatchAsync(rolNaam, publicMatchId!);
        var matchFout = SportlinkEndpointSupportCore.VertaalStatusNaarFout(match.Status);
        if (matchFout != null) return matchFout;
        if (match.Data == null)
            return new NotFoundObjectResult(new { error = "Sportlink kent dit PublicMatchId niet (meer)." });

        var guard = SportlinkMutationGuard.MagMuteren(match.Data, SportlinkMutationSoort.Verwijderen);
        var voltooiAudit = await LogPogingAsync(audit, rolNaam, publicMatchId!, match.Data);
        if (!guard.IsToegstaan)
        {
            if (voltooiAudit != null) await voltooiAudit("Geblokkeerd", guard.Reden);
            return new ObjectResult(new { error = guard.Reden }) { StatusCode = 409 };
        }

        var resultaat = await client.DeleteClubMatchAsync(rolNaam, publicMatchId!);
        return await SportlinkEndpointSupportCore.RondMutatieAfAsync(
            resultaat, voltooiAudit, r => r,
            r => new OkObjectResult(new ClubMatchVerwijderResultaat(r.IsSuccess, r.Violations, r.IsDryRun, r.IsForcedDryRun, publicMatchId!)));
    }

    /// <summary>
    /// Wat de tier aanlevert voor de mutatie-audit: clubcode, actor en de twee methodes van het
    /// tier-eigen <c>ISportlinkMutationAuditService</c> (die interface bestaat per tier, zie
    /// <see cref="SportlinkEndpointSupportCore"/>).
    /// </summary>
    public sealed record VerwijderAudit(
        string ClubCode, string TriggerdDoor,
        Func<SportlinkMutationAuditEntry, Task<long>> LogPoging,
        Func<long, string, string?, Task> Voltooi);

    /// <summary>Schrijft de Pending-rij en geeft de afronding (resultaat, samenvatting) terug.</summary>
    private static async Task<Func<string, string?, Task>?> LogPogingAsync(
        VerwijderAudit? audit, string rolNaam, string publicMatchId, SportlinkMatch match)
    {
        if (audit == null) return null;
        var auditId = await audit.LogPoging(new SportlinkMutationAuditEntry(
            audit.ClubCode, rolNaam, audit.TriggerdDoor, publicMatchId, AuditActie,
            WaardeVoor: BouwWaardeVoor(match), WaardeNa: null, CorrelationId: null));
        return (resultaat, samenvatting) => audit.Voltooi(auditId, resultaat, samenvatting);
    }

    /// <summary>
    /// Snapshot voor de audit (<c>WaardeVoor</c>): alleen niet-persoonsgebonden velden — wedstrijd-
    /// nummer, datum, status, de vlaggen waarop de guard beslist en de accommodatie. Geen teamnamen
    /// of officials: een verwijderde wedstrijd is daarna niet meer in Sportlink na te kijken, en dit
    /// is genoeg om te zien wélke wedstrijd het was.
    /// </summary>
    public static string BouwWaardeVoor(SportlinkMatch match) => JsonConvert.SerializeObject(new
    {
        match.ExternalMatchId,
        match.MatchDate,
        match.MatchStatus,
        match.IsKernelMatch,
        match.IsHomeMatch,
        match.IsCanceledMatch,
        match.IsConceptMatch,
        FacilityId = match.MatchField?.FacilityId,
        FacilityName = match.MatchField?.Name
    });
}
