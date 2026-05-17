using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

/// <summary>
/// Laadt email-templates uit dbo.EmailTemplateInstellingen.
/// Valt terug op hardcoded defaults (BerichtResponseGenerator) als de tabel leeg is.
/// Cacht templates statisch met TTL = 5 minuten om DB-round-trips te beperken.
/// </summary>
public static class EmailTemplateService
{
    private static readonly ConcurrentDictionary<string, (EmailTemplate template, DateTime expiresAt)> _cache = new();
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
    private static readonly object _lock = new();

    /// <summary>
    /// Probeert een template op te halen uit de database. Retourneert null als de template
    /// niet bestaat of niet actief is — in dat geval valt de caller terug op hardcoded defaults.
    /// </summary>
    public static async Task<EmailTemplate?> GetTemplateAsync(string key, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        // Cache lookup (TTL-gecontroleerd)
        if (_cache.TryGetValue(key, out var cached) && cached.expiresAt > DateTime.UtcNow)
            return cached.template;

        try
        {
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT TOP 1 [TemplateKey], [Onderwerp], [BodyTemplate]
                FROM [dbo].[EmailTemplateInstellingen]
                WHERE [TemplateKey] = @Key AND [ClubCode] = @ClubCode AND [Actief] = 1", connection);
            command.Parameters.AddWithValue("@Key", key);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var template = new EmailTemplate(
                    reader["TemplateKey"].ToString() ?? key,
                    reader["Onderwerp"].ToString() ?? "",
                    reader["BodyTemplate"].ToString() ?? ""
                );
                _cache[key] = (template, DateTime.UtcNow.Add(_cacheTtl));
                return template;
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "EmailTemplateService: kon template {Key} niet laden — terugval op hardcoded default", key);
        }

        return null;
    }

    /// <summary>
    /// Invalideert de hele template-cache. Aanroepen na admin-update via PUT /api/admin/templates.
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cache.Clear();
        }
    }

    /// <summary>
    /// Past een template toe met simpele placeholder-substitutie ({{key}}).
    /// </summary>
    public static string ApplyPlaceholders(string body, IDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(body) || values == null || values.Count == 0) return body;
        foreach (var (key, value) in values)
        {
            body = body.Replace("{{" + key + "}}", value ?? "", StringComparison.OrdinalIgnoreCase);
        }
        return body;
    }
}

/// <summary>
/// Eenvoudig template-record voor email-output.
/// </summary>
public record EmailTemplate(string Key, string Onderwerp, string Body);
