using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Planner.Shared.Planning;

namespace Planner.Endpoints.Planning;

/// <summary>Dezelfde veilige foutvertaling voor beide database-tiers.</summary>
public static class PlanningEndpointError
{
    public static IActionResult Bepaal(Exception error, ILogger log, string context, string correlationId)
    {
        if (error is PlanningDuurOnbekendException)
            return new ObjectResult(new { error = PlanningDuurOnbekendException.PubliekeMelding }) { StatusCode = 409 };

        log.LogError(error, "{Context} mislukt [correlationId={CorrelationId}]", context, correlationId);
        return new ObjectResult(new { error = "Interne fout" }) { StatusCode = 500 };
    }
}
