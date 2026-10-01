using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Endpoints.Sportlink;

/// <summary>Wordt uitsluitend binnen de admin-autorisatiepoort aangeroepen; retourneert nooit secrets.</summary>
public static class SportlinkAutoLoginEndpointCore
{
    public static async Task<IActionResult> ExecuteAsync(HttpRequest request, string clubCode,
        string role, ISportlinkAutoLoginStore? store)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!string.Equals(role, "Wedstrijdzaken", StringComparison.OrdinalIgnoreCase))
            return new BadRequestObjectResult(new { error = "Onbekende functionele rol." });
        role = "Wedstrijdzaken";
        if (store is null)
            return new ObjectResult(new { error = "Automatische login is niet ingericht: de beveiligde hostsleutel ontbreekt." }) { StatusCode = 503 };
        if (!string.Equals(clubCode, store.ClubCode, StringComparison.Ordinal))
            return new ObjectResult(new { error = "Alleen beschikbaar voor de primaire club." }) { StatusCode = 403 };

        var ct = request.HttpContext.RequestAborted;
        try
        {
            await using var lease = await store.AcquireLeaseAsync(role, ct);
            if (HttpMethods.IsPut(request.Method))
            {
                if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
                    return new StatusCodeResult(415);
                var credentials = await ReadCredentialsAsync(request, ct);
                if (credentials is null) return new StatusCodeResult(413);
                if (!ValidCredentials(credentials)) return OngeldigeInvoer();
                // Valideert de instelsleutel lokaal; deze call doet geen login en bewaart geen code.
                _ = SportlinkTotp.Generate(credentials.TotpSecret, DateTimeOffset.UtcNow,
                    credentials.TotpAlgorithm, credentials.TotpDigits, credentials.TotpPeriodSeconds);
                await store.ConfigureAsync(role, credentials, ct);
            }
            else if (HttpMethods.IsDelete(request.Method))
                await store.DeleteCredentialsAsync(role, ct);
            else if (!HttpMethods.IsGet(request.Method))
                return new StatusCodeResult(405);

            var state = await store.ReadAsync(role, ct);
            return new OkObjectResult(new
            {
                Configured = state?.Credentials != null,
                Enabled = state?.Credentials != null && state.FailureCount < 3 &&
                    state.RetryAfterUtc?.Year != 9999,
                state?.LastLoginUtc,
                state?.RetryAfterUtc,
                state?.LastError
            });
        }
        catch (JsonException) { return OngeldigeInvoer(); }
        catch (ArgumentException) { return OngeldigeInvoer(); }
        catch (FormatException) { return OngeldigeInvoer(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Geen exceptionobject loggen: een provider/parser mag credentials in details bevatten.
            return new ObjectResult(new { error = "Beveiligde loginopslag niet beschikbaar; controleer de hostconfiguratie." }) { StatusCode = 503 };
        }
    }

    private static async Task<SportlinkLoginCredentials?> ReadCredentialsAsync(HttpRequest request, CancellationToken ct)
    {
        // Limiet geldt ook voor chunked requests zonder Content-Length, in bytes.
        var buffer = new byte[8193];
        try
        {
            var used = 0;
            while (used < buffer.Length)
            {
                var count = await request.Body.ReadAsync(buffer.AsMemory(used), ct);
                if (count == 0) break;
                used += count;
            }
            if (used > 8192) return null;
            return JsonSerializer.Deserialize<SportlinkLoginCredentials>(buffer.AsSpan(0, used),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
    }

    private static bool ValidCredentials(SportlinkLoginCredentials credentials) =>
        !string.IsNullOrWhiteSpace(credentials.Username) && credentials.Username.Length <= 320 &&
        !string.IsNullOrEmpty(credentials.Password) && credentials.Password.Length <= 1024 &&
        !string.IsNullOrWhiteSpace(credentials.TotpSecret) && credentials.TotpSecret.Length <= 256 &&
        credentials.TotpDigits is 6 or 8 && credentials.TotpPeriodSeconds is 30 or 60 &&
        credentials.TotpAlgorithm is "SHA1" or "SHA256" or "SHA512";

    private static IActionResult OngeldigeInvoer() => new BadRequestObjectResult(new
    { error = "Controleer gebruikersnaam, wachtwoord en authenticator-instelsleutel met de bijbehorende instellingen." });
}
