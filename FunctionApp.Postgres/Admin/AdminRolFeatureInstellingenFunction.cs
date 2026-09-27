using FunctionApp.Postgres.Sportlink;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using Planner.Shared.Autorisatie;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// <c>GET/PUT /api/beheer/rolfeatureinstellingen</c> (#1390, opvolger van #1341/epic #1338) — de
/// volledige toegangsmatrix: per-club, per-rol instelbare zichtbaarheid van elk Admin GUI-menu-item
/// (<see cref="MenuFeatureKeys"/>) plus de 3 Sportlink-mutatieacties (<see cref="SportlinkRolFeature"/>).
/// Instelbare rollen: <see cref="RolNamen.Alle"/> — <c>admin</c> komt hier bewust niet in voor, want
/// admin heeft altijd alles aan (zie o.a. <c>SportlinkMatchFunction.BepaalRolFeatureToestemmingenAsync</c>)
/// en is niet instelbaar. Dit endpoint zelf blijft uitsluitend voor <c>admin</c> bereikbaar (achter
/// <c>AdminEndpoint.ExecuteAsync</c>) — een niet-adminrol kan zichzelf via deze matrix dus nooit
/// meer rechten geven. SQL Server-tegenhanger: <c>FunctionApp/Admin/AdminRolFeatureInstellingenFunction.cs</c>.
/// </summary>
public static class AdminRolFeatureInstellingenFunction
{
    [Function("AdminRolFeatureInstellingenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/rolfeatureinstellingen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminRolFeatureInstellingenGet"), "rol-feature-instellingen ophalen",
            async clubCode =>
            {
                var matrix = await RolFeatureInstellingenRepository.GetMatrixAsync(
                    clubCode, PostgresDatabaseConfig.ConnectionString);
                var result = matrix.Select(kv => new { RolNaam = kv.Key.RolNaam, FeatureKey = kv.Key.FeatureKey, Enabled = kv.Value });
                return new OkObjectResult(result);
            });

    [Function("AdminRolFeatureInstellingenPut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/rolfeatureinstellingen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminRolFeatureInstellingenPut"), "rol-feature-instelling wijzigen",
            async clubCode =>
            {
                var dto = JsonConvert.DeserializeObject<ZetInstellingDto>(
                    await new StreamReader(req.Body).ReadToEndAsync());

                var validatiefout = ValideerRolEnFeatureKey(dto?.RolNaam, dto?.FeatureKey);
                if (validatiefout is not null) return validatiefout;

                await RolFeatureInstellingenRepository.SetAsync(
                    clubCode, dto!.RolNaam!, dto.FeatureKey!, dto.Enabled, PostgresDatabaseConfig.ConnectionString);

                return new OkObjectResult(new { dto.RolNaam, dto.FeatureKey, dto.Enabled });
            });

    /// <summary>
    /// Losgetrokken van <see cref="Put"/> zodat de validatie zonder databaseverbinding getest kan
    /// worden (<c>AdminEndpoint.ExecuteAsync</c> wacht vóór de <c>work</c>-lambda op de database —
    /// zie <c>AdminVeldBeschikbaarheidFunction.ValideerTijden</c> voor hetzelfde precedent). De
    /// validatie zelf staat in <see cref="RolFeatureMatrixCore.Valideer"/> (tier-onafhankelijk);
    /// hier alleen de vertaling naar een HTTP-respons. Retourneert <c>null</c> als beide waarden
    /// geldig zijn.
    /// </summary>
    internal static BadRequestObjectResult? ValideerRolEnFeatureKey(string? rolNaam, string? featureKey)
    {
        var fout = RolFeatureMatrixCore.Valideer(rolNaam, featureKey);
        return fout is null ? null : new BadRequestObjectResult(new { error = fout });
    }

    private sealed class ZetInstellingDto
    {
        public string? RolNaam { get; set; }
        public string? FeatureKey { get; set; }
        public bool Enabled { get; set; }
    }
}
