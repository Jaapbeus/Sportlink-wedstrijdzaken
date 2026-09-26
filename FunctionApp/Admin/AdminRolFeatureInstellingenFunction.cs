using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Sportlink;

namespace SportlinkFunction.Admin;

/// <summary>
/// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Admin/AdminRolFeatureInstellingenFunction.cs</c>
/// (#1341, epic #1338) — per-club, per-rol instelbare zichtbaarheid van Sportlink-acties.
/// </summary>
public static class AdminRolFeatureInstellingenFunction
{
    private const string RolNaam = SportlinkEndpointSupport.RolWedstrijdzaken;

    [Function("SqlAdminRolFeatureInstellingenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/rolfeatureinstellingen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SqlAdminRolFeatureInstellingenGet"), "rol-feature-instellingen ophalen",
            async clubCode =>
            {
                var alle = await RolFeatureInstellingenRepository.GetAllAsync(
                    clubCode, RolNaam, SystemUtilities.DatabaseConfig.ConnectionString);
                var result = alle.Select(kv => new { FeatureKey = kv.Key, Enabled = kv.Value });
                return new OkObjectResult(result);
            });

    [Function("SqlAdminRolFeatureInstellingenPut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/rolfeatureinstellingen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SqlAdminRolFeatureInstellingenPut"), "rol-feature-instelling wijzigen",
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
                    clubCode, RolNaam, dto.FeatureKey, dto.Enabled, SystemUtilities.DatabaseConfig.ConnectionString);

                return new OkObjectResult(new { dto.FeatureKey, dto.Enabled });
            });

    private sealed class ZetInstellingDto
    {
        public string? FeatureKey { get; set; }
        public bool Enabled { get; set; }
    }
}
