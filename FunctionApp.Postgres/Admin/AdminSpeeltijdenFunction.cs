using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminSpeeltijdenFunction.cs</c> (#887).
/// Vertaling: <c>SqlException ex.Number == 2627</c> (unique violation) →
/// <c>PostgresException ex.SqlState == PostgresErrorCodes.UniqueViolation</c>.
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
                    clubCode, PostgresDatabaseConfig.ConnectionString);
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
                        new SpeeltijdInput(dto.Leeftijd, dto.Veldafmeting, dto.WedstrijdTotaal,
                            dto.WedstrijdHelft, dto.WedstrijdRust, ParseTijd(dto.StandaardVoorkeurTijd)),
                        clubCode, PostgresDatabaseConfig.ConnectionString);
                    return new CreatedResult("/api/beheer/speeltijden", new { Leeftijd = dto.Leeftijd });
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
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
                    new SpeeltijdInput(leeftijd, dto.Veldafmeting, dto.WedstrijdTotaal,
                        dto.WedstrijdHelft, dto.WedstrijdRust, ParseTijd(dto.StandaardVoorkeurTijd)),
                    clubCode, PostgresDatabaseConfig.ConnectionString);
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
                    leeftijd, clubCode, PostgresDatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = "Leeftijdscategorie niet gevonden" });
                return new OkObjectResult(new { deleted = leeftijd });
            });

    private static TimeOnly? ParseTijd(string? tijd) =>
        !string.IsNullOrWhiteSpace(tijd) && TimeOnly.TryParse(tijd, out var t) ? t : null;

    private record SpeeltijdDto(
        string Leeftijd, decimal Veldafmeting,
        int WedstrijdTotaal, int WedstrijdHelft, int WedstrijdRust,
        string? StandaardVoorkeurTijd);
}
