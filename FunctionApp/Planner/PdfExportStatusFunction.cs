using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Planner.Endpoints.Deel;
using SportlinkFunction.Admin;

namespace SportlinkFunction.Planner;

/// <summary>
/// #1459: <c>GET planner/pdf-export</c> — of PDF-export voor de gekozen club aan staat. Open voor elke
/// ingelogde rol (net als <c>planner/veldbezetting</c>), zodat Planning de PDF-knop ook voor de rol
/// <c>user</c> juist toont; <c>beheer/settings</c> is admin-only. Respons in
/// <see cref="PlannerDeelEndpointCore.PdfExportStatusAsync"/>, per tier alleen de lees-delegate.
/// </summary>
public static class PdfExportStatusFunction
{
    [Function("PdfExportStatus")]
    public static Task<IActionResult> PdfExportStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "planner/pdf-export")] HttpRequest req,
        FunctionContext context)
        => AdminEndpoint.ExecuteAuthenticatedAsync(req, context.GetLogger("PdfExportStatus"), "pdf-export-status ophalen",
            clubCode => PlannerDeelEndpointCore.PdfExportStatusAsync(() => PdfExportInstelling.IsIngeschakeldAsync(clubCode)));
}
