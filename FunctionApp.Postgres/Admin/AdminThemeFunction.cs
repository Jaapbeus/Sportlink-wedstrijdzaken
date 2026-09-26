using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Theming;
using System.Text.Json;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminThemeFunction.cs</c> (#887). Vertaling:
/// <c>[dbo].[AppSettings]</c> → <c>public.appsettings</c> (incl. <c>faviconurl</c>/<c>logourl</c>,
/// zie <c>005_appsettings_theme_assets.sql</c>).
/// <para>
/// Sinds #1248 staat alle tier-onafhankelijke logica in <see cref="ThemeCore"/> — de
/// HTML-scraping, de hexvalidatie, de SSRF-allowlist-orkestratie en de standaardkleuren waren
/// daarvóór een woordelijke kopie van de SQL Server-tier. Wat hier overblijft is uitsluitend de
/// Npgsql-specifieke databasetoegang en de vertaling van een <see cref="ThemeCore"/>-status naar
/// een HTTP-respons.
/// </para>
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
                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(@"
                    SELECT themecolorprimary, themecolorsecondary, themecoloraccent,
                           themecolortextonprimary, themeclubwebsiteurl,
                           faviconurl, logourl,
                           themecolorslightjson, themecolorsdarkjson
                    FROM public.appsettings
                    WHERE clubcode = @clubcode", connection);
                command.Parameters.AddWithValue("clubcode", clubCode);
                await using var reader = await command.ExecuteReaderAsync();
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

                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(@"
                    UPDATE public.appsettings
                    SET themecolorprimary       = @primary,
                        themecolorsecondary     = @secondary,
                        themecoloraccent        = @accent,
                        themecolortextonprimary = @textonprimary,
                        themeclubwebsiteurl     = @websiteurl,
                        faviconurl              = @faviconurl,
                        logourl                 = @logourl,
                        themecolorslightjson    = @lightjson,
                        themecolorsdarkjson     = @darkjson
                    WHERE clubcode             = @clubcode", connection);
                command.Parameters.AddWithValue("primary",        dto.Primary       ?? ThemeCore.DefaultPrimaryColor);
                command.Parameters.AddWithValue("secondary",      dto.Secondary     ?? ThemeCore.DefaultSecondaryColor);
                command.Parameters.AddWithValue("accent",         dto.Accent        ?? ThemeCore.DefaultAccentColor);
                command.Parameters.AddWithValue("textonprimary",  dto.TextOnPrimary ?? ThemeCore.DefaultTextOnPrimaryColor);
                command.Parameters.AddWithValue("websiteurl",     (object?)dto.ClubWebsiteUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("faviconurl",     (object?)dto.FaviconUrl     ?? DBNull.Value);
                command.Parameters.AddWithValue("logourl",        (object?)dto.LogoUrl        ?? DBNull.Value);
                command.Parameters.AddWithValue("lightjson",      (object?)ThemeCore.PaletNaarJson(dto.LightColors) ?? DBNull.Value);
                command.Parameters.AddWithValue("darkjson",       (object?)ThemeCore.PaletNaarJson(dto.DarkColors)  ?? DBNull.Value);
                command.Parameters.AddWithValue("clubcode",       clubCode);
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
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
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

    // Leest themeclubwebsiteurl uit DB en geeft de hostnaam terug voor de SSRF-allowlist (#422).
    // Retourneert null als de instelling leeg is of DB niet beschikbaar is → extract geblokkeerd.
    private static async Task<string?> GetToegestaneWebsiteHostAsync(string clubCode, ILogger log)
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT themeclubwebsiteurl FROM public.appsettings WHERE clubcode = @cc", connection);
            cmd.Parameters.AddWithValue("cc", clubCode);
            return ThemeCore.HostUitWebsiteUrl(await cmd.ExecuteScalarAsync() as string);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon toegestane website-host niet bepalen voor extractie");
            return null;
        }
    }
}
