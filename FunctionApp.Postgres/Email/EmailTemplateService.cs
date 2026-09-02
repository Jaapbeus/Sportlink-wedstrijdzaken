using System.Collections.Concurrent;
using FunctionApp.Postgres.Planner;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/EmailTemplateService.cs</c> (#889) — het
/// laatste bestand uit dat issue se scope-omschrijving met directe databasetoegang.
/// Laadt e-mailsjablonen uit <c>public.emailtemplateinstellingen</c> en cacht ze statisch met een
/// TTL van vijf minuten.
///
/// <para>
/// <b>Vertaling:</b> <c>SELECT TOP 1 ... WHERE [Actief] = 1</c> →
/// <c>SELECT ... WHERE actief = TRUE LIMIT 1</c>; <c>SystemUtilities.AppSettings.RequireClubCode</c>
/// → <see cref="PostgresClubScope.Resolve"/>. Geen <c>UPPER(...)</c>-wrap hier, in tegenstelling tot
/// de teamresolutie (#820): <c>templatekey</c> en <c>clubcode</c> worden door de applicatie zelf
/// gezet (de Beheer-GUI en <c>BerichtResponseGenerator</c>'s vaste sleutels), niet door een externe
/// bron aangeleverd — zelfde onderscheid als in §25 tussen
/// <c>planner.geplandewedstrijden.status</c> en <c>his.matches.status</c>.
/// </para>
///
/// <para>
/// <b>De cachesleutel is (clubcode, key) en niet alleen key.</b> Dat is geen optimalisatie maar een
/// correctness-eis uit #706: een deployment bevat naast de productieclub ook de democlub, dus met
/// alleen de sleutel krijgt de tweede club het sjabloon van de eerste die het ophaalde — data van
/// een andere club in haar eigen antwoord.
/// </para>
///
/// <para>
/// <b>Bekende stand op deze tier (bijgewerkt, #889-vervolg):</b> <see cref="InvalidateCache"/>
/// wordt aangeroepen door <c>AdminTemplatesFunction</c>. <see cref="GetTemplateAsync"/> heeft
/// inmiddels wél een productieconsument: <c>BerichtPipeline.BouwTemplateAntwoord</c> roept hem aan
/// voor elk classificatietype (dry-run pad via <c>EmailTestFunction</c>) — dezelfde route als op de
/// SQL Server-tier. De opmerking hierboven dat dit "nog niet vertaald" zou zijn is achterhaald
/// sinds die PR; laten staan als geschiedenis zou een lezer op het verkeerde been zetten.
/// </para>
/// </summary>
public static class EmailTemplateService
{
    private static readonly ConcurrentDictionary<(string clubCode, string key), (EmailTemplate template, DateTime expiresAt)> _cache = new();
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Probeert een sjabloon op te halen uit de database. Retourneert <c>null</c> als het sjabloon
    /// niet bestaat of niet actief is — de aanroeper valt dan terug op de hardcoded defaults,
    /// exact zoals op de SQL Server-tier.
    /// </summary>
    /// <param name="clubCode">
    /// Expliciete club-override (#677/#706). <c>null</c> betekent de primaire club van deze
    /// deployment.
    /// </param>
    public static async Task<EmailTemplate?> GetTemplateAsync(
        string key, string? clubCode = null, ILogger? log = null)
        => await GetTemplateAsync(PostgresDatabaseConfig.ConnectionString, key, clubCode, log);

    /// <summary>
    /// Overload met expliciete connectiestring — nodig om dit gedrag tegen een wegwerpcontainer te
    /// kunnen meten zonder de procesbrede configuratie te zetten. Zelfde patroon als de overige
    /// repositories op deze tier, die de connectiestring altijd als parameter krijgen.
    /// </summary>
    internal static async Task<EmailTemplate?> GetTemplateAsync(
        string connectionString, string key, string? clubCode = null, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            // Eerst de club resolven, dan pas de cache raadplegen: de club hoort in de cachesleutel.
            var cc = PostgresClubScope.Resolve(clubCode);

            if (TryGetCached(key, cc, out var cached)) return cached;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(@"
                SELECT templatekey, onderwerp, bodytemplate
                FROM public.emailtemplateinstellingen
                WHERE templatekey = @key AND clubcode = @clubcode AND actief = TRUE
                LIMIT 1", connection);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("clubcode", cc);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var template = new EmailTemplate(
                    reader.IsDBNull(0) ? key : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2));
                StoreInCache(key, cc, template);
                return template;
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex,
                "EmailTemplateService: kon sjabloon {Key} niet laden — terugval op hardcoded default", key);
        }

        return null;
    }

    /// <summary>Cache-lookup met TTL-controle. Zie de klasse-doc-comment voor waarom de club in de sleutel zit.</summary>
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
    /// Invalideert de hele sjabloon-cache — alle clubs. Aangeroepen na een admin-wijziging via
    /// <c>PUT/DELETE /api/beheer/templates</c>.
    /// </summary>
    public static void InvalidateCache()
    {
        _cache.Clear();
    }

    /// <summary>Past een sjabloon toe met simpele placeholder-substitutie (<c>{{key}}</c>).</summary>
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
/// Eenvoudig sjabloon-record voor e-mailuitvoer. Tier-lokale kopie van het gelijknamige type in
/// <c>FunctionApp/Email/EmailTemplateService.cs</c> — pure databagger zonder gedrag, dus geen
/// architectuurbezwaar tegen een tweede definitie (zelfde redenering als
/// <c>FunctionApp.Postgres/Email/EmailModels.cs</c>).
/// </summary>
public record EmailTemplate(string Key, string Onderwerp, string Body);
