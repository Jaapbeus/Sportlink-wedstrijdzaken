using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor dbo.Speeltijden (#291).
/// WedstrijdTotaal = speeltijd + rust — de GUI toont dit expliciet.
/// </summary>
public static class AdminSpeeltijdenFunction
{
    [Function("AdminSpeeltijdenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/speeltijden")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminSpeeltijdenGet"), "speeltijden ophalen",
            async clubCode =>
            {
                var list = await AdminSpeeltijdenRepository.GetAlleAsync(
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(list);
            });

    [Function("AdminSpeeltijdenPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/speeltijden")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminSpeeltijdenPost"), "speeltijd toevoegen",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<SpeeltijdDto>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                if (dto == null || string.IsNullOrWhiteSpace(dto.Leeftijd))
                    return new BadRequestObjectResult(new { error = "Leeftijd is vereist" });
                if (dto.WedstrijdTotaal <= 0)
                    return new BadRequestObjectResult(new { error = "WedstrijdTotaal moet groter zijn dan 0" });

                try
                {
                    await AdminSpeeltijdenRepository.InsertAsync(
                        new SpeeltijdInput(dto.Leeftijd, dto.Veldafmeting, dto.WedstrijdTotaal, dto.WedstrijdHelft, dto.WedstrijdRust),
                        clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                    return new CreatedResult("/api/beheer/speeltijden", new { Leeftijd = dto.Leeftijd });
                }
                catch (SqlException ex) when (ex.Number == 2627)
                {
                    return new ConflictObjectResult(new { error = "Leeftijdscategorie bestaat al" });
                }
            });

    [Function("AdminSpeeltijdenPut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/speeltijden/{leeftijd}")] HttpRequest req,
        string leeftijd,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminSpeeltijdenPut"), "speeltijd bijwerken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<SpeeltijdDto>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                if (dto == null)
                    return new BadRequestObjectResult(new { error = "Ongeldige request body" });
                if (dto.WedstrijdTotaal <= 0)
                    return new BadRequestObjectResult(new { error = "WedstrijdTotaal moet groter zijn dan 0" });

                var rows = await AdminSpeeltijdenRepository.UpdateAsync(
                    leeftijd,
                    new SpeeltijdInput(leeftijd, dto.Veldafmeting, dto.WedstrijdTotaal, dto.WedstrijdHelft, dto.WedstrijdRust),
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = "Leeftijdscategorie niet gevonden" });
                return new OkObjectResult(new { updated = leeftijd });
            });

    [Function("AdminSpeeltijdenDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/speeltijden/{leeftijd}")] HttpRequest req,
        string leeftijd,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminSpeeltijdenDelete"), "speeltijd verwijderen",
            async clubCode =>
            {
                var rows = await AdminSpeeltijdenRepository.DeleteAsync(
                    leeftijd, clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = "Leeftijdscategorie niet gevonden" });
                return new OkObjectResult(new { deleted = leeftijd });
            });

    private record SpeeltijdDto(
        string Leeftijd, decimal Veldafmeting,
        int WedstrijdTotaal, int WedstrijdHelft, int WedstrijdRust);
}
