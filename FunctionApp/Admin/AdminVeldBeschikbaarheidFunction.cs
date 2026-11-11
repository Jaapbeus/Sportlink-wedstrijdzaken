using Planner.Endpoints.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using Planner.Endpoints.Admin;

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
            clubCode => BoundedRequestBodyReader.ExecuteJsonAsync<VeldBeschikbaarheidRequest>(req, async dto =>
            {
                var validatie = Valideer(dto);
                if (validatie != null) return validatie;

                var cs = SystemUtilities.DatabaseConfig.ConnectionString;
                var periodeValidatie = await ValideerPeriodeAsync(dto!.PeriodeId, clubCode, cs);
                if (periodeValidatie != null) return periodeValidatie;

                var conflict = await VeldBeschikbaarheidUpdateCore.ConflicterendeCombinatieAsync(
                    () => AdminVeldBeschikbaarheidRepository.BestaatAndereVoorUpdateAsync(id, dto.PeriodeId, clubCode, cs));
                if (conflict != null) return conflict;

                var rows = await AdminVeldBeschikbaarheidRepository.UpdateAsync(
                    id, TimeSpan.Parse(dto.BeschikbaarVanaf!), TimeSpan.Parse(dto.BeschikbaarTot!),
                    dto.GebruikZonsondergang, dto.PeriodeId, clubCode, cs);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });
                return new OkObjectResult(new { id, status = "bijgewerkt" });
            }));

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

    [Function("AdminVeldenPost")]
    public static Task<IActionResult> PostVeld(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/velden")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldenPost"), "veld aanmaken",
            clubCode => BoundedRequestBodyReader.ExecuteJsonAsync<VeldCreateRequest>(req, async dto =>
            {
                var validatie = ValideerVeld(dto);
                if (validatie != null) return validatie;

                var cs = SystemUtilities.DatabaseConfig.ConnectionString;
                if (await AdminVeldBeschikbaarheidRepository.VeldNummerBestaatAsync(dto!.VeldNummer, cs))
                    return new ConflictObjectResult(new { error = $"VeldNummer {dto.VeldNummer} bestaat al" });

                await AdminVeldBeschikbaarheidRepository.InsertVeldAsync(
                    dto.VeldNummer, dto.VeldNaam!, dto.VeldType!, dto.HeeftKunstlicht, dto.Actief ?? true,
                    clubCode, cs);
                return new OkObjectResult(new { veldNummer = dto.VeldNummer, status = "aangemaakt" });
            }));

    [Function("AdminVeldenPut")]
    public static Task<IActionResult> PutVeld(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/velden/{veldNummer:int}")] HttpRequest req,
        int veldNummer,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldenPut"), "veld bijwerken",
            clubCode => BoundedRequestBodyReader.ExecuteJsonAsync<VeldUpdateRequest>(req, async dto =>
            {
                var validatie = ValideerVeldUpdate(dto);
                if (validatie != null) return validatie;

                var rows = await AdminVeldBeschikbaarheidRepository.UpdateVeldAsync(
                    veldNummer, dto!.VeldNaam!, dto.VeldType!, dto.HeeftKunstlicht, dto.Actief,
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Veld {veldNummer} bestaat niet" });
                return new OkObjectResult(new { veldNummer, status = "bijgewerkt" });
            }));

    [Function("AdminVeldBeschikbaarheidPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/veldbeschikbaarheid")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldBeschikbaarheidPost"), "veldbeschikbaarheid aanmaken",
            clubCode => BoundedRequestBodyReader.ExecuteJsonAsync<VeldBeschikbaarheidCreateRequest>(req, async dto =>
            {
                if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
                if (dto.VeldNummer <= 0)  return new BadRequestObjectResult(new { error = "VeldNummer vereist" });
                if (dto.DagVanWeek < 1 || dto.DagVanWeek > 7)
                    return new BadRequestObjectResult(new { error = "DagVanWeek moet 1–7 zijn" });
                var tijdenValidatie = ValideerTijden(dto.BeschikbaarVanaf, dto.BeschikbaarTot);
                if (tijdenValidatie != null) return tijdenValidatie;

                var cs = SystemUtilities.DatabaseConfig.ConnectionString;
                var periodeValidatie = await ValideerPeriodeAsync(dto.PeriodeId, clubCode, cs);
                if (periodeValidatie != null) return periodeValidatie;
                if (await AdminVeldBeschikbaarheidRepository.BestaatAsync(dto.VeldNummer, dto.DagVanWeek, dto.PeriodeId, clubCode, cs))
                    return new ConflictObjectResult(new { error = "Combinatie veld + dag + periode bestaat al" });

                var newId = await AdminVeldBeschikbaarheidRepository.InsertAsync(
                    dto.VeldNummer, dto.DagVanWeek,
                    TimeSpan.Parse(dto.BeschikbaarVanaf!), TimeSpan.Parse(dto.BeschikbaarTot!),
                    dto.GebruikZonsondergang, dto.PeriodeId, clubCode, cs);
                return new OkObjectResult(new { id = newId, status = "aangemaakt" });
            }));

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

    // internal (#476-precedent): testbaar zonder de HTTP-triggerwrapper na te bootsen.
    internal static IActionResult? ValideerTijden(string? vanf, string? tot)
        => VeldBeschikbaarheidUpdateCore.ValideerTijden(vanf, tot);

    /// <summary>
    /// NULL is altijd geldig (standaardregime, #581). Een waarde moet een bestaande periode van
    /// déze club zijn — anders krijgt de beheerder een begrijpelijke foutmelding in plaats van een
    /// kale FK-violation uit SQL Server.
    /// </summary>
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
        return VeldBeschikbaarheidUpdateCore.ValideerVeldOmschrijvingen(dto.VeldNaam, dto.VeldType);
    }

    private static IActionResult? ValideerVeldUpdate(VeldUpdateRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        return VeldBeschikbaarheidUpdateCore.ValideerVeldOmschrijvingen(dto.VeldNaam, dto.VeldType);
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
        // #581: NULL = standaardregime (geldt buiten elke actieve periode).
        public int?    PeriodeId           { get; set; }
    }

    public class VeldBeschikbaarheidCreateRequest
    {
        public int     VeldNummer           { get; set; }
        public int     DagVanWeek           { get; set; }
        public string? BeschikbaarVanaf     { get; set; }
        public string? BeschikbaarTot       { get; set; }
        public bool    GebruikZonsondergang  { get; set; }
        // #581: NULL = standaardregime (geldt buiten elke actieve periode).
        public int?    PeriodeId            { get; set; }
    }
}
