using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminUitgeslotenEmailFunction.cs</c> (#887).
/// Vertaling: <c>SqlException.Number == 208</c> (ontbrekend object) →
/// <c>PostgresException.SqlState == PostgresErrorCodes.UndefinedTable</c> ("42P01");
/// <c>2627/2601</c> (unique violation) → <c>PostgresErrorCodes.UniqueViolation</c> ("23505").
/// </summary>
public static class AdminUitgeslotenEmailFunction
{
    [Function("AdminUitgeslotenEmailGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/uitgesloten-emails")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminUitgeslotenEmailGet"), "uitsluitingslijst ophalen",
            async clubCode =>
            {
                try
                {
                    var list = await AdminUitgeslotenEmailRepository.GetAlleAsync(
                        clubCode, PostgresDatabaseConfig.ConnectionString);
                    return new OkObjectResult(list);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
                {
                    return new OkObjectResult(new List<object>());
                }
            });

    [Function("AdminUitgeslotenEmailPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/uitgesloten-emails")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminUitgeslotenEmailPost"), "uitsluitingsadres toevoegen",
            async clubCode =>
            {
                var body = await new System.IO.StreamReader(req.Body).ReadToEndAsync();
                var dto  = JsonConvert.DeserializeObject<UitgeslotenEmailRequest>(body);
                if (dto == null || string.IsNullOrWhiteSpace(dto.EmailAdres))
                    return new BadRequestObjectResult(new { error = "EmailAdres verplicht" });

                var adres = dto.EmailAdres.Trim().ToLowerInvariant();
                try
                {
                    var newId = await AdminUitgeslotenEmailRepository.InsertAsync(
                        adres, dto.Omschrijving, dto.Actief, clubCode, PostgresDatabaseConfig.ConnectionString);
                    return new OkObjectResult(new { id = newId });
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    return new ConflictObjectResult(new { error = "Dit e-mailadres staat al in de lijst" });
                }
            });

    [Function("AdminUitgeslotenEmailDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/uitgesloten-emails/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminUitgeslotenEmailDelete"), "uitsluitingsadres verwijderen",
            async clubCode =>
            {
                var rows = await AdminUitgeslotenEmailRepository.DeleteAsync(
                    id, clubCode, PostgresDatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = "Niet gevonden" });
                return new OkObjectResult(new { deleted = true });
            });

    private class UitgeslotenEmailRequest
    {
        public string? EmailAdres    { get; set; }
        public string? Omschrijving  { get; set; }
        public bool    Actief        { get; set; } = true;
    }
}
