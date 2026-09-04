using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// #988: rol↔serviceaccount-koppelingsstatus voor de Sportlink Web Extension (epic #986). Registreert
/// alleen WIE (uit de X-MS-CLIENT-PRINCIPAL, nooit client-input) en WANNEER een functionele rol een
/// Sportlink-serviceaccount gekoppeld heeft gekregen — geen live Sportlink-verificatie, geen tokens.
/// Zie docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §6 voor de architectuurbeslissing (rol-
/// gebaseerde service-accounts i.p.v. één gedeelde credential).
///
/// Rollenlijst is bewust hardcoded (geen API om Entra App Roles te bevragen vanuit de backend) —
/// zelfde "twee plekken handmatig sync houden"-afweging als de service-accounts zelf.
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
                using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
                await connection.OpenAsync();

                var gekoppeld = new Dictionary<string, (string? Door, DateTime? Op, string? Account)>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SqlCommand(@"
                    SELECT [RolNaam], [LaatstGekoppeldDoor], [LaatstGekoppeldOp], [SportlinkAccountNaam]
                    FROM [dbo].[SportlinkExtensieRollen] WHERE [ClubCode] = @ClubCode", connection))
                {
                    cmd.Parameters.AddWithValue("@ClubCode", clubCode);
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        gekoppeld[reader.GetString(0)] = (
                            reader.IsDBNull(1) ? null : reader.GetString(1),
                            reader.IsDBNull(2) ? null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
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

                using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
                await connection.OpenAsync();

                using var cmd = new SqlCommand(@"
                    MERGE [dbo].[SportlinkExtensieRollen] AS target
                    USING (SELECT @RolNaam AS RolNaam, @ClubCode AS ClubCode) AS src
                        ON target.[RolNaam] = src.RolNaam AND target.[ClubCode] = src.ClubCode
                    WHEN MATCHED THEN UPDATE SET
                        [LaatstGekoppeldDoor] = @Door, [LaatstGekoppeldOp] = SYSUTCDATETIME(), [SportlinkAccountNaam] = @Account
                    WHEN NOT MATCHED THEN INSERT
                        ([RolNaam], [LaatstGekoppeldDoor], [LaatstGekoppeldOp], [SportlinkAccountNaam], [ClubCode])
                        VALUES (@RolNaam, @Door, SYSUTCDATETIME(), @Account, @ClubCode);", connection);
                cmd.Parameters.AddWithValue("@RolNaam", rolNaam);
                cmd.Parameters.AddWithValue("@ClubCode", clubCode);
                cmd.Parameters.AddWithValue("@Door", door);
                cmd.Parameters.AddWithValue("@Account", (object?)dto?.SportlinkAccountNaam ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();

                return new OkObjectResult(new { RolNaam = rolNaam, LaatstGekoppeldDoor = door });
            });

    private class RegistreerKoppelingDto
    {
        public string? SportlinkAccountNaam { get; set; }
    }
}
