using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Endpoints.Sportlink;

/// <summary>
/// <c>GET /api/sportlink/koppeling-status</c> (#1773): de koppelingsstatus van rol Wedstrijdzaken
/// voor de vooraf-melding op de werkpagina's. Wordt achter de Wedstrijdzaken-poort aangeroepen
/// (de health-endpoint is admin-only). Leest alleen de eigen opslag, doet geen Sportlink-aanroep en
/// geeft nooit een tokenwaarde, credential of foutdetail van de provider terug.
/// </summary>
public static class SportlinkKoppelingStatusEndpointCore
{
    public static async Task<IActionResult> ExecuteAsync(HttpRequest request, string clubCode,
        ISportlinkAutoLoginStore? store, TimeProvider? timeProvider = null)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        var rol = SportlinkEndpointCore.RolWedstrijdzaken;
        var nu = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var ct = request.HttpContext.RequestAborted;

        SportlinkAutoLoginState? state = null;
        var refreshTokenAanwezig = false;
        // Alleen de primaire club heeft een opslag; voor elke andere club is "niet gekoppeld" de waarheid.
        if (store is not null && string.Equals(clubCode, store.ClubCode, StringComparison.Ordinal))
        {
            try
            {
                state = await store.ReadAsync(rol, ct);
                refreshTokenAanwezig = !string.IsNullOrWhiteSpace(store.LeesRefreshToken(rol));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Geen exceptionobject naar buiten: een provider/parser kan credentials in details bevatten.
                return new ObjectResult(new { error = "Beveiligde loginopslag niet beschikbaar; controleer de hostconfiguratie." }) { StatusCode = 503 };
            }
        }

        var status = SportlinkKoppelingStatusCore.Bepaal(rol, state, refreshTokenAanwezig, nu);
        return new OkObjectResult(new
        {
            status.RolNaam,
            Niveau = status.Niveau.ToString(),
            status.IsVerbroken,
            status.Melding,
            status.LaatsteLoginUtc,
        });
    }
}
