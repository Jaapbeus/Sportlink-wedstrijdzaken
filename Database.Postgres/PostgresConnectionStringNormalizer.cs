using Npgsql;

namespace Database.Postgres;

/// <summary>
/// Normaliseert een Postgres-connectiestring naar de vorm die <see cref="NpgsqlConnection"/>
/// rechtstreeks accepteert. Supabase's dashboard toont de URI-vorm
/// (<c>postgresql://gebruiker:wachtwoord@host:5432/database</c>) prominenter dan de
/// keyword=value-vorm die Npgsql verwacht — beide zijn een voor de hand liggende keuze om te
/// kopiëren, en Npgsql accepteert alleen de tweede rechtstreeks. Ontdekt tijdens de eerste
/// daadwerkelijke productiecutover-poging (#976): elke plek die zelf een
/// <see cref="NpgsqlConnection"/> opent met een door de gebruiker aangeleverde connectiestring
/// moet hier eerst doorheen, niet alleen <c>MigrationTools/SqlServerToPostgresCopy</c>.
/// </summary>
public static class PostgresConnectionStringNormalizer
{
    public static string Normalize(string raw)
    {
        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return raw;

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
            SslMode = SslMode.Require,
        };
        return builder.ConnectionString;
    }
}
