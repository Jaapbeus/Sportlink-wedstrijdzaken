using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Planner.Shared;

namespace Planner.Endpoints.AutoPlan;

/// <summary>Shared HTTP mapping for the exact-plan apply path on both database tiers.</summary>
public static class AutoPlanToepassenEndpointCore
{
    public static async Task<IActionResult> VerwerkAsync<TResponse>(
        Func<Task<TResponse>> toepassen,
        ILogger log,
        bool invalidOperationIsBadRequest = false)
    {
        try
        {
            return new OkObjectResult(await toepassen());
        }
        catch (AutoPlanApplyConflictException ex)
        {
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 409 };
        }
        catch (AutoPlanApplyForbiddenException ex)
        {
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 403 };
        }
        catch (InvalidOperationException ex) when (invalidOperationIsBadRequest)
        {
            log.LogWarning("AutoPlanToepassen aanvraag ongeldig: {Reden}", ex.Message);
            return new BadRequestObjectResult(new { error = ex.Message });
        }
    }
}
