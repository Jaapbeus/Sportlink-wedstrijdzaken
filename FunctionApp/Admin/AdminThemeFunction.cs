using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Planner.Shared.Theming;
using System.Text.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor club-thema beheer. v2 — #325.
///
/// GET  /api/beheer/theme              → haal huidige themakleuren op
/// PUT  /api/beheer/theme              → sla themakleuren op
/// POST /api/beheer/theme/extract?url= → extraheer kleuren uit club-website (SSRF-beschermd)
///
/// Sinds #1248 staat alle tier-onafhankelijke logica in <see cref="ThemeCore"/>: de
/// regex-extractie, de hexvalidatie, de SSRF-allowlist-orkestratie en de standaardkleuren. Dit
/// bestand bevat nog uitsluitend de SQL Server-specifieke databasetoegang en de vertaling van een
/// <see cref="ThemeCore"/>-status naar een HTTP-respons — zelfde scheiding als
/// <c>FeedbackFunction</c>/<c>FeedbackCore</c> (#1130).
/// </summary>
public static class AdminThemeFunction
{
    [Function("AdminThemeGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/theme")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminThemeGet"), "thema ophalen",
            async clubCode =>
            {
                using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                using var command = new SqlCommand(@"
                    SELECT [ThemeColorPrimary], [ThemeColorSecondary], [ThemeColorAccent],
                           [ThemeColorTextOnPrimary], [ThemeClubWebsiteUrl],
                           [FaviconUrl], [LogoUrl],
                           [ThemeColorsLightJson], [ThemeColorsDarkJson]
                    FROM [dbo].[AppSettings]
                    WHERE [ClubCode] = @ClubCode", connection);
                command.Parameters.AddWithValue("@ClubCode", clubCode);
                using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return new OkObjectResult(ThemeCore.BouwResponse(ThemeCore.Standaard));

                return new OkObjectResult(ThemeCore.BouwResponse(new ThemeWaarden(
                    Primary:        reader.IsDBNull(0) ? ThemeCore.DefaultPrimaryColor       : reader.GetString(0),
                    Secondary:      reader.IsDBNull(1) ? ThemeCore.DefaultSecondaryColor     : reader.GetString(1),
                    Accent:         reader.IsDBNull(2) ? ThemeCore.DefaultAccentColor        : reader.GetString(2),
                    TextOnPrimary:  reader.IsDBNull(3) ? ThemeCore.DefaultTextOnPrimaryColor : reader.GetString(3),
                    ClubWebsiteUrl: reader.IsDBNull(4) ? ""                                  : reader.GetString(4),
                    FaviconUrl:     reader.IsDBNull(5) ? null                                : reader.GetString(5),
                    LogoUrl:        reader.IsDBNull(6) ? null                                : reader.GetString(6),
                    LightColors:    ThemeCore.PaletUitJson(reader.IsDBNull(7) ? null : reader.GetString(7)),
                    DarkColors:     ThemeCore.PaletUitJson(reader.IsDBNull(8) ? null : reader.GetString(8)))));
            });

    [Function("AdminThemePut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/theme")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminThemePut");
        return AdminEndpoint.ExecuteAsync(req, log, "thema opslaan",
            async clubCode =>
            {
                using var sr = new StreamReader(req.Body);
                var body = await sr.ReadToEndAsync();

                ThemeUpdateRequest? dto = null;
                try
                {
                    dto = JsonSerializer.Deserialize<ThemeUpdateRequest>(body,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                }
                catch { }

                if (dto == null)
                    return new BadRequestObjectResult(new { error = "Ongeldige JSON." });

                var validatie = await ThemeCore.ValideerUpdateAsync(dto);
                if (validatie.Status != ThemeValidatieStatus.Ok)
                    return new BadRequestObjectResult(new { error = validatie.Foutmelding });

                using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                using var command = new SqlCommand(@"
                    UPDATE [dbo].[AppSettings]
                    SET [ThemeColorPrimary]        = @Primary,
                        [ThemeColorSecondary]      = @Secondary,
                        [ThemeColorAccent]         = @Accent,
                        [ThemeColorTextOnPrimary]  = @TextOnPrimary,
                        [ThemeClubWebsiteUrl]      = @WebsiteUrl,
                        [FaviconUrl]               = @FaviconUrl,
                        [LogoUrl]                  = @LogoUrl,
                        [ThemeColorsLightJson]     = @LightJson,
                        [ThemeColorsDarkJson]      = @DarkJson
                    WHERE [ClubCode]              = @ClubCode", connection);
                command.Parameters.AddWithValue("@Primary",        dto.Primary       ?? ThemeCore.DefaultPrimaryColor);
                command.Parameters.AddWithValue("@Secondary",      dto.Secondary     ?? ThemeCore.DefaultSecondaryColor);
                command.Parameters.AddWithValue("@Accent",         dto.Accent        ?? ThemeCore.DefaultAccentColor);
                command.Parameters.AddWithValue("@TextOnPrimary",  dto.TextOnPrimary ?? ThemeCore.DefaultTextOnPrimaryColor);
                command.Parameters.AddWithValue("@WebsiteUrl",     (object?)dto.ClubWebsiteUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("@FaviconUrl",     (object?)dto.FaviconUrl     ?? DBNull.Value);
                command.Parameters.AddWithValue("@LogoUrl",        (object?)dto.LogoUrl        ?? DBNull.Value);
                command.Parameters.AddWithValue("@LightJson",      (object?)ThemeCore.PaletNaarJson(dto.LightColors) ?? DBNull.Value);
                command.Parameters.AddWithValue("@DarkJson",       (object?)ThemeCore.PaletNaarJson(dto.DarkColors)  ?? DBNull.Value);
                command.Parameters.AddWithValue("@ClubCode",       clubCode);
                await command.ExecuteNonQueryAsync();

                log.LogInformation("Club-thema bijgewerkt");
                return new OkObjectResult(new { success = true });
            });
    }

    // #1350: bewust NIET via AdminEndpoint.ExecuteAsync — dat zou de databasewacht vóór de
    // URL-vormcontrole zetten, terwijl het luie pad hieronder juist geen databaseaanroep wil doen
    // voor een onbruikbare URL. Staat daarom, met deze reden, in
    // scripts/ci/endpoint-autorisatie-allowlist.txt. De poort zelf is identiek.
    [Function("AdminThemeExtract")]
    public static async Task<IActionResult> Extract(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/theme/extract")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminThemeExtract");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;

        // Lui: ThemeCore controleert eerst de vorm van de URL, zodat een onbruikbare URL geen
        // databaseaanroep kost.
        var resultaat = await ThemeCore.ExtraheerAsync(req.Query["url"].ToString(), async () =>
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            return await GetToegestaneWebsiteHostAsync(EasyAuthHelper.GetClubCodeFromRequest(req), log);
        }, log);
        return resultaat.Status switch
        {
            ThemeExtractieStatus.Ok => new OkObjectResult(new
            {
                colors = resultaat.Colors,
                faviconUrl = resultaat.FaviconUrl,
                logoUrl = resultaat.LogoUrl
            }),
            ThemeExtractieStatus.OphalenMislukt =>
                new ObjectResult(new { error = resultaat.Foutmelding }) { StatusCode = 502 },
            _ => new BadRequestObjectResult(new { error = resultaat.Foutmelding })
        };
    }

    // Leest ThemeClubWebsiteUrl uit DB en geeft de hostnaam terug voor de SSRF-allowlist (#422).
    // Retourneert null als de instelling leeg is of DB niet beschikbaar is → extract geblokkeerd.
    private static async Task<string?> GetToegestaneWebsiteHostAsync(string clubCode, ILogger log)
    {
        try
        {
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT [ThemeClubWebsiteUrl] FROM [dbo].[AppSettings] WHERE [ClubCode] = @cc", connection);
            cmd.Parameters.AddWithValue("@cc", clubCode);
            return ThemeCore.HostUitWebsiteUrl(await cmd.ExecuteScalarAsync() as string);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon toegestane website-host niet bepalen voor extractie");
            return null;
        }
    }
}
