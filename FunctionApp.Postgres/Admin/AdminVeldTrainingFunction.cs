using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminVeldTrainingFunction.cs</c> (#887).
/// Bewuste kopie — geen logicawijziging t.o.v. de SQL Server-tier.
/// </summary>
public static class AdminVeldTrainingFunction
{
    [Function("AdminVeldTrainingGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/veldtraining")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldTrainingGet"), "veldtraining ophalen",
            async clubCode =>
            {
                var list = await AdminVeldTrainingRepository.GetAlleAsync(
                    clubCode, PostgresDatabaseConfig.ConnectionString);
                return new OkObjectResult(list);
            });

    [Function("AdminVeldTrainingPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/veldtraining")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldTrainingPost"), "veldtraining aanmaken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<VeldTrainingRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = Valideer(dto);
                if (validatie != null) return validatie;

                var newId = await AdminVeldTrainingRepository.InsertAsync(
                    dto!.VeldNummer, dto.DagVanWeek, TimeSpan.Parse(dto.VanTijd!), TimeSpan.Parse(dto.TotTijd!),
                    dto.Omschrijving, dto.Actief ?? true, clubCode, PostgresDatabaseConfig.ConnectionString);
                return new OkObjectResult(new { id = newId, status = "aangemaakt" });
            });

    [Function("AdminVeldTrainingPut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/veldtraining/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldTrainingPut"), "veldtraining bijwerken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<VeldTrainingRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = Valideer(dto);
                if (validatie != null) return validatie;

                var rows = await AdminVeldTrainingRepository.UpdateAsync(
                    id, dto!.VeldNummer, dto.DagVanWeek, TimeSpan.Parse(dto.VanTijd!), TimeSpan.Parse(dto.TotTijd!),
                    dto.Omschrijving, dto.Actief ?? true, clubCode, PostgresDatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });
                return new OkObjectResult(new { id, status = "bijgewerkt" });
            });

    [Function("AdminVeldTrainingDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/veldtraining/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldTrainingDelete"), "veldtraining verwijderen",
            async clubCode =>
            {
                var rows = await AdminVeldTrainingRepository.DeleteAsync(
                    id, clubCode, PostgresDatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} niet gevonden" });
                return new OkObjectResult(new { id, status = "verwijderd" });
            });

    private static IActionResult? Valideer(VeldTrainingRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        if (dto.VeldNummer <= 0) return new BadRequestObjectResult(new { error = "VeldNummer vereist" });
        if (dto.DagVanWeek < 1 || dto.DagVanWeek > 7)
            return new BadRequestObjectResult(new { error = "DagVanWeek moet 1–7 zijn" });
        if (string.IsNullOrWhiteSpace(dto.VanTijd) || !TimeSpan.TryParse(dto.VanTijd, out var van))
            return new BadRequestObjectResult(new { error = "VanTijd vereist HH:mm formaat" });
        if (string.IsNullOrWhiteSpace(dto.TotTijd) || !TimeSpan.TryParse(dto.TotTijd, out var tot))
            return new BadRequestObjectResult(new { error = "TotTijd vereist HH:mm formaat" });
        if (tot <= van)
            return new BadRequestObjectResult(new { error = "TotTijd moet na VanTijd liggen" });
        return null;
    }

    public class VeldTrainingRequest
    {
        public int     VeldNummer    { get; set; }
        public int     DagVanWeek    { get; set; }
        public string? VanTijd       { get; set; }
        public string? TotTijd       { get; set; }
        public string? Omschrijving  { get; set; }
        public bool?   Actief        { get; set; }
    }
}
