using System.Globalization;
using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using Newtonsoft.Json.Linq;

namespace SportlinkFunction.Monitoring;

/// <summary>
/// Leest de management-plane status van een Azure SQL Database via de Azure Resource Manager REST API
/// (#831). Dit is een ARM-leesoperatie — géén databaseverbinding — dus deze check kan niet zelf
/// slachtoffer worden van dezelfde storing die hij probeert te detecteren.
///
/// <para>
/// Hergebruikt hetzelfde authenticatiepatroon als
/// <c>AdminSettingsFunction.TriggerFunctionAppRestartAsync</c> (<see cref="DefaultAzureCredential"/> +
/// Bearer-token voor <c>management.azure.com</c>): de Function App heeft in productie al een Managed
/// Identity met een roltoewijzing voor die andere ARM-aanroep (Website Contributor op de Function
/// App-resource). Voor déze aanroep is alleen leestoegang op de SQL-server nodig — een Reader-rol,
/// gratis en géén nieuwe Azure-resource, wél een eenmalige extra roltoewijzing op de bestaande
/// identity. Zie docs/MONITORING.md voor de exacte stappen.
/// </para>
/// </summary>
public sealed class ArmDatabaseStatusReader : IDatabaseStatusReader
{
    // Laatste stabiele versie van de Azure SQL Database REST API (2014-04-01 is uitgefaseerd —
    // zie de Microsoft-aankondiging over de retirement van die oudere versie).
    private const string ApiVersion = "2021-11-01";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<DatabaseStatusInfo> LeesStatusAsync(
        string subscriptionId, string resourceGroup, string sqlServerName, string sqlDatabaseName)
    {
        var credential = new DefaultAzureCredential();
        var tokenContext = new TokenRequestContext(["https://management.azure.com/.default"]);
        var token = await credential.GetTokenAsync(tokenContext);

        var url = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                  $"/resourceGroups/{resourceGroup}" +
                  $"/providers/Microsoft.Sql/servers/{sqlServerName}/databases/{sqlDatabaseName}" +
                  $"?api-version={ApiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var obj = JObject.Parse(json);
        var status = obj["properties"]?["status"]?.ToString() ?? "Unknown";
        var pausedDateRaw = obj["properties"]?["pausedDate"]?.ToString();

        DateTime? pausedSinceUtc = null;
        if (!string.IsNullOrWhiteSpace(pausedDateRaw)
            && DateTime.TryParse(
                pausedDateRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            pausedSinceUtc = parsed;
        }

        return new DatabaseStatusInfo(status, pausedSinceUtc);
    }
}
