using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Email.Trace;

namespace Planner.Endpoints.Admin;

/// <summary>
/// Tier-onafhankelijke aansluiting van de e-maillog-endpoints (#1568): lezen van de queryparameters en
/// de vertaling van een opgeslagen trace naar HTTP. Alleen de databasevraag blijft per tier.
/// </summary>
public static class EmailLogEndpointCore
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <param name="Vanaf">Begin (UTC, inclusief) of <c>null</c>.</param>
    /// <param name="Tot">Eind (UTC, exclusief; de opgegeven dag + 1) of <c>null</c>.</param>
    public sealed record Filter(DateTime? Vanaf, DateTime? Tot, string Status, int Limit);

    /// <summary>
    /// Leest <c>vanaf</c>, <c>tot</c>, <c>status</c> en <c>limit</c>. Datums krijgen Kind=Utc: Npgsql weigert
    /// een Unspecified <c>DateTime</c> voor een <c>TIMESTAMPTZ</c>-kolom; SQL Server's <c>DATETIME2</c> kent geen Kind.
    /// </summary>
    public static Filter LeesFilter(IQueryCollection query)
    {
        DateTime? vanaf = null, tot = null;
        if (DateTime.TryParse(query["vanaf"].ToString(), out var vd)) vanaf = DateTime.SpecifyKind(vd.Date, DateTimeKind.Utc);
        if (DateTime.TryParse(query["tot"].ToString(), out var td)) tot = DateTime.SpecifyKind(td.Date.AddDays(1), DateTimeKind.Utc);
        var limit = int.TryParse(query["limit"].ToString(), out var l) ? Math.Min(MaxLimit, Math.Max(1, l)) : DefaultLimit;
        return new Filter(vanaf, tot, query["status"].ToString(), limit);
    }

    /// <summary>404 als er geen trace is (nooit gemaakt), anders 200 met de PII-arme trace.</summary>
    public static IActionResult VertaalTrace(EmailTraceAntwoord? trace)
        => trace is null
            ? new NotFoundObjectResult(new { error = "Geen trace gevonden voor deze verwerking" })
            : new OkObjectResult(trace);
}
