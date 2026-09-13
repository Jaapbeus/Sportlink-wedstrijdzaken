using FunctionApp.Postgres.Sportlink;
using Npgsql;

namespace FunctionApp.Postgres.Integrations.SportlinkClub;

/// <summary>Interne wedstrijdgegevens nodig voor de #987-reverse-lookup — niet de volledige
/// <c>his.matches</c>-rij, alleen wat <see cref="SportlinkClubClient.ResolvePublicMatchIdAsync"/>
/// nodig heeft.</summary>
internal sealed record WedstrijdVoorLookup(long Wedstrijdnummer, DateOnly Datum);

/// <summary>Eén rij zonder cache-entry, gebruikt door de #1017-warmup-timer — bevat ook
/// <c>Wedstrijdcode</c> (de eigen sleutel om straks in de cache te schrijven), in tegenstelling tot
/// <see cref="WedstrijdVoorLookup"/> dat alleen is wat de Sportlink-aanroep zelf nodig heeft.</summary>
internal sealed record WedstrijdZonderCache(long Wedstrijdcode, long Wedstrijdnummer, DateOnly Datum);

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

    /// <summary>Alle eigen wedstrijden binnen <paramref name="vanaf"/>–<paramref name="totEnMet"/>
    /// (inclusief) die nog geen `PublicMatchId` in de cache hebben — gebruikt door de #1017-warmup-
    /// timer om te bepalen wat er nog opgehaald moet worden vóórdat een gebruiker er zelf naar
    /// vraagt.</summary>
    internal static async Task<List<WedstrijdZonderCache>> ZoekWedstrijdenZonderCacheAsync(
        NpgsqlConnection connection, DateOnly vanaf, DateOnly totEnMet, string clubCode)
    {
        var resultaat = new List<WedstrijdZonderCache>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT m.wedstrijdcode, m.wedstrijdnummer, m.kaledatum::date
            FROM his.matches m
            LEFT JOIN public.sportlinkpublicmatchidcache c
                ON c.wedstrijdcode = m.wedstrijdcode AND c.clubcode = m.clubcode
            WHERE m.clubcode = @clubcode
              AND m.kaledatum::date BETWEEN @vanaf AND @totEnMet
              AND m.wedstrijdnummer IS NOT NULL
              AND c.wedstrijdcode IS NULL",
            connection);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("vanaf", vanaf.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("totEnMet", totEnMet.ToDateTime(TimeOnly.MinValue));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            resultaat.Add(new WedstrijdZonderCache(
                reader.GetInt64(0),
                reader.GetInt64(1),
                DateOnly.FromDateTime(reader.GetDateTime(2))));
        }
        return resultaat;
    }

    /// <summary>
    /// #1111: de omgekeerde richting van de cache — van Sportlinks <c>PublicMatchId</c> naar onze
    /// eigen wedstrijd in <c>his.matches</c>, in één query voor alle verzoeken van
    /// <c>/wijzigingsverzoeken</c>. Alleen wedstrijden die al in de cache staan (warmup-timer #1017
    /// of een eerdere paneel-lookup) worden gevonden; de rest ontbreekt gewoon in het resultaat.
    /// <c>kaledatum</c> i.p.v. <c>wedstrijddatum</c> om dezelfde reden als
    /// <see cref="ZoekWedstrijdAsync"/>. Bestaat <c>his.matches</c> nog niet (verse database, nog
    /// nooit gesynchroniseerd — de tabel is dynamisch, #818), dan is het antwoord leeg, geen fout.
    /// </summary>
    internal static async Task<Dictionary<string, SportlinkWedstrijdContext>> ZoekWedstrijdenBijPublicMatchIdsAsync(
        NpgsqlConnection connection, IReadOnlyCollection<string> publicMatchIds, string clubCode)
    {
        var resultaat = new Dictionary<string, SportlinkWedstrijdContext>(StringComparer.Ordinal);
        if (publicMatchIds.Count == 0) return resultaat;

        await using var cmd = new NpgsqlCommand(@"
            SELECT c.publicmatchid, m.wedstrijdcode, m.wedstrijdnummer, m.thuisteam, m.uitteam,
                   to_char(m.kaledatum::date, 'YYYY-MM-DD'), m.aanvangstijd, m.accommodatie
            FROM public.sportlinkpublicmatchidcache c
            JOIN his.matches m ON m.wedstrijdcode = c.wedstrijdcode AND m.clubcode = c.clubcode
            WHERE c.clubcode = @clubcode
              AND c.publicmatchid = ANY(@ids)",
            connection);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("ids", publicMatchIds.ToArray());

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var publicMatchId = reader.GetString(0);
                resultaat[publicMatchId] = new SportlinkWedstrijdContext(
                    Wedstrijdcode: reader.GetInt64(1),
                    Wedstrijdnummer: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    Thuisteam: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Uitteam: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Datum: reader.IsDBNull(5) ? null : reader.GetString(5),
                    Tijd: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Accommodatie: reader.IsDBNull(7) ? null : reader.GetString(7));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // his.matches bestaat pas na de eerste sync — geen context, geen fout.
        }
        return resultaat;
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
