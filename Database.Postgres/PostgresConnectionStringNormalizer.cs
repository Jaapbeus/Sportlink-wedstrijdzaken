using Npgsql;

namespace Database.Postgres;

/// <summary>
/// Resultaat van <see cref="PostgresConnectionStringNormalizer.NormalizeWithDiagnostics"/>:
/// de genormaliseerde connectiestring, de TLS-modus die daadwerkelijk gaat gelden, en — als het
/// TLS-beleid niet volledig gehaald wordt — een waarschuwing die veilig te tonen is (bevat nooit
/// host, gebruiker of wachtwoord; <c>/api/health</c> is anoniem toegankelijk).
/// </summary>
public sealed record PostgresConnectionNormalization(
    string ConnectionString,
    SslMode EffectiveSslMode,
    string? TlsWarning);

/// <summary>
/// Normaliseert een Postgres-connectiestring naar de vorm die <see cref="NpgsqlConnection"/>
/// rechtstreeks accepteert, en past daarbij het TLS-beleid van #1004/#1095 toe. Supabase's dashboard
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
/// tegen een aanvaller die zich als het database-endpoint voordoet (MITM). Sinds #1004 worden
/// <c>sslmode</c>/<c>sslrootcert</c> uit de URI-query wél vertaald, en geldt voor beide vormen
/// hetzelfde beleid, op basis van de daadwerkelijk benaderde host — niet van het aanroepende proces:
/// <list type="bullet">
/// <item>Host is een van de lokale-ontwikkelhosts (<see cref="LocalDevelopmentHosts"/>) — exact de
/// hosts uit <c>docker-compose.yml</c>, <c>docs/DEVELOPER-SETUP.md</c> §7.2 en de CI-job
/// <c>fresh-db-postgres</c>. Geen TLS-eis: de officiële <c>postgres</c>-image draait zonder TLS,
/// dus Npgsql's default (<see cref="SslMode.Prefer"/>) valt terug op een onversleutelde verbinding
/// zoals vandaag al het geval is.</item>
/// <item>Elke andere host — per definitie een productie- of stagingdatabase — hoort op
/// <see cref="SslMode.VerifyFull"/> te staan. Er wordt nergens een callback toegevoegd die elk
/// certificaat accepteert.</item>
/// </list>
/// </para>
/// <para>
/// <b>#1095 — fail-open met waarschuwing in plaats van fail-closed.</b> #1004 gooide voor een
/// niet-lokale host zonder <c>verify-full</c> een <see cref="InvalidOperationException"/> vóór de
/// eerste verbinding. Omdat <c>PostgresDatabaseConfig.ConnectionString</c> een static initializer
/// is, legde dat bij release v3.3.0.0 de complete productie-Function App plat (elke databasetoegang
/// faalde; <c>/api/health</c> gaf aanhoudend 503) — de bestaande productie-instelling had geen
/// <c>?sslmode=</c>, en vóór #1004 werd daar stilzwijgend <c>Require</c> van gemaakt. Bovendien
/// vereist <c>verify-full</c> bij de gebruikte hostingprovider het eigen CA-certificaat van die
/// provider (<c>sslrootcert</c>), zodat alleen de modus omzetten een certificaatketen-fout geeft.
/// Daarom nu:
/// <list type="bullet">
/// <item><see cref="SslMode.Prefer"/> (Npgsql's default, dus "niet opgegeven") wordt voor een
/// niet-lokale host opgewaardeerd naar <see cref="SslMode.Require"/> — de productiestand van vóór
/// v3.3.0.0, nooit zwakker dan voorheen — met een <see cref="PostgresConnectionNormalization.TlsWarning"/>.</item>
/// <item><see cref="SslMode.Require"/> en <see cref="SslMode.VerifyCA"/> blijven staan, met dezelfde
/// waarschuwing (versleuteld, maar certificaat resp. hostnaam niet gevalideerd).</item>
/// <item><see cref="SslMode.Disable"/> en <see cref="SslMode.Allow"/> op een niet-lokale host blijven
/// geweigerd: dat is een expliciete keuze voor onversleuteld verkeer naar een productiedatabase en is
/// nooit een geldige configuratie geweest.</item>
/// </list>
/// De waarschuwing wordt door <c>/api/health</c> (<c>tlsWarning</c>) en het functielog zichtbaar
/// gemaakt, zodat de beheerder de instelling kan aanvullen zonder dat de applicatie eerst uitvalt.
/// De ruwe connectiestring wordt hier nooit gelogd (bevat wachtwoorden), en de waarschuwing bevat
/// bewust ook geen hostnaam.
/// </para>
/// </summary>
public static class PostgresConnectionStringNormalizer
{
    private static readonly string[] LocalDevelopmentHosts = { "localhost", "127.0.0.1", "::1" };

    public static string Normalize(string raw) => NormalizeWithDiagnostics(raw).ConnectionString;

    public static PostgresConnectionNormalization NormalizeWithDiagnostics(string raw)
    {
        var builder = IsUriForm(raw) ? BuildFromUri(raw) : new NpgsqlConnectionStringBuilder(raw);

        ValidateNoContradictorySslOptions(builder);
        var tlsWarning = ApplyTlsPolicy(builder);

        return new PostgresConnectionNormalization(builder.ConnectionString, builder.SslMode, tlsWarning);
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
    /// Kernbeleid van #1004, met de #1095-correctie: voor een niet-lokale host is
    /// <see cref="SslMode.VerifyFull"/> de norm, maar een zwakkere-maar-versleutelde modus wordt
    /// gemeld in plaats van geweigerd. Alleen een expliciete keuze voor onversleuteld verkeer
    /// (<see cref="SslMode.Disable"/>/<see cref="SslMode.Allow"/>) blijft een fout. Geldt identiek
    /// voor URI- en keyword/value-vorm, en dus voor elke aanroeper (Function App-configuratielaag,
    /// <c>Database.Postgres.Cli</c>, <c>MigrationTools/SqlServerToPostgresCopy</c>).
    /// </summary>
    /// <returns>De waarschuwing (zonder host of credentials), of <c>null</c> als het beleid volledig gehaald is.</returns>
    private static string? ApplyTlsPolicy(NpgsqlConnectionStringBuilder builder)
    {
        if (IsLocalDevelopmentHost(builder.Host))
            return null;

        switch (builder.SslMode)
        {
            case SslMode.VerifyFull:
                return null;

            case SslMode.Disable:
            case SslMode.Allow:
                throw new InvalidOperationException(
                    $"Postgres-verbinding naar een niet-lokale host met SslMode='{builder.SslMode}' is niet " +
                    "toegestaan: dat kiest expliciet voor onversleuteld verkeer naar een productie- of " +
                    "stagingdatabase (#1004). Geef ?sslmode=verify-full mee in de URI, of 'SSL Mode=VerifyFull' " +
                    $"in de keyword/value-connectiestring. Alleen {string.Join(", ", LocalDevelopmentHosts)} " +
                    "gelden als lokale ontwikkelomgeving.");

            case SslMode.Prefer:
                // Npgsql's default — de modus is niet opgegeven. Opwaarderen naar Require: dat was
                // vóór #1004 al het gedrag voor de URI-vorm (productiestand tot v3.3.0.0), en voor
                // de keyword/value-vorm is het strikt sterker dan wat er stond.
                builder.SslMode = SslMode.Require;
                return BuildWarning(builder.SslMode, "de sslmode is niet opgegeven en is opgewaardeerd naar Require");

            case SslMode.Require:
                return BuildWarning(builder.SslMode, "het servercertificaat en de hostnaam worden niet gevalideerd");

            case SslMode.VerifyCA:
                return BuildWarning(builder.SslMode, "de certificaatketen wordt gevalideerd, maar de hostnaam niet");

            default:
                return BuildWarning(builder.SslMode, "onbekende TLS-modus");
        }
    }

    private static string BuildWarning(SslMode effective, string reden) =>
        $"TLS-beleid niet volledig gehaald: verbinding is versleuteld (SslMode={effective}), maar {reden}. " +
        "Zet sslmode=verify-full met het CA-certificaat van de databaseprovider (sslrootcert) — zie " +
        "docs/ARCHITECTUUR-DATABASE-TIERS.md §50 en issue #1095.";

    private static bool IsLocalDevelopmentHost(string? host) =>
        !string.IsNullOrEmpty(host) &&
        LocalDevelopmentHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
}
