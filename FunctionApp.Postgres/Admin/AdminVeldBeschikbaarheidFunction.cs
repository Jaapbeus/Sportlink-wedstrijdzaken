using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminVeldBeschikbaarheidFunction.cs</c>
/// (#887). Bewuste kopie — geen logicawijziging t.o.v. de SQL Server-tier.
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
                    clubCode, PostgresDatabaseConfig.ConnectionString);
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

                var cs = PostgresDatabaseConfig.ConnectionString;
                var periodeValidatie = await ValideerPeriodeAsync(dto!.PeriodeId, clubCode, cs);
                if (periodeValidatie != null) return periodeValidatie;

                var rows = await AdminVeldBeschikbaarheidRepository.UpdateAsync(
                    id, TimeSpan.Parse(dto.BeschikbaarVanaf!), TimeSpan.Parse(dto.BeschikbaarTot!),
                    dto.GebruikZonsondergang, dto.PeriodeId, clubCode, cs);
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
                    clubCode, PostgresDatabaseConfig.ConnectionString);
                return new OkObjectResult(list);
            });

    [Function("AdminVeldenPost")]
    public static Task<IActionResult> PostVeld(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/velden")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldenPost"), "veld aanmaken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<VeldCreateRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = ValideerVeld(dto);
                if (validatie != null) return validatie;

                var cs = PostgresDatabaseConfig.ConnectionString;
                if (await AdminVeldBeschikbaarheidRepository.VeldNummerBestaatAsync(dto!.VeldNummer, cs))
                    return new ConflictObjectResult(new { error = $"VeldNummer {dto.VeldNummer} bestaat al" });

                await AdminVeldBeschikbaarheidRepository.InsertVeldAsync(
                    dto.VeldNummer, dto.VeldNaam!, dto.VeldType!, dto.HeeftKunstlicht, dto.Actief ?? true,
                    clubCode, cs);
                return new OkObjectResult(new { veldNummer = dto.VeldNummer, status = "aangemaakt" });
            });

    [Function("AdminVeldenPut")]
    public static Task<IActionResult> PutVeld(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/velden/{veldNummer:int}")] HttpRequest req,
        int veldNummer,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldenPut"), "veld bijwerken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<VeldUpdateRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = ValideerVeldUpdate(dto);
                if (validatie != null) return validatie;

                var rows = await AdminVeldBeschikbaarheidRepository.UpdateVeldAsync(
                    veldNummer, dto!.VeldNaam!, dto.VeldType!, dto.HeeftKunstlicht, dto.Actief,
                    clubCode, PostgresDatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Veld {veldNummer} bestaat niet" });
                return new OkObjectResult(new { veldNummer, status = "bijgewerkt" });
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

                var cs = PostgresDatabaseConfig.ConnectionString;
                var periodeValidatie = await ValideerPeriodeAsync(dto.PeriodeId, clubCode, cs);
                if (periodeValidatie != null) return periodeValidatie;
                if (await AdminVeldBeschikbaarheidRepository.BestaatAsync(dto.VeldNummer, dto.DagVanWeek, dto.PeriodeId, clubCode, cs))
                    return new ConflictObjectResult(new { error = "Combinatie veld + dag + periode bestaat al" });

                var newId = await AdminVeldBeschikbaarheidRepository.InsertAsync(
                    dto.VeldNummer, dto.DagVanWeek,
                    TimeSpan.Parse(dto.BeschikbaarVanaf!), TimeSpan.Parse(dto.BeschikbaarTot!),
                    dto.GebruikZonsondergang, dto.PeriodeId, clubCode, cs);
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
                    id, clubCode, PostgresDatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} niet gevonden" });
                return new OkObjectResult(new { id, status = "verwijderd" });
            });

    private static IActionResult? Valideer(VeldBeschikbaarheidRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        return ValideerTijden(dto.BeschikbaarVanaf, dto.BeschikbaarTot);
    }

    // internal (#476-precedent): testbaar zonder de HTTP-triggerwrapper na te bootsen.
    internal static IActionResult? ValideerTijden(string? vanf, string? tot)
    {
        if (string.IsNullOrWhiteSpace(vanf) || !TimeSpan.TryParse(vanf, out var vanfTijd))
            return new BadRequestObjectResult(new { error = "BeschikbaarVanaf vereist HH:mm formaat" });
        if (string.IsNullOrWhiteSpace(tot) || !TimeSpan.TryParse(tot, out var totTijd))
            return new BadRequestObjectResult(new { error = "BeschikbaarTot vereist HH:mm formaat" });
        // #957: zonder deze check accepteerde de API stilzwijgend een venster dat vóór het begin
        // eindigt (bijv. verwisselde velden), met onvoorspelbaar effect op de planner.
        if (totTijd <= vanfTijd)
            return new BadRequestObjectResult(new { error = "BeschikbaarTot moet na BeschikbaarVanaf liggen" });
        return null;
    }

    private static async Task<IActionResult?> ValideerPeriodeAsync(int? periodeId, string clubCode, string cs)
    {
        if (periodeId == null) return null;
        if (!await AdminVeldPeriodeRepository.BestaatAsync(periodeId.Value, clubCode, cs))
            return new BadRequestObjectResult(new { error = $"Periode {periodeId} bestaat niet voor deze club" });
        return null;
    }

    private static IActionResult? ValideerVeld(VeldCreateRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        if (dto.VeldNummer <= 0) return new BadRequestObjectResult(new { error = "VeldNummer vereist" });
        if (string.IsNullOrWhiteSpace(dto.VeldNaam))
            return new BadRequestObjectResult(new { error = "VeldNaam verplicht" });
        if (string.IsNullOrWhiteSpace(dto.VeldType))
            return new BadRequestObjectResult(new { error = "VeldType verplicht (vrije tekst, bijv. kunstgras of natuurgras)" });
        return null;
    }

    private static IActionResult? ValideerVeldUpdate(VeldUpdateRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        if (string.IsNullOrWhiteSpace(dto.VeldNaam))
            return new BadRequestObjectResult(new { error = "VeldNaam verplicht" });
        if (string.IsNullOrWhiteSpace(dto.VeldType))
            return new BadRequestObjectResult(new { error = "VeldType verplicht (vrije tekst, bijv. kunstgras of natuurgras)" });
        return null;
    }

    public class VeldCreateRequest
    {
        public int     VeldNummer      { get; set; }
        public string? VeldNaam        { get; set; }
        public string? VeldType        { get; set; }
        public bool    HeeftKunstlicht { get; set; }
        public bool?   Actief          { get; set; }
    }

    public class VeldUpdateRequest
    {
        public string? VeldNaam        { get; set; }
        public string? VeldType        { get; set; }
        public bool    HeeftKunstlicht { get; set; }
        public bool    Actief          { get; set; } = true;
    }

    public class VeldBeschikbaarheidRequest
    {
        public string? BeschikbaarVanaf    { get; set; }
        public string? BeschikbaarTot      { get; set; }
        public bool    GebruikZonsondergang { get; set; }
        public int?    PeriodeId           { get; set; }
    }

    public class VeldBeschikbaarheidCreateRequest
    {
        public int     VeldNummer           { get; set; }
        public int     DagVanWeek           { get; set; }
        public string? BeschikbaarVanaf     { get; set; }
        public string? BeschikbaarTot       { get; set; }
        public bool    GebruikZonsondergang  { get; set; }
        public int?    PeriodeId            { get; set; }
    }
}
