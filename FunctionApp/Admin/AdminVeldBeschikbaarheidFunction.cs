using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor VeldBeschikbaarheid (openingstijden sportpark per veld per dag). v2.
/// </summary>
public static class AdminVeldBeschikbaarheidFunction
{
    [Function("AdminVeldBeschikbaarheidGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/veldbeschikbaarheid")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldBeschikbaarheidGet"), "veldbeschikbaarheid ophalen",
            async clubCode =>
            {
                var list = await AdminVeldBeschikbaarheidRepository.GetAlleAsync(
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(list);
            });

    [Function("AdminVeldBeschikbaarheidPut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/veldbeschikbaarheid/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldBeschikbaarheidPut"), "veldbeschikbaarheid bijwerken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<VeldBeschikbaarheidRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = Valideer(dto);
                if (validatie != null) return validatie;

                var rows = await AdminVeldBeschikbaarheidRepository.UpdateAsync(
                    id, TimeSpan.Parse(dto!.BeschikbaarVanaf!), TimeSpan.Parse(dto.BeschikbaarTot!),
                    dto.GebruikZonsondergang, clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });
                return new OkObjectResult(new { id, status = "bijgewerkt" });
            });

    [Function("AdminVeldenGet")]
    public static Task<IActionResult> GetVelden(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/velden")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldenGet"), "velden ophalen",
            async clubCode =>
            {
                var list = await AdminVeldBeschikbaarheidRepository.GetVeldenAsync(
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(list);
            });

    [Function("AdminVeldBeschikbaarheidPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/veldbeschikbaarheid")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldBeschikbaarheidPost"), "veldbeschikbaarheid aanmaken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<VeldBeschikbaarheidCreateRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
                if (dto.VeldNummer <= 0)  return new BadRequestObjectResult(new { error = "VeldNummer vereist" });
                if (dto.DagVanWeek < 1 || dto.DagVanWeek > 7)
                    return new BadRequestObjectResult(new { error = "DagVanWeek moet 1–7 zijn" });
                var tijdenValidatie = ValideerTijden(dto.BeschikbaarVanaf, dto.BeschikbaarTot);
                if (tijdenValidatie != null) return tijdenValidatie;

                var cs = SystemUtilities.DatabaseConfig.ConnectionString;
                if (await AdminVeldBeschikbaarheidRepository.BestaatAsync(dto.VeldNummer, dto.DagVanWeek, clubCode, cs))
                    return new ConflictObjectResult(new { error = "Combinatie veld + dag bestaat al" });

                var newId = await AdminVeldBeschikbaarheidRepository.InsertAsync(
                    dto.VeldNummer, dto.DagVanWeek,
                    TimeSpan.Parse(dto.BeschikbaarVanaf!), TimeSpan.Parse(dto.BeschikbaarTot!),
                    dto.GebruikZonsondergang, clubCode, cs);
                return new OkObjectResult(new { id = newId, status = "aangemaakt" });
            });

    [Function("AdminVeldBeschikbaarheidDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/veldbeschikbaarheid/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldBeschikbaarheidDelete"), "veldbeschikbaarheid verwijderen",
            async clubCode =>
            {
                var rows = await AdminVeldBeschikbaarheidRepository.DeleteAsync(
                    id, clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} niet gevonden" });
                return new OkObjectResult(new { id, status = "verwijderd" });
            });

    private static IActionResult? Valideer(VeldBeschikbaarheidRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        return ValideerTijden(dto.BeschikbaarVanaf, dto.BeschikbaarTot);
    }

    private static IActionResult? ValideerTijden(string? vanf, string? tot)
    {
        if (string.IsNullOrWhiteSpace(vanf) || !TimeSpan.TryParse(vanf, out _))
            return new BadRequestObjectResult(new { error = "BeschikbaarVanaf vereist HH:mm formaat" });
        if (string.IsNullOrWhiteSpace(tot) || !TimeSpan.TryParse(tot, out _))
            return new BadRequestObjectResult(new { error = "BeschikbaarTot vereist HH:mm formaat" });
        return null;
    }

    public class VeldBeschikbaarheidRequest
    {
        public string? BeschikbaarVanaf    { get; set; }
        public string? BeschikbaarTot      { get; set; }
        public bool    GebruikZonsondergang { get; set; }
    }

    public class VeldBeschikbaarheidCreateRequest
    {
        public int     VeldNummer           { get; set; }
        public int     DagVanWeek           { get; set; }
        public string? BeschikbaarVanaf     { get; set; }
        public string? BeschikbaarTot       { get; set; }
        public bool    GebruikZonsondergang  { get; set; }
    }
}
