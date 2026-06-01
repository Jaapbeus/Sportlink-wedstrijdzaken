using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor UitgeslotenEmailAdressen — expliciet uitgesloten afzenders. v2.
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
                        clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                    return new OkObjectResult(list);
                }
                catch (SqlException ex) when (ex.Number == 208)
                {
                    // Tabel bestaat nog niet — post-deployment script nog niet uitgevoerd
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
                        adres, dto.Omschrijving, dto.Actief, clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                    return new OkObjectResult(new { id = newId });
                }
                catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
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
                    id, clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
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
