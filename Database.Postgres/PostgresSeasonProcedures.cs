using Npgsql;

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
/// </summary>
public static class PostgresSeasonProcedures
{
    /// <summary>Fallback voor <c>seasonstartmonth</c>, gelijk aan migratie 008.</summary>
    private const int DefaultSeasonStartMonth = 7;

    /// <summary>
    /// Vult <c>public.season</c> aan zodat het huidige en aankomende seizoen bestaan. Idempotent:
    /// zonder werk te doen zijn dit twee SELECTs en geen enkele wijziging.
    /// </summary>
    /// <param name="vandaag">
    /// De datum waartegen gerekend wordt. Expliciet in plaats van <c>CURRENT_DATE</c>, zodat het
    /// gedrag rond de tweemaandsgrens toetsbaar is zonder de systeemklok te verzetten — anders zou
    /// die tak elf maanden per jaar onbewijsbaar zijn.
    /// </param>
    /// <returns>Het aantal toegevoegde seizoensrijen (0 wanneer er niets te doen was).</returns>
    public static async Task<int> EnsureSeasonsAsync(
        NpgsqlConnection connection, DateOnly vandaag, CancellationToken ct = default)
    {
        var startMaand = await LeesSeasonStartMonthAsync(connection, ct);
        var jaar = vandaag.Year;
        var toegevoegd = 0;

        // Tak 1 — "No seasons found" uit het origineel: zaai de laatste twee afgeronde seizoenen.
        // Op een normaal draaiende installatie doet migratie 008 dit al; deze tak is er voor een
        // database waar de tabel om welke reden dan ook leeg is, zodat de sync niet stilvalt.
        if (!await HeeftSeizoenenAsync(connection, ct))
        {
            toegevoegd += await VoegSeizoenToeAsync(connection, jaar - 2, startMaand, ct);
            toegevoegd += await VoegSeizoenToeAsync(connection, jaar - 1, startMaand, ct);
        }

        // Tak 2 — de doorrol: vanaf twee maanden vóór de seizoensstart bestaat het nieuwe seizoen.
        //
        // De drempeldatum wordt met intervalrekenkunde bepaald en niet met make_date(jaar,
        // startMaand - 2, 1): bij startMaand 1 of 2 levert dat een ongeldige maand op. Het
        // SQL Server-origineel heeft die fout wel (DATEFROMPARTS(..., @SeasonStartMonth-2, ...)
        // faalt daar bij januari/februari); migratie 008 loste hem al op en deze implementatie
        // houdt die correctie aan. Voor de gangbare startmaanden (juli/augustus) is het gedrag
        // identiek aan het origineel.
        var drempel = new DateTime(jaar, 1, 1).AddMonths(startMaand - 3);
        if (vandaag >= DateOnly.FromDateTime(drempel))
            toegevoegd += await VoegSeizoenToeAsync(connection, jaar, startMaand, ct);

        return toegevoegd;
    }

    /// <summary>
    /// Voegt het seizoen <c>&lt;jaar&gt;-&lt;jaar+1&gt;</c> toe als het nog niet bestaat.
    /// <para>
    /// De idempotentie leunt op <c>ON CONFLICT (name) DO NOTHING</c> tegen <c>ux_season_name</c>
    /// (migratie 008) in plaats van op een <c>IF NOT EXISTS</c>-guard zoals het origineel: dat
    /// laatste is een controle-en-daarna-schrijven met een venster ertussen, en de sync kan vanuit
    /// meerdere invocaties tegelijk starten. #631 laat zien wat een niet-sluitende guard hier
    /// oplevert — daar stonden drie identieke rijen voor hetzelfde seizoen in productie.
    /// </para>
    /// </summary>
    private static async Task<int> VoegSeizoenToeAsync(
        NpgsqlConnection connection, int jaar, int startMaand, CancellationToken ct)
    {
        var naam = $"{jaar}-{jaar + 1}";
        var van = new DateTime(jaar, startMaand, 1);
        // EOMONTH(DATEFROMPARTS(jaar+1, startMaand-1, 1)) uit het origineel = de dag vóór de start
        // van het volgende seizoen. Die vorm werkt ook bij startMaand = 1, waar startMaand-1 = 0
        // geen geldige maand is.
        var tot = new DateTime(jaar + 1, startMaand, 1).AddDays(-1);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.season (name, datefrom, dateuntil)
            VALUES (@name, @datefrom, @dateuntil)
            ON CONFLICT (name) DO NOTHING", connection);
        cmd.Parameters.AddWithValue("name", naam);
        cmd.Parameters.AddWithValue("datefrom", van.Date);
        cmd.Parameters.AddWithValue("dateuntil", tot.Date);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> HeeftSeizoenenAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM public.season LIMIT 1", connection);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Zelfde bron en fallback als migratie 008: de laagste <c>seasonstartmonth</c> uit
    /// <c>public.appsettings</c>, of <see cref="DefaultSeasonStartMonth"/> als die tabel geen
    /// bruikbare waarde bevat.
    /// </summary>
    private static async Task<int> LeesSeasonStartMonthAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT MIN(seasonstartmonth) FROM public.appsettings", connection);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int maand && maand is >= 1 and <= 12 ? maand : DefaultSeasonStartMonth;
    }
}
