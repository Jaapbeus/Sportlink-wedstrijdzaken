using Npgsql;

namespace Database.Postgres;

/// <summary>
/// #1246: voert het idempotente demodata-seedscript uit en rapporteert daarna wat de democlub
/// werkelijk heeft.
/// <para>
/// <b>Waarom dit geen migratie is.</b> Het seedscript vult <c>his.teams</c>/<c>his.matches</c>, en
/// die tabellen worden door geen enkel migratiebestand aangemaakt — <see cref="PostgresSchemaGenerator"/>
/// doet dat dynamisch zodra de ETL zijn eerste sync draait. Op het moment dat
/// <see cref="MigrationRunner"/> loopt bestaan ze op een verse database dus nog niet. Dat is ook de
/// reden dat het script buiten <c>Database.Postgres/migrations/</c> staat en buiten de ledger valt.
/// </para>
/// <para>
/// <b>Waarom het herhaald draaien mag.</b> Elk onderdeel van het script is <c>NOT EXISTS</c>-gated
/// en raakt uitsluitend rijen met de democlubcode. Juist die herhaalbaarheid repareert de fout uit
/// #1246: de speeltijden-copy hangt af van data die de beheerder pas later via de Admin GUI
/// invoert, en een eenmalige migratie kan daar per definitie niet op wachten.
/// </para>
/// </summary>
public static class DemodataSeeder
{
    /// <summary>Vaste democlubcode — zie CLAUDE.md, "AllStars FC".</summary>
    public const string DemoClubCode = "ALLSTARS";

    /// <summary>Wat de democlub na afloop bevat. -1 = tabel bestaat nog niet.</summary>
    public sealed record Telling(
        int HisTeams,
        int PublicTeams,
        int HisMatches,
        int Teambegeleiding,
        int Speeltijden)
    {
        /// <summary>
        /// <c>public.teams</c> is een afgeleide tabel die alleen door
        /// <c>POST /api/beheer/teams/herstel</c> wordt opgebouwd. Dat endpoint is
        /// <c>RequireAdmin</c> en vereist een Entra-token, dus een deploypipeline kan het niet
        /// aanroepen — vandaar een signaal in plaats van automatisering (#1246).
        /// </summary>
        public bool CanoniekeLijstOntbreekt => HisTeams > 0 && PublicTeams == 0;
    }

    /// <summary>
    /// Draait het seedscript. Geeft <c>null</c> terug als de democlub niet in
    /// <c>public.appsettings</c> staat.
    /// <para>
    /// Dat laatste is geen fout maar een geldige keuze: migratie 006 maakt de rij aan, maar een
    /// fork mag hem weghalen en houdt dan een schone database. Het seedscript zelf faalt daar hard
    /// op — terecht, want wie het met de hand aanroept heeft iets te vroeg gedraaid. Een
    /// deploypipeline mag daar niet op stuklopen, dus die vraag wordt hier gesteld vóór het script
    /// überhaupt begint (#1246).
    /// </para>
    /// </summary>
    public static async Task<Telling?> RunAsync(
        string connectionString,
        string scriptPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Seedscript niet gevonden: {scriptPath}", scriptPath);

        var sql = await File.ReadAllTextAsync(scriptPath, ct);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var demoClub = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM public.appsettings WHERE clubcode = @club)", connection))
        {
            demoClub.Parameters.AddWithValue("club", DemoClubCode);
            if (await demoClub.ExecuteScalarAsync(ct) is not true)
                return null;
        }

        // Het script is één DO-blok: één command volstaat, en een RAISE EXCEPTION erin rolt het
        // geheel terug zonder dat hier een expliciete transactie nodig is.
        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return await TelAsync(connection, ct);
    }

    /// <summary>Telt per tabel; -1 als de tabel nog niet bestaat (verse database zonder ETL-sync).</summary>
    public static async Task<Telling> TelAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        return new Telling(
            await TelAsync(connection, "his", "teams", ct),
            await TelAsync(connection, "public", "teams", ct),
            await TelAsync(connection, "his", "matches", ct),
            await TelAsync(connection, "avg", "teambegeleiding", ct),
            await TelAsync(connection, "public", "speeltijden", ct));
    }

    private static async Task<int> TelAsync(
        NpgsqlConnection connection, string schema, string tabel, CancellationToken ct)
    {
        // Schema- en tabelnaam komen uitsluitend uit de literalen hierboven, nooit uit invoer;
        // de clubcode gaat wél als parameter mee.
        var identifier = $"{PostgresIdentifier.Quote(schema)}.{PostgresIdentifier.Quote(tabel)}";

        await using (var bestaat = new NpgsqlCommand("SELECT to_regclass(@naam) IS NOT NULL", connection))
        {
            bestaat.Parameters.AddWithValue("naam", $"{schema}.{tabel}");
            if (await bestaat.ExecuteScalarAsync(ct) is not true)
                return -1;
        }

        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM {identifier} WHERE clubcode = @club", connection);
        cmd.Parameters.AddWithValue("club", DemoClubCode);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
}
