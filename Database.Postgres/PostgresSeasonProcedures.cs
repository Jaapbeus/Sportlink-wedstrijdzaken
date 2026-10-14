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
/// <b>Per club (#1715).</b> Sinds migratie 043 staan de seizoenen per club in <c>public.clubseason</c> en krijgt
/// elke club zijn seizoenen uit zijn eigen <c>seasonstartmonth</c>. Er wordt nergens meer een waarde uit de
/// rijen van alle clubs afgeleid (geen <c>MIN()</c>/<c>MAX()</c> over clubs). De clubneutrale tabel
/// <c>public.season</c> blijft voor de overgang onaangeroerd staan (expand/contract, §84): de vorige codeversie
/// schrijft daar tijdens de deploy nog in, deze klasse niet meer.
/// </para>
///
/// <para>
/// <b>Eén club tegelijk (Codex-review #1743 ronde 1).</b> Doorrollen en herbouwen lezen de startmaand binnen
/// hun eigen transactie met een rijlock op <c>public.appsettings</c> (<c>FOR SHARE</c> resp. de
/// <c>FOR UPDATE</c> van de instellingenwijziging). Zo kan een gelijktijdige wijziging van de maand niet
/// tussen het lezen van de maand en het schrijven van de seizoenen vallen, en eindigen configuratie en
/// kalender altijd in dezelfde toestand.
/// </para>
/// </summary>
public static class PostgresSeasonProcedures
{
    /// <summary>
    /// Vult <c>public.clubseason</c> aan voor elke club in <c>public.appsettings</c> (met de eigen startmaand)
    /// zodat het huidige en aankomende seizoen bestaan. Idempotent: zonder werk te doen zijn dit
    /// SELECTs en geen enkele wijziging. Elke club in een eigen korte transactie.
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
        foreach (var clubCode in await LeesClubsAsync(connection, ct))
        {
            await using var transactie = await connection.BeginTransactionAsync(ct);
            // FOR SHARE: wacht op een lopende wijziging van de maand en houdt die tegen tot deze club klaar is.
            var startMaand = await LeesStartMaandAsync(connection, transactie, clubCode, exclusief: false, ct);
            if (startMaand is int maand)
                toegevoegd += await VulAanAsync(connection, transactie, clubCode, maand, vandaag, ct);
            await transactie.CommitAsync(ct);
        }
        return toegevoegd;
    }

    /// <summary>
    /// Bouwt de seizoensrijen van één club opnieuw op uit de startmaand die binnen
    /// <paramref name="transactie"/> in <c>public.appsettings</c> staat — na een wijziging van
    /// <c>SeasonStartMonth</c> in Beheer &gt; Instellingen (#1715). Draait in de transactie van die wijziging
    /// (met de auditregel), zodat configuratie, audit en kalender samen slagen of samen worden teruggedraaid.
    /// De tabel is afgeleide data: de rijen van de club worden verwijderd en met de maand opnieuw geplaatst.
    /// Andere clubs worden niet aangeraakt.
    /// </summary>
    /// <returns>Het aantal geplaatste seizoensrijen.</returns>
    public static async Task<int> HerbouwSeizoenenVoorClubAsync(
        NpgsqlConnection connection, NpgsqlTransaction transactie, string clubCode, DateOnly vandaag, CancellationToken ct = default)
    {
        // FOR UPDATE: de instellingenwijziging houdt deze lock al; een losse aanroep neemt hem hier.
        // Zonder instellingenrij valt er niets af te leiden; dan blijven ook de bestaande rijen staan.
        if (await LeesStartMaandAsync(connection, transactie, clubCode, exclusief: true, ct) is not int startMaand)
            return 0;
        await using (var verwijder = new NpgsqlCommand("DELETE FROM public.clubseason WHERE clubcode = @clubcode", connection, transactie))
        {
            verwijder.Parameters.AddWithValue("clubcode", clubCode);
            await verwijder.ExecuteNonQueryAsync(ct);
        }
        return await VulAanAsync(connection, transactie, clubCode, startMaand, vandaag, ct);
    }

    /// <summary>
    /// Vult de seizoenen van één club aan met de opgegeven startmaand (een ongeldige maand valt terug op
    /// <see cref="SeizoenStartMaand.Standaard"/>): de zaai-tak als de club nog niets heeft, en de doorrol
    /// voor elk seizoen waarvan de doorroldatum bereikt is.
    /// </summary>
    private static async Task<int> VulAanAsync(
        NpgsqlConnection connection, NpgsqlTransaction transactie, string clubCode, int startMaand, DateOnly vandaag, CancellationToken ct)
    {
        startMaand = SeizoenStartMaand.OfStandaard(startMaand);
        var jaar = vandaag.Year;
        var toegevoegd = 0;

        // Tak 1 — "No seasons found" uit het origineel: zaai de laatste twee afgeronde seizoenen.
        // Op een normaal draaiende installatie doet migratie 043 dit al; deze tak is er voor een
        // club waarvoor de tabel om welke reden dan ook leeg is, zodat de sync niet stilvalt.
        if (!await HeeftSeizoenenAsync(connection, transactie, clubCode, ct))
        {
            toegevoegd += await VoegSeizoenToeAsync(connection, transactie, clubCode, jaar - 2, startMaand, ct);
            toegevoegd += await VoegSeizoenToeAsync(connection, transactie, clubCode, jaar - 1, startMaand, ct);
        }

        // Tak 2 — de doorrol: vanaf twee maanden vóór de seizoensstart bestaat het nieuwe seizoen. Bij een
        // start in januari/februari ligt die drempel vóór de jaarwisseling, dus kijkt de gedeelde kalender
        // ook naar het seizoen van volgend jaar (SeizoenKalender.DoorTeRollenStartjaren). De drempeldatum
        // gebruikt intervalrekenkunde en niet make_date(jaar, startMaand - 2, 1): bij startMaand 1 of 2
        // levert dat een ongeldige maand op.
        foreach (var startjaar in SeizoenKalender.DoorTeRollenStartjaren(startMaand, vandaag))
            toegevoegd += await VoegSeizoenToeAsync(connection, transactie, clubCode, startjaar, startMaand, ct);

        return toegevoegd;
    }

    /// <summary>
    /// Voegt het seizoen <c>&lt;jaar&gt;-&lt;jaar+1&gt;</c> van een club toe als het nog niet bestaat.
    /// <para>
    /// De idempotentie leunt op <c>ON CONFLICT (clubcode, name) DO NOTHING</c> tegen
    /// <c>pk_clubseason</c> (migratie 043) in plaats van op een <c>IF NOT EXISTS</c>-guard zoals
    /// het origineel: dat laatste is een controle-en-daarna-schrijven met een venster ertussen, en de
    /// sync kan vanuit meerdere invocaties tegelijk starten. #631 laat zien wat een niet-sluitende guard
    /// hier oplevert — daar stonden drie identieke rijen voor hetzelfde seizoen in productie.
    /// </para>
    /// </summary>
    private static async Task<int> VoegSeizoenToeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transactie, string clubCode, int jaar, int startMaand, CancellationToken ct)
    {
        // Seizoensnaam en -grenzen uit de gedeelde seizoenskalender: het einde is de dag vóór de start
        // van het volgende seizoen (werkt ook bij startMaand = 1, waar startMaand-1 = 0 geen maand is).
        var seizoen = SeizoenKalender.Seizoen(jaar, startMaand);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.clubseason (clubcode, name, datefrom, dateuntil)
            VALUES (@clubcode, @name, @datefrom, @dateuntil)
            ON CONFLICT (clubcode, name) DO NOTHING", connection, transactie);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("name", seizoen.Naam);
        cmd.Parameters.AddWithValue("datefrom", seizoen.Van);
        cmd.Parameters.AddWithValue("dateuntil", seizoen.Tot);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> HeeftSeizoenenAsync(
        NpgsqlConnection connection, NpgsqlTransaction transactie, string clubCode, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM public.clubseason WHERE clubcode = @clubcode LIMIT 1", connection, transactie);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// De startmaand van één club, gelezen met een rijlock: <c>FOR UPDATE</c> bij herbouwen
    /// (<paramref name="exclusief"/>), anders <c>FOR SHARE</c>. Ongeldig of leeg wordt
    /// <see cref="SeizoenStartMaand.Standaard"/> (juli): één regel voor de club zelf, geen waarde uit andere
    /// clubs. <c>null</c> als de club (inmiddels) geen instellingen heeft.
    /// </summary>
    private static async Task<int?> LeesStartMaandAsync(
        NpgsqlConnection connection, NpgsqlTransaction transactie, string clubCode, bool exclusief, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            exclusief
                ? "SELECT seasonstartmonth FROM public.appsettings WHERE clubcode = @clubcode FOR UPDATE"
                : "SELECT seasonstartmonth FROM public.appsettings WHERE clubcode = @clubcode FOR SHARE",
            connection, transactie);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return SeizoenStartMaand.OfStandaard(reader.IsDBNull(0) ? null : reader.GetInt32(0));
    }

    /// <summary>Alle clubs uit <c>public.appsettings</c>; de maand wordt per club onder lock gelezen.</summary>
    private static async Task<List<string>> LeesClubsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var clubs = new List<string>();
        await using var cmd = new NpgsqlCommand("SELECT clubcode FROM public.appsettings ORDER BY clubcode", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            clubs.Add(reader.GetString(0));
        return clubs;
    }
}
