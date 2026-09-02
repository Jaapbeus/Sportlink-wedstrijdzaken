using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor VeldPeriode — herbruikbare regimes (bijv. "Zomerstop", "Competitie") waar
/// veldbeschikbaarheid-vensters aan gekoppeld kunnen worden (#581).
/// </summary>
public static class AdminVeldPeriodeFunction
{
    [Function("AdminVeldPeriodeGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/veldperiodes")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldPeriodeGet"), "veldperiodes ophalen",
            async clubCode =>
            {
                var list = await AdminVeldPeriodeRepository.GetAlleAsync(
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(list);
            });

    [Function("AdminVeldPeriodePost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/veldperiodes")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldPeriodePost"), "veldperiode aanmaken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<VeldPeriodeRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = Valideer(dto, out var datumVan, out var datumTot);
                if (validatie != null) return validatie;

                var cs = SystemUtilities.DatabaseConfig.ConnectionString;
                var actief = dto!.Actief ?? true;
                // Een inactieve periode kan per definitie nooit tegelijk actief zijn met een
                // andere — de overlapcontrole is dan overbodig en zou een geldige combinatie
                // onterecht blokkeren (bijv. een bewust uitgeschakelde "Zomerstop vorig jaar").
                if (actief && await AdminVeldPeriodeRepository.OverlaptMetAndereAsync(datumVan, datumTot, clubCode, cs))
                    return new ConflictObjectResult(new { error = "Periode overlapt met een bestaande actieve periode van deze club" });

                var newId = await AdminVeldPeriodeRepository.InsertAsync(
                    dto.Naam!, datumVan, datumTot, actief, clubCode, cs);
                return new OkObjectResult(new { id = newId, status = "aangemaakt" });
            });

    [Function("AdminVeldPeriodePut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/veldperiodes/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldPeriodePut"), "veldperiode bijwerken",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<VeldPeriodeRequest>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                var validatie = Valideer(dto, out var datumVan, out var datumTot);
                if (validatie != null) return validatie;

                var cs = SystemUtilities.DatabaseConfig.ConnectionString;
                if (!await AdminVeldPeriodeRepository.BestaatAsync(id, clubCode, cs))
                    return new NotFoundObjectResult(new { error = $"Periode {id} bestaat niet" });
                var actief = dto!.Actief ?? true;
                // Zie toelichting bij Post: een periode die (weer) inactief wordt, kan nooit
                // overlappen met een andere actieve periode.
                if (actief && await AdminVeldPeriodeRepository.OverlaptMetAndereAsync(datumVan, datumTot, clubCode, cs, uitgesloten: id))
                    return new ConflictObjectResult(new { error = "Periode overlapt met een bestaande actieve periode van deze club" });

                var rows = await AdminVeldPeriodeRepository.UpdateAsync(
                    id, dto.Naam!, datumVan, datumTot, actief, clubCode, cs);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Periode {id} bestaat niet" });
                return new OkObjectResult(new { id, status = "bijgewerkt" });
            });

    [Function("AdminVeldPeriodeDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/veldperiodes/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminVeldPeriodeDelete"), "veldperiode verwijderen",
            async clubCode =>
            {
                var cs = SystemUtilities.DatabaseConfig.ConnectionString;
                if (await AdminVeldPeriodeRepository.InGebruikAsync(id, cs))
                    return new ConflictObjectResult(new
                    {
                        error = "Periode is nog gekoppeld aan één of meer veldbeschikbaarheid-vensters — koppel die eerst los of verwijder ze"
                    });

                var rows = await AdminVeldPeriodeRepository.DeleteAsync(id, clubCode, cs);
                if (rows == 0) return new NotFoundObjectResult(new { error = $"Periode {id} niet gevonden" });
                return new OkObjectResult(new { id, status = "verwijderd" });
            });

    private static IActionResult? Valideer(VeldPeriodeRequest? dto, out DateOnly datumVan, out DateOnly datumTot)
    {
        datumVan = default;
        datumTot = default;
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        if (string.IsNullOrWhiteSpace(dto.Naam))
            return new BadRequestObjectResult(new { error = "Naam verplicht" });
        if (string.IsNullOrWhiteSpace(dto.DatumVan) || !DateOnly.TryParse(dto.DatumVan, out datumVan))
            return new BadRequestObjectResult(new { error = "DatumVan vereist yyyy-MM-dd formaat" });
        if (string.IsNullOrWhiteSpace(dto.DatumTot) || !DateOnly.TryParse(dto.DatumTot, out datumTot))
            return new BadRequestObjectResult(new { error = "DatumTot vereist yyyy-MM-dd formaat" });
        if (datumTot < datumVan)
            return new BadRequestObjectResult(new { error = "DatumTot moet op of na DatumVan liggen" });
        return null;
    }

    public class VeldPeriodeRequest
    {
        public string? Naam     { get; set; }
        public string? DatumVan { get; set; }
        public string? DatumTot { get; set; }
        public bool?   Actief   { get; set; }
    }
}
