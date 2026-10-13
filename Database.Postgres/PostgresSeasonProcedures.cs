using Npgsql;
using Planner.Shared.Sync;

namespace Database.Postgres;

/// <summary>
/// Postgres-tier-tegenhanger van <c>dbo.sp_UpdateSeasonTable</c> (#861) — de vijfde en laatste
/// opgeslagen procedure uit dat issue met een consument op deze tier.
///
/// <para>
/// <b>Waarom dit moest bestaan.</b> Migratie <c>008_season.sql</c> zaait <c>public.season</c> één
/// keer, op het moment dat de migratie wordt toegepast. De SQL Server-tier doet meer:
/// <c>Script.PostDeployment1.sql</c> roept <c>sp_UpdateSeasonTable</c> bij élke deploy opnieuw aan
/// en rolt het seizoen zo vanzelf door zodra de kalender twee maanden vóór de volgende start zit.
/// Een migratiebestand draait precies één keer, ooit — een installatie die lang genoeg meedraait
/// liep dus uit de seizoenen, waarna <c>PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync</c> een
/// steeds korter (uiteindelijk negatief) synchronisatievenster oplevert. Vastgelegd als gat in
/// docs/ARCHITECTUUR-DATABASE-TIERS.md §21; deze klasse dicht het.
/// </para>
///
/// <para>
/// <b>Architectuurbeslissing — C#, geen PL/pgSQL-functie.</b> Zelfde patroon als
/// <see cref="PostgresCleanupProcedures"/> (#861) en <see cref="PostgresMergeOrchestrator"/> (#818):
/// procedurele logica leeft op deze tier in C#. <c>CURRENT_DATE</c> wordt bewust één keer in C#
/// bepaald en als parameter meegegeven, zodat alle vergelijkingen binnen één aanroep op dezelfde
/// datum berusten — dezelfde reden als bij de opschoonprocedures.
/// </para>
///
/// <para>
/// <b>Dit is vanaf nu de enige levende plek voor deze regel.</b> Het DO-blok in migratie 008 bevat
/// dezelfde berekening, maar dat bestand is per definitie bevroren: <see cref="MigrationRunner"/>
/// verifieert de SHA-256 van elk toegepast bestand en faalt hard op een wijziging (#821). Migratie
/// 008 is dus historie, geen regel die nog onderhouden wordt — een toekomstige wijziging aan de
/// seizoenslogica hoort uitsluitend hier.
/// </para>
///
/// <para>
/// <b>Bewust niet meegenomen:</b> de laatste stap van het origineel,
/// <c>EXEC dbo.sp_CreateDateTable @YearStart, @YearEnd</c>. <c>dbo.DateTable</c> heeft binnen de
/// applicatie precies één consument — de view <c>pub.DateTable</c> — en die drie
/// <c>pub.*</c>-rapportageviews zijn voor deze tier al expliciet en gemotiveerd laten vervallen
/// (#861, nul consumenten). Een tegenhanger zou uitsluitend een tabel zijn die nergens gelezen wordt.
/// </para>
///
/// <para>
/// <b>Per club (#1715).</b> <c>public.season</c> heeft sinds migratie 043 een <c>clubcode</c> en elke club
/// krijgt zijn seizoenen uit zijn eigen <c>seasonstartmonth</c>. Er wordt nergens meer een waarde uit de
/// rijen van alle clubs afgeleid (geen <c>MIN()</c>/<c>MAX()</c> over clubs).
/// </para>
/// </summary>
public static class PostgresSeasonProcedures
{
    /// <summary>
    /// Vult <c>public.season</c> aan voor elke club in <c>public.appsettings</c> (met de eigen startmaand)
    /// zodat het huidige en aankomende seizoen bestaan. Idempotent: zonder werk te doen zijn dit
    /// SELECTs en geen enkele wijziging.
    /// </summary>
    /// <param name="vandaag">
    /// De datum waartegen gerekend wordt. Expliciet in plaats van <c>CURRENT_DATE</c>, zodat het
    /// gedrag rond de tweemaandsgrens toetsbaar is zonder de systeemklok te verzetten — anders zou
    /// die tak elf maanden per jaar onbewijsbaar zijn.
    /// </param>
    /// <returns>Het aantal toegevoegde seizoensrijen over alle clubs (0 wanneer er niets te doen was).</returns>
    public static async Task<int> EnsureSeasonsAsync(
        NpgsqlConnection connection, DateOnly vandaag, CancellationToken ct = default)
    {
        var toegevoegd = 0;
        foreach (var (clubCode, startMaand) in await LeesClubsAsync(connection, ct))
            toegevoegd += await EnsureSeasonsVoorClubAsync(connection, clubCode, startMaand, vandaag, ct);
        return toegevoegd;
    }

    /// <summary>
    /// Vult de seizoenen van één club aan met de opgegeven startmaand; een ongeldige maand valt terug
    /// op <see cref="SeizoenStartMaand.Standaard"/>.
    /// </summary>
    public static async Task<int> EnsureSeasonsVoorClubAsync(
        NpgsqlConnection connection, string clubCode, int startMaand, DateOnly vandaag, CancellationToken ct = default)
    {
        startMaand = SeizoenStartMaand.OfStandaard(startMaand);
        var jaar = vandaag.Year;
        var toegevoegd = 0;

        // Tak 1 — "No seasons found" uit het origineel: zaai de laatste twee afgeronde seizoenen.
        // Op een normaal draaiende installatie doet migratie 043 dit al; deze tak is er voor een
        // club waarvoor de tabel om welke reden dan ook leeg is, zodat de sync niet stilvalt.
        if (!await HeeftSeizoenenAsync(connection, clubCode, ct))
        {
            toegevoegd += await VoegSeizoenToeAsync(connection, clubCode, jaar - 2, startMaand, ct);
            toegevoegd += await VoegSeizoenToeAsync(connection, clubCode, jaar - 1, startMaand, ct);
        }

        // Tak 2 — de doorrol: vanaf twee maanden vóór de seizoensstart bestaat het nieuwe seizoen.
        // De drempeldatum (SeizoenKalender.DoorrolDatum) gebruikt intervalrekenkunde en niet
        // make_date(jaar, startMaand - 2, 1): bij startMaand 1 of 2 levert dat een ongeldige maand op.
        // Het SQL Server-origineel had die fout; die procedure rekent sinds #1715 ook met DATEADD.
        if (vandaag >= SeizoenKalender.DoorrolDatum(jaar, startMaand))
            toegevoegd += await VoegSeizoenToeAsync(connection, clubCode, jaar, startMaand, ct);

        return toegevoegd;
    }

    /// <summary>
    /// Bouwt de seizoensrijen van één club opnieuw op uit de huidige startmaand — na een wijziging van
    /// <c>SeasonStartMonth</c> in Beheer &gt; Instellingen (#1715). De tabel is afgeleide data: de rijen
    /// van de club worden verwijderd en met de nieuwe maand opnieuw geplaatst, in één transactie.
    /// Andere clubs worden niet aangeraakt.
    /// </summary>
    public static async Task<int> HerbouwSeizoenenVoorClubAsync(
        NpgsqlConnection connection, string clubCode, int startMaand, DateOnly vandaag, CancellationToken ct = default)
    {
        await using var transactie = await connection.BeginTransactionAsync(ct);
        await using (var verwijder = new NpgsqlCommand("DELETE FROM public.season WHERE clubcode = @clubcode", connection, transactie))
        {
            verwijder.Parameters.AddWithValue("clubcode", clubCode);
            await verwijder.ExecuteNonQueryAsync(ct);
        }
        var toegevoegd = await EnsureSeasonsVoorClubAsync(connection, clubCode, startMaand, vandaag, ct);
        await transactie.CommitAsync(ct);
        return toegevoegd;
    }

    /// <summary>
    /// Voegt het seizoen <c>&lt;jaar&gt;-&lt;jaar+1&gt;</c> van een club toe als het nog niet bestaat.
    /// <para>
    /// De idempotentie leunt op <c>ON CONFLICT (clubcode, name) DO NOTHING</c> tegen
    /// <c>ux_season_clubcode_name</c> (migratie 043) in plaats van op een <c>IF NOT EXISTS</c>-guard zoals
    /// het origineel: dat laatste is een controle-en-daarna-schrijven met een venster ertussen, en de
    /// sync kan vanuit meerdere invocaties tegelijk starten. #631 laat zien wat een niet-sluitende guard
    /// hier oplevert — daar stonden drie identieke rijen voor hetzelfde seizoen in productie.
    /// </para>
    /// </summary>
    private static async Task<int> VoegSeizoenToeAsync(
        NpgsqlConnection connection, string clubCode, int jaar, int startMaand, CancellationToken ct)
    {
        // Seizoensnaam en -grenzen uit de gedeelde seizoenskalender: het einde is de dag vóór de start
        // van het volgende seizoen (werkt ook bij startMaand = 1, waar startMaand-1 = 0 geen maand is).
        var seizoen = SeizoenKalender.Seizoen(jaar, startMaand);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.season (clubcode, name, datefrom, dateuntil)
            VALUES (@clubcode, @name, @datefrom, @dateuntil)
            ON CONFLICT (clubcode, name) DO NOTHING", connection);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("name", seizoen.Naam);
        cmd.Parameters.AddWithValue("datefrom", seizoen.Van);
        cmd.Parameters.AddWithValue("dateuntil", seizoen.Tot);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> HeeftSeizoenenAsync(NpgsqlConnection connection, string clubCode, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM public.season WHERE clubcode = @clubcode LIMIT 1", connection);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Alle clubs met hun eigen startmaand uit <c>public.appsettings</c>. Een ongeldige of ontbrekende
    /// waarde wordt <see cref="SeizoenStartMaand.Standaard"/> (juli): één regel voor de club zelf, geen
    /// waarde uit andere clubs.
    /// </summary>
    private static async Task<List<(string ClubCode, int StartMaand)>> LeesClubsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var clubs = new List<(string, int)>();
        await using var cmd = new NpgsqlCommand("SELECT clubcode, seasonstartmonth FROM public.appsettings ORDER BY clubcode", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            clubs.Add((reader.GetString(0), SeizoenStartMaand.OfStandaard(reader.IsDBNull(1) ? null : reader.GetInt32(1))));
        return clubs;
    }
}
