using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Planner.Endpoints.Sportlink;

/// <summary>
/// Gedeelde orkestratie voor <c>PUT /api/sportlink/audit/{id}/notitie</c> (#1320) — dezelfde
/// tier-dunne-omhulsel-aanpak als <see cref="SportlinkEndpointSupportCore"/>: validatie en de
/// vertaling naar <see cref="IActionResult"/> staan hier één keer; wat per tier verschilt is
/// uitsluitend <c>ISportlinkMutationAuditService.ZetNotitieAsync</c> zelf (twee kopieën, zie de
/// doc-comment op <see cref="SportlinkEndpointSupportCore"/> voor waarom die interface nog niet
/// is samengevoegd — #1271).
/// </summary>
public static class SportlinkAuditEndpointCore
{
    public static async Task<IActionResult> VerwerkNotitieAsync(
        long auditId, string clubCode, string? notitie, Func<long, string, string, Task<bool>> zetNotitieAsync)
    {
        if (notitie == null)
            return new BadRequestObjectResult(new { error = "Notitie is verplicht (lege string toegestaan om te wissen)." });
        if (notitie.Length > 1000)
            return new BadRequestObjectResult(new { error = "Notitie mag maximaal 1000 tekens zijn." });

        var bijgewerkt = await zetNotitieAsync(auditId, clubCode, notitie);
        return bijgewerkt
            ? new OkObjectResult(new { success = true })
            : new NotFoundObjectResult(new { error = $"Audit-record {auditId} niet gevonden voor deze club." });
    }
}

/// <summary>Body-DTO voor het notitie-endpoint — gedeeld zodat geen van beide tiers een eigen kopie nodig heeft.</summary>
public sealed class SportlinkAuditNotitieDto
{
    public string? Notitie { get; set; }
}
