using FunctionApp.Postgres.Integrations.SportlinkClub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/SportlinkExtensieRollenFunction.cs</c> (#988).
/// Vertaling: <c>[dbo].[SportlinkExtensieRollen]</c> → <c>public.sportlinkextensierollen</c>,
/// T-SQL <c>MERGE</c> → Postgres <c>INSERT ... ON CONFLICT ... DO UPDATE</c>.
/// </summary>
public static class SportlinkExtensieRollenFunction
{
    private static readonly string[] FunctioneleRollen = { "Wedstrijdzaken" };

    // #991: kale HttpClient voor de Keycloak-tokenexchange bij bootstrap — zelfde precedent als
    // Sync/PostgresSyncPipeline.cs (geen Polly/resilience-library elders in deze repo).
    private static readonly HttpClient TokenHttp = new();

    [Function("SportlinkExtensieRollenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/sportlink-extensie/rollen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkExtensieRollenGet"), "sportlink-extensie-rollen ophalen",
            async clubCode =>
            {
                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();

                var gekoppeld = new Dictionary<string, (string? Door, DateTime? Op, string? Account)>(StringComparer.OrdinalIgnoreCase);
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT rolnaam, laatstgekoppelddoor, laatstgekoppeldop, sportlinkaccountnaam
                    FROM public.sportlinkextensierollen WHERE clubcode = @clubcode", connection))
                {
                    cmd.Parameters.AddWithValue("clubcode", clubCode);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        gekoppeld[reader.GetString(0)] = (
                            reader.IsDBNull(1) ? null : reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3));
                    }
                }

                var result = FunctioneleRollen.Select(rol =>
                {
                    gekoppeld.TryGetValue(rol, out var info);
                    return new
                    {
                        RolNaam = rol,
                        Gekoppeld = info.Op != null,
                        LaatstGekoppeldDoor = info.Door,
                        LaatstGekoppeldOp = info.Op,
                        SportlinkAccountNaam = info.Account
                    };
                });

                return new OkObjectResult(result);
            });

    [Function("SportlinkExtensieRollenPut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/sportlink-extensie/rollen/{rolNaam}")] HttpRequest req,
        string rolNaam,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkExtensieRollenPut"), "sportlink-extensie-rol registreren",
            async clubCode =>
            {
                if (!FunctioneleRollen.Contains(rolNaam, StringComparer.OrdinalIgnoreCase))
                    return new BadRequestObjectResult(new { error = $"Onbekende rol '{rolNaam}'. Toegestaan: {string.Join(", ", FunctioneleRollen)}." });

                var dto = JsonConvert.DeserializeObject<RegistreerKoppelingDto>(
                    await new StreamReader(req.Body).ReadToEndAsync());

                // Server bepaalt WIE — nooit uit client-input, om spoofing te voorkomen.
                var door = EasyAuthHelper.GetCallerName(req) ?? EasyAuthHelper.GetCallerEmail(req) ?? "onbekend";

                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();

                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO public.sportlinkextensierollen
                        (rolnaam, laatstgekoppelddoor, laatstgekoppeldop, sportlinkaccountnaam, clubcode)
                    VALUES (@rolnaam, @door, now(), @account, @clubcode)
                    ON CONFLICT (rolnaam) DO UPDATE SET
                        laatstgekoppelddoor = @door, laatstgekoppeldop = now(), sportlinkaccountnaam = @account",
                    connection);
                cmd.Parameters.AddWithValue("rolnaam", rolNaam);
                cmd.Parameters.AddWithValue("clubcode", clubCode);
                cmd.Parameters.AddWithValue("door", door);
                cmd.Parameters.AddWithValue("account", (object?)dto?.SportlinkAccountNaam ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();

                return new OkObjectResult(new { RolNaam = rolNaam, LaatstGekoppeldDoor = door });
            });

    // #991: registreert het échte refresh_token productie-persistent in
    // public.sportlinkservicetokens. Bewust géén GET-tegenhanger — dit endpoint is write-only.
    [Function("SportlinkExtensieRollenPutToken")]
    public static Task<IActionResult> PutToken(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/sportlink-extensie/rollen/{rolNaam}/token")] HttpRequest req,
        string rolNaam,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkExtensieRollenPutToken"), "sportlink-extensie-token registreren",
            async clubCode =>
            {
                if (!FunctioneleRollen.Contains(rolNaam, StringComparer.OrdinalIgnoreCase))
                    return new BadRequestObjectResult(new { error = $"Onbekende rol '{rolNaam}'. Toegestaan: {string.Join(", ", FunctioneleRollen)}." });

                var dto = JsonConvert.DeserializeObject<RegistreerTokenDto>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                if (string.IsNullOrWhiteSpace(dto?.RefreshToken))
                    return new BadRequestObjectResult(new { error = "refreshToken ontbreekt." });

                try
                {
                    await SportlinkClubTokenProvider.RegisterInitialTokenAsync(
                        PostgresDatabaseConfig.ConnectionString, rolNaam, clubCode, dto.RefreshToken, TokenHttp,
                        context.GetLogger("SportlinkExtensieRollenPutToken"));
                }
                catch (SportlinkTokenVerlopenException)
                {
                    return new ObjectResult(new { error = "Sportlink heeft dit refresh-token geweigerd — controleer of het recent en correct is." }) { StatusCode = 409 };
                }

                return new OkObjectResult(new { RolNaam = rolNaam });
            });

    private class RegistreerKoppelingDto
    {
        public string? SportlinkAccountNaam { get; set; }
    }

    private class RegistreerTokenDto
    {
        public string? RefreshToken { get; set; }
    }
}
