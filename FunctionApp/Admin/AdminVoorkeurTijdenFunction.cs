using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor TeamVoorkeurTijden en TeamRegels. v2 — #91 / #62.
/// </summary>
public static class AdminVoorkeurTijdenFunction
{
    // ── Voorkeurstijden ──

    [Function("AdminVoorkeurTijdenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/voorkeurstijden")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVoorkeurTijdenGet"), "voorkeurstijden ophalen",
            async clubCode =>
            {
                string? team = req.Query["team"].ToString();
                if (string.IsNullOrWhiteSpace(team)) team = null;
                var list = await AdminVoorkeurTijdenRepository.GetVoorkeurTijdenAsync(
                    clubCode, team, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(list);
            });

    [Function("AdminVoorkeurTijdenPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/voorkeurstijden")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVoorkeurTijdenPost"), "voorkeurstijd toevoegen",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<VoorkeurTijdRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = Valideer(dto);
                if (validatie != null) return validatie;
                var newId = await AdminVoorkeurTijdenRepository.InsertVoorkeurTijdAsync(
                    dto!.TeamNaam!, dto.DagVanWeek!.Value, TimeSpan.Parse(dto.VoorkeurTijd!),
                    dto.Prioriteit ?? 5, dto.Actief ?? true,
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(new { id = newId, status = "aangemaakt" });
            });

    [Function("AdminVoorkeurTijdenPut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/voorkeurstijden/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVoorkeurTijdenPut"), "voorkeurstijd bijwerken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<VoorkeurTijdRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = Valideer(dto);
                if (validatie != null) return validatie;
                var rows = await AdminVoorkeurTijdenRepository.UpdateVoorkeurTijdAsync(
                    id, dto!.TeamNaam!, dto.DagVanWeek!.Value, TimeSpan.Parse(dto.VoorkeurTijd!),
                    dto.Prioriteit ?? 5, dto.Actief ?? true,
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });
                return new OkObjectResult(new { id, status = "bijgewerkt" });
            });

    [Function("AdminVoorkeurTijdenDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/voorkeurstijden/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVoorkeurTijdenDelete"), "voorkeurstijd verwijderen",
            async clubCode =>
            {
                var rows = await AdminVoorkeurTijdenRepository.SoftDeleteVoorkeurTijdAsync(
                    id, clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });
                return new OkObjectResult(new { id, status = "soft-deleted" });
            });

    // ── TeamRegels ──

    [Function("AdminTeamRegelsGet")]
    public static Task<IActionResult> GetTeamRegels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/teamregels")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamRegelsGet"), "teamregels ophalen",
            async clubCode =>
            {
                var list = await AdminVoorkeurTijdenRepository.GetTeamRegelsAsync(
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(list);
            });

    [Function("AdminTeamRegelsPost")]
    public static Task<IActionResult> PostTeamRegel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/teamregels")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamRegelsPost"), "teamregel toevoegen",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<TeamRegelRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = ValideerRegel(dto);
                if (validatie != null) return validatie;
                TimeSpan? waardeTijd = string.IsNullOrWhiteSpace(dto!.WaardeTijd) ? null : TimeSpan.Parse(dto.WaardeTijd);
                var newId = await AdminVoorkeurTijdenRepository.InsertTeamRegelAsync(
                    dto.TeamNaam!, dto.RegelType!, dto.WaardeMinuten, dto.WaardeVeldNummer, waardeTijd,
                    dto.Prioriteit ?? 0, dto.Actief ?? true, dto.Opmerking,
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(new { id = newId, status = "aangemaakt" });
            });

    [Function("AdminTeamRegelsPut")]
    public static Task<IActionResult> PutTeamRegel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/teamregels/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamRegelsPut"), "teamregel bijwerken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<TeamRegelRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = ValideerRegel(dto);
                if (validatie != null) return validatie;
                TimeSpan? waardeTijd = string.IsNullOrWhiteSpace(dto!.WaardeTijd) ? null : TimeSpan.Parse(dto.WaardeTijd);
                var rows = await AdminVoorkeurTijdenRepository.UpdateTeamRegelAsync(
                    id, dto.TeamNaam!, dto.RegelType!, dto.WaardeMinuten, dto.WaardeVeldNummer, waardeTijd,
                    dto.Prioriteit ?? 0, dto.Actief ?? true, dto.Opmerking,
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });
                return new OkObjectResult(new { id, status = "bijgewerkt" });
            });

    [Function("AdminTeamRegelsDelete")]
    public static Task<IActionResult> DeleteTeamRegel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/teamregels/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamRegelsDelete"), "teamregel verwijderen",
            async clubCode =>
            {
                var rows = await AdminVoorkeurTijdenRepository.SoftDeleteTeamRegelAsync(
                    id, clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });
                return new OkObjectResult(new { id, status = "soft-deleted" });
            });

    private static IActionResult? ValideerRegel(TeamRegelRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        if (string.IsNullOrWhiteSpace(dto.TeamNaam))
            return new BadRequestObjectResult(new { error = "TeamNaam verplicht" });
        var toegestaneTypes = new[] { "BufferVoor", "BufferNa", "VoorkeurVeld" };
        if (!toegestaneTypes.Contains(dto.RegelType))
            return new BadRequestObjectResult(new { error = "RegelType moet BufferVoor, BufferNa of VoorkeurVeld zijn" });
        if ((dto.RegelType == "BufferVoor" || dto.RegelType == "BufferNa") && dto.WaardeMinuten == null)
            return new BadRequestObjectResult(new { error = "WaardeMinuten verplicht voor BufferVoor/BufferNa" });
        if (dto.RegelType == "VoorkeurVeld" && dto.WaardeVeldNummer == null)
            return new BadRequestObjectResult(new { error = "WaardeVeldNummer verplicht voor VoorkeurVeld" });
        if (!string.IsNullOrWhiteSpace(dto.WaardeTijd) && !TimeSpan.TryParse(dto.WaardeTijd, out _))
            return new BadRequestObjectResult(new { error = "WaardeTijd vereist HH:mm formaat" });
        return null;
    }

    private static IActionResult? Valideer(VoorkeurTijdRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        if (string.IsNullOrWhiteSpace(dto.TeamNaam))
            return new BadRequestObjectResult(new { error = "TeamNaam verplicht" });
        if (dto.DagVanWeek is null or < 1 or > 7)
            return new BadRequestObjectResult(new { error = "DagVanWeek moet 1-7 zijn (6=zaterdag)" });
        if (string.IsNullOrWhiteSpace(dto.VoorkeurTijd) || !TimeSpan.TryParse(dto.VoorkeurTijd, out _))
            return new BadRequestObjectResult(new { error = "VoorkeurTijd vereist HH:mm formaat" });
        return null;
    }

    public class TeamRegelRequest
    {
        public string? TeamNaam          { get; set; }
        public string? RegelType         { get; set; }
        public int?    WaardeMinuten     { get; set; }
        public int?    WaardeVeldNummer  { get; set; }
        public string? WaardeTijd        { get; set; }
        public int?    Prioriteit        { get; set; }
        public bool?   Actief            { get; set; }
        public string? Opmerking         { get; set; }
    }

    public class VoorkeurTijdRequest
    {
        public string? TeamNaam     { get; set; }
        public int?    DagVanWeek   { get; set; }
        public string? VoorkeurTijd { get; set; }
        public int?    Prioriteit   { get; set; }
        public bool?   Actief       { get; set; }
    }
}
