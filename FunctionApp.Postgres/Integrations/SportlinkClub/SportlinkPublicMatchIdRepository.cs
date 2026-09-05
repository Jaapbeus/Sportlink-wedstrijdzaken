using Npgsql;

namespace FunctionApp.Postgres.Integrations.SportlinkClub;

/// <summary>Interne wedstrijdgegevens nodig voor de #987-reverse-lookup — niet de volledige
/// <c>his.matches</c>-rij, alleen wat <see cref="SportlinkClubClient.ResolvePublicMatchIdAsync"/>
/// nodig heeft.</summary>
internal sealed record WedstrijdVoorLookup(long Wedstrijdnummer, DateOnly Datum);

/// <summary>DB-toegang voor de PublicMatchId-cache (#991, epic #986). Cachet het resultaat van de
/// trage (12+ s), niet-club-gescoped reverse-lookup in een eigen tabel — <b>niet</b> als kolom op
/// <c>his.matches</c>, want die tabel wordt dynamisch beheerd door
/// <c>Database.Postgres/PostgresSchemaGenerator</c> (#818) vanuit het ETL-gesynchroniseerde
/// <c>Match</c>-DTO; een eigen cache-kolom daarin zou onze cache-data vermengen met
/// Sportlink-gesynchroniseerde data.</summary>
internal static class SportlinkPublicMatchIdRepository
{
    /// <summary>Zoekt <c>wedstrijdnummer</c>/<c>kaledatum</c> op via onze eigen <c>wedstrijdcode</c>
    /// (issue #991's externe sleutel). <c>kaledatum</c> i.p.v. <c>wedstrijddatum</c>: dat laatste is
    /// een vrije Sportlink-weergavestring, <c>kaledatum</c> is al een parseerbare datum (zelfde
    /// keuze als <c>PlannerMatchRepository.FindMatchByCodeAsync</c>).</summary>
    internal static async Task<WedstrijdVoorLookup?> ZoekWedstrijdAsync(
        NpgsqlConnection connection, long wedstrijdcode, string clubCode)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT wedstrijdnummer, kaledatum::date
            FROM his.matches
            WHERE wedstrijdcode = @wedstrijdcode AND clubcode = @clubcode",
            connection);
        cmd.Parameters.AddWithValue("wedstrijdcode", wedstrijdcode);
        cmd.Parameters.AddWithValue("clubcode", clubCode);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        // wedstrijdnummer/kaledatum kunnen NULL zijn op een onvolledig gesynchroniseerde rij —
        // dan is de reverse-lookup niet mogelijk, geen crash.
        if (reader.IsDBNull(0) || reader.IsDBNull(1)) return null;

        var wedstrijdnummer = reader.GetInt64(0);
        var datum = DateOnly.FromDateTime(reader.GetDateTime(1));
        return new WedstrijdVoorLookup(wedstrijdnummer, datum);
    }

    internal static async Task<string?> LeesUitCacheAsync(NpgsqlConnection connection, long wedstrijdcode, string clubCode)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT publicmatchid FROM public.sportlinkpublicmatchidcache WHERE wedstrijdcode = @wedstrijdcode AND clubcode = @clubcode",
            connection);
        cmd.Parameters.AddWithValue("wedstrijdcode", wedstrijdcode);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    internal static async Task SchrijfInCacheAsync(
        NpgsqlConnection connection, long wedstrijdcode, string clubCode, string publicMatchId)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.sportlinkpublicmatchidcache (wedstrijdcode, clubcode, publicmatchid, opgehaaldop)
            VALUES (@wedstrijdcode, @clubcode, @publicmatchid, now())
            ON CONFLICT (wedstrijdcode, clubcode) DO UPDATE SET
                publicmatchid = @publicmatchid, opgehaaldop = now()",
            connection);
        cmd.Parameters.AddWithValue("wedstrijdcode", wedstrijdcode);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("publicmatchid", publicMatchId);
        await cmd.ExecuteNonQueryAsync();
    }
}
