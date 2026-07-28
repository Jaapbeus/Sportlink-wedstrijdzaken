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
    private static readonly ConcurrentDictionary<(string clubCode, string key), (EmailTemplate template, DateTime expiresAt)> _cache = new();
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
    private static readonly object _lock = new();

    /// <summary>
    /// Probeert een template op te halen uit de database. Retourneert null als de template
    /// niet bestaat of niet actief is — in dat geval valt de caller terug op hardcoded defaults.
    /// </summary>
    /// <param name="clubCode">
    /// Expliciete club-override (#677/#706): het dry-run pad van de Email-tester geeft hier de in
    /// de GUI geselecteerde club mee. <c>null</c> betekent de primaire club van deze deployment —
    /// het bestaande gedrag van de echte e-mailpipeline, die geen club-switcher heeft.
    /// </param>
    public static async Task<EmailTemplate?> GetTemplateAsync(string key, string? clubCode = null, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            // Eerst de club resolven, dan pas de cache raadplegen: de club hoort in de
            // cachesleutel (zie TryGetCached).
            var cc = SystemUtilities.AppSettings.RequireClubCode(clubCode);

            if (TryGetCached(key, cc, out var cached)) return cached;

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT TOP 1 [TemplateKey], [Onderwerp], [BodyTemplate]
                FROM [dbo].[EmailTemplateInstellingen]
                WHERE [TemplateKey] = @Key AND [ClubCode] = @ClubCode AND [Actief] = 1", connection);
            command.Parameters.AddWithValue("@Key", key);
            command.Parameters.AddWithValue("@ClubCode", cc);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var template = new EmailTemplate(
                    reader["TemplateKey"].ToString() ?? key,
                    reader["Onderwerp"].ToString() ?? "",
                    reader["BodyTemplate"].ToString() ?? ""
                );
                StoreInCache(key, cc, template);
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
    /// Cache-lookup met TTL-controle. De sleutel is (club, key) en niet alleen key: een deployment
    /// bevat naast de productieclub ook de democlub, dus met alleen de key krijgt de tweede club de
    /// template van de eerste die hem ophaalde — data van een andere club in haar eigen antwoord (#706).
    /// </summary>
    internal static bool TryGetCached(string key, string clubCode, out EmailTemplate? template)
    {
        template = null;
        if (!_cache.TryGetValue((clubCode, key), out var cached) || cached.expiresAt <= DateTime.UtcNow)
            return false;

        template = cached.template;
        return true;
    }

    internal static void StoreInCache(string key, string clubCode, EmailTemplate template)
        => _cache[(clubCode, key)] = (template, DateTime.UtcNow.Add(_cacheTtl));

    /// <summary>
    /// Invalideert de hele template-cache — alle clubs. Aanroepen na admin-update via
    /// PUT /api/beheer/templates.
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
