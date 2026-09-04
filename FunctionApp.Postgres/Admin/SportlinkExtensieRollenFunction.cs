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

    private class RegistreerKoppelingDto
    {
        public string? SportlinkAccountNaam { get; set; }
    }
}
