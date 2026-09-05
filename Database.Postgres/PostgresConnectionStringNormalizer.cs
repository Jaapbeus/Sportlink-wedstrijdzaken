using Npgsql;

namespace Database.Postgres;

/// <summary>
/// Normaliseert een Postgres-connectiestring naar de vorm die <see cref="NpgsqlConnection"/>
/// rechtstreeks accepteert, en dwingt daarbij het TLS-beleid van #1004 af. Supabase's dashboard
/// toont de URI-vorm (<c>postgresql://gebruiker:wachtwoord@host:5432/database</c>) prominenter dan
/// de keyword=value-vorm die Npgsql verwacht — beide zijn een voor de hand liggende keuze om te
/// kopiëren, en Npgsql accepteert alleen de tweede rechtstreeks. Ontdekt tijdens de eerste
/// daadwerkelijke productiecutover-poging (#976): elke plek die zelf een
/// <see cref="NpgsqlConnection"/> opent met een door de gebruiker aangeleverde connectiestring
/// moet hier eerst doorheen, niet alleen <c>MigrationTools/SqlServerToPostgresCopy</c>.
/// <para>
/// <b>#1004 — TLS-beleid.</b> De oorspronkelijke implementatie negeerde elke <c>sslmode</c>-optie
/// uit de URI-query en zette altijd <see cref="SslMode.Require"/> — een modus die sinds Npgsql 8
/// geen certificaatketen of hostnaam meer valideert (zie
/// <see href="https://www.npgsql.org/doc/release-notes/8.0.html"/>), en dus geen bescherming biedt
/// tegen een aanvaller die zich als het database-endpoint voordoet (MITM). Dit type maakt nu
/// onderscheid tussen twee gevallen, consistent met hoe <c>EgressGuard</c> lokaal van productie
/// onderscheidt (env-gebaseerd) maar toegepast op de vraag die hier daadwerkelijk telt — welke
/// server wordt benaderd, niet welk proces het aanroept:
/// <list type="bullet">
/// <item>Host is een van de lokale-ontwikkelhosts (<see cref="LocalDevelopmentHosts"/>) — exact de
/// hosts uit <c>docker-compose.yml</c>, <c>docs/DEVELOPER-SETUP.md</c> §7.2 en de CI-job
/// <c>fresh-db-postgres</c>. Geen TLS-eis: de officiële <c>postgres:16</c>-image draait zonder TLS,
/// dus Npgsql's default (<see cref="SslMode.Prefer"/>) valt terug op een onversleutelde verbinding
/// zoals vandaag al het geval is.</item>
/// <item>Elke andere host — dit is per definitie een productie- of stagingdatabase — vereist
/// expliciet <see cref="SslMode.VerifyFull"/>. Ontbreekt dat, dan gooit <see cref="Normalize"/> een
/// <see cref="InvalidOperationException"/> vóór er ook maar een verbinding wordt geopend. Er wordt
/// nergens een callback toegevoegd die elk certificaat accepteert.</item>
/// </list>
/// Dit beleid geldt identiek voor de URI-vorm én de keyword/value-vorm — beide gaan door
/// <see cref="EnforceTlsPolicy"/>. De ruwe connectiestring wordt hier nooit gelogd (bevat
/// wachtwoorden).
/// </para>
/// </summary>
public static class PostgresConnectionStringNormalizer
{
    private static readonly string[] LocalDevelopmentHosts = { "localhost", "127.0.0.1", "::1" };

    public static string Normalize(string raw)
    {
        var builder = IsUriForm(raw) ? BuildFromUri(raw) : new NpgsqlConnectionStringBuilder(raw);

        ValidateNoContradictorySslOptions(builder);
        EnforceTlsPolicy(builder);

        return builder.ConnectionString;
    }

    private static bool IsUriForm(string raw) =>
        raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    private static NpgsqlConnectionStringBuilder BuildFromUri(string raw)
    {
        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var database = uri.AbsolutePath.TrimStart('/');
        var port = uri.Port > 0 ? uri.Port : 5432;

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = port,
            Database = string.IsNullOrEmpty(database) ? "postgres" : database,
            Username = username,
            Password = password,
        };

        ApplySslOptionsFromQuery(uri.Query, builder);
        return builder;
    }

    /// <summary>
    /// Vertaalt de ondersteunde libpq-achtige TLS-queryopties (<c>sslmode</c>, <c>sslrootcert</c>)
    /// naar hun Npgsql-equivalent. Elke andere querysleutel wordt bewust genegeerd in plaats van
    /// stilzwijgend doorgezet — een onbekende sleutel mag nooit als beveiligingsoptie worden
    /// geïnterpreteerd.
    /// </summary>
    private static void ApplySslOptionsFromQuery(string query, NpgsqlConnectionStringBuilder builder)
    {
        foreach (var (key, value) in ParseQueryString(query))
        {
            switch (key.ToLowerInvariant())
            {
                case "sslmode":
                    builder.SslMode = ParseSslMode(value);
                    break;
                case "sslrootcert":
                    builder.RootCertificate = value;
                    break;
            }
        }
    }

    private static IEnumerable<(string Key, string Value)> ParseQueryString(string query)
    {
        if (string.IsNullOrEmpty(query))
            yield break;

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            yield return (key, value);
        }
    }

    private static SslMode ParseSslMode(string value) => value.ToLowerInvariant() switch
    {
        "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "require" => SslMode.Require,
        "verify-ca" => SslMode.VerifyCA,
        "verify-full" => SslMode.VerifyFull,
        _ => throw new InvalidOperationException(
            $"Onbekende of niet-ondersteunde sslmode-waarde in de connectiestring: '{value}'. " +
            "Ondersteund: disable, allow, prefer, require, verify-ca, verify-full."),
    };

    /// <summary>
    /// Wijst combinaties af die op een misverstand wijzen: een root-CA-certificaat heeft alleen
    /// betekenis wanneer Npgsql de certificaatketen daadwerkelijk valideert (<c>VerifyCA</c>/
    /// <c>VerifyFull</c>). Bij <c>Disable</c>/<c>Allow</c>/<c>Prefer</c>/<c>Require</c> wordt het
    /// stilzwijgend genegeerd door Npgsql, wat een beheerder ten onrechte kan doen denken dat
    /// certificaatvalidatie actief is.
    /// </summary>
    private static void ValidateNoContradictorySslOptions(NpgsqlConnectionStringBuilder builder)
    {
        if (!string.IsNullOrEmpty(builder.RootCertificate) &&
            builder.SslMode is not (SslMode.VerifyCA or SslMode.VerifyFull))
        {
            throw new InvalidOperationException(
                $"Tegenstrijdige TLS-configuratie: er is een RootCertificate ('{builder.RootCertificate}') " +
                $"opgegeven, maar SslMode staat op '{builder.SslMode}' — dat certificaat wordt dan nooit " +
                "gebruikt om de serverketen te valideren. Zet sslmode op verify-ca of verify-full.");
        }
    }

    /// <summary>
    /// Kernbeleid van #1004: certificaatvalidatie is verplicht zodra de host niet de lokale
    /// ontwikkelomgeving is. Geldt identiek voor URI- en keyword/value-vorm, en dus ook voor elke
    /// aanroeper (Function App-configuratielaag, <c>Database.Postgres.Cli</c>,
    /// <c>MigrationTools/SqlServerToPostgresCopy</c>).
    /// </summary>
    private static void EnforceTlsPolicy(NpgsqlConnectionStringBuilder builder)
    {
        if (IsLocalDevelopmentHost(builder.Host))
            return;

        if (builder.SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException(
                $"Postgres-verbinding naar host '{builder.Host}' vereist SslMode=VerifyFull " +
                $"(huidige waarde: '{builder.SslMode}'). Certificaatvalidatie mag voor een " +
                "niet-lokale host nooit worden verzwakt (#1004) — geef ?sslmode=verify-full mee in " +
                "de URI, of 'SSL Mode=VerifyFull' in de keyword/value-connectiestring. Alleen " +
                $"{string.Join(", ", LocalDevelopmentHosts)} gelden als lokale ontwikkelomgeving.");
    }

    private static bool IsLocalDevelopmentHost(string? host) =>
        !string.IsNullOrEmpty(host) &&
        LocalDevelopmentHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
}
