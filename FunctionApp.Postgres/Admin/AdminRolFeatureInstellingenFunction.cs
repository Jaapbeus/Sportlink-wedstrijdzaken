using FunctionApp.Postgres.Sportlink;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// <c>GET/PUT /api/beheer/rolfeatureinstellingen</c> (#1341, epic #1338) — per-club, per-rol
/// instelbare zichtbaarheid van Sportlink-acties (kleedkamers/scheidsrechter/veld). Uitsluitend
/// de <c>Wedstrijdzaken</c>-rol is instelbaar; <c>admin</c> heeft altijd alles aan (zie
/// <c>SportlinkMatchFunction.BepaalRolFeatureToestemmingenAsync</c>) en komt hier niet in voor.
/// SQL Server-tegenhanger: <c>FunctionApp/Admin/AdminRolFeatureInstellingenFunction.cs</c>.
/// </summary>
public static class AdminRolFeatureInstellingenFunction
{
    // #1341: vandaag de enige instelbare rol — zie de toelichting in issue #1341 over waarom
    // 'admin' hier bewust niet in voorkomt (altijd aan, niet instelbaar).
    private const string RolNaam = SportlinkEndpointSupport.RolWedstrijdzaken;

    [Function("AdminRolFeatureInstellingenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/rolfeatureinstellingen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminRolFeatureInstellingenGet"), "rol-feature-instellingen ophalen",
            async clubCode =>
            {
                var alle = await RolFeatureInstellingenRepository.GetAllAsync(
                    clubCode, RolNaam, PostgresDatabaseConfig.ConnectionString);
                var result = alle.Select(kv => new { FeatureKey = kv.Key, Enabled = kv.Value });
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

                if (string.IsNullOrWhiteSpace(dto?.FeatureKey) ||
                    !SportlinkRolFeature.Alle.Contains(dto.FeatureKey))
                    return new BadRequestObjectResult(new
                    {
                        error = $"Onbekende FeatureKey. Toegestaan: {string.Join(", ", SportlinkRolFeature.Alle)}."
                    });

                await RolFeatureInstellingenRepository.SetAsync(
                    clubCode, RolNaam, dto.FeatureKey, dto.Enabled, PostgresDatabaseConfig.ConnectionString);

                return new OkObjectResult(new { dto.FeatureKey, dto.Enabled });
            });

    private sealed class ZetInstellingDto
    {
        public string? FeatureKey { get; set; }
        public bool Enabled { get; set; }
    }
}
