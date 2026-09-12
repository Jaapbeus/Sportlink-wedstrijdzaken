using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Token-opslag die refresh tokens leest/schrijft via Azure App Settings.
/// Tokens worden opgeslagen in omgevingsvariabelen van de vorm: SportlinkClubRefreshToken__{functioneleRol}
///
/// Schrijven gebeurt via Azure Management API (vereist Managed Identity met Website Contributor).
/// </summary>
public class SportlinkClubAppSettingsTokenStore : ISportlinkClubTokenStore
{
    private const string ManagementApiVersion = "2022-03-01";
    private readonly ILogger<SportlinkClubAppSettingsTokenStore> _logger;

    public SportlinkClubAppSettingsTokenStore(ILogger<SportlinkClubAppSettingsTokenStore> logger)
    {
        _logger = logger;
    }

    public string? LeesRefreshToken(string functioneleRol)
    {
        var envVarName = $"SportlinkClubRefreshToken__{functioneleRol}";
        return Environment.GetEnvironmentVariable(envVarName);
    }

    public async Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default)
    {
        var subscriptionId = Environment.GetEnvironmentVariable("AzureSubscriptionId");
        var resourceGroup = Environment.GetEnvironmentVariable("AzureResourceGroupName");
        var functionAppName = Environment.GetEnvironmentVariable("AzureFunctionAppName");

        if (string.IsNullOrWhiteSpace(subscriptionId) ||
            string.IsNullOrWhiteSpace(resourceGroup) ||
            string.IsNullOrWhiteSpace(functionAppName))
        {
            _logger.LogWarning(
                "Azure Management env vars niet geconfigureerd (AzureSubscriptionId / AzureResourceGroupName / AzureFunctionAppName) — " +
                "refresh token bijwerken via API overgeslagen. Lokale omgeving?");
            return;
        }

        try
        {
            var credential = new DefaultAzureCredential();
            var tokenContext = new TokenRequestContext(["https://management.azure.com/.default"]);
            var token = await credential.GetTokenAsync(tokenContext, cancellationToken);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            var baseUrl = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                          $"/resourceGroups/{resourceGroup}" +
                          $"/providers/Microsoft.Web/sites/{functionAppName}";

            // Haal huiconstante app settings op
            var listResponse = await http.PostAsync(
                $"{baseUrl}/config/appsettings/list?api-version={ManagementApiVersion}",
                null,
                cancellationToken);
            listResponse.EnsureSuccessStatusCode();
            var listJson = await listResponse.Content.ReadAsStringAsync(cancellationToken);
            var listObj = JObject.Parse(listJson);

            var properties = new Dictionary<string, string?>();
            var existingProps = listObj["properties"] as JObject;
            if (existingProps != null)
            {
                foreach (var prop in existingProps.Properties())
                    properties[prop.Name] = prop.Value.ToString();
            }

            // Bijwerken refresh token
            var envVarName = $"SportlinkClubRefreshToken__{functioneleRol}";
            properties[envVarName] = nieuwRefreshToken;

            var putBody = System.Text.Json.JsonSerializer.Serialize(new { properties });
            var putResponse = await http.PutAsync(
                $"{baseUrl}/config/appsettings?api-version={ManagementApiVersion}",
                new StringContent(putBody, System.Text.Encoding.UTF8, "application/json"),
                cancellationToken);
            putResponse.EnsureSuccessStatusCode();

            _logger.LogInformation("SportlinkClubRefreshToken__{Rol} bijgewerkt via Azure Management API", functioneleRol);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fout bij bijwerken refresh token via Azure Management API voor rol '{Rol}'", functioneleRol);
            // GEEN exception doorwerpen — de aanroeper moet doorgaan met het huiconstante token
        }
    }
}
