using AwesomeAssertions;
using Npgsql;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Bewijst dat <see cref="PostgresSeasonProcedures.EnsureSeasonsAsync"/> het gat dicht dat §21
/// beschreef: migratie 008 zaait <c>public.season</c> één keer, maar er was op deze tier geen
/// equivalent van de doorrol die <c>sp_UpdateSeasonTable</c> op de SQL Server-tier bij elke deploy
/// uitvoert.
///
/// <para>
/// <b>Waarom deze tests een expliciete datum meegeven.</b> De doorrol-tak vuurt alleen vanaf twee
/// maanden vóór de seizoensstart — bij de gangbare startmaand juli dus vanaf 1 mei. Zou de test op
/// <c>DateTime.Today</c> leunen, dan zou hij elf maanden per jaar niets bewijzen en één maand per
/// jaar iets anders meten. De datum is daarom een parameter van de methode zelf; dat is de reden
/// dat die parameter bestaat.
/// </para>
///
/// <para>Zie <c>PostgresMergeOrchestratorIntegrationTests</c> voor de wegwerpcontainer-instructies.</para>
/// </summary>
public class PostgresSeasonProceduresIntegrationTests
{
    private const string Club = "SEASONTEST";
    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull!;

    [PostgresFact]
    public async Task EnsureSeasonsAsync_LegeTabel_ZaaitDeLaatsteTweeAfgerondeSeizoenen()
    {
        await using var conn = await OpenSchoonAsync();

        var toegevoegd = await PostgresSeasonProcedures.EnsureSeasonsAsync(
            conn, new DateOnly(2026, 2, 1)); // vóór de drempel (1 mei), dus alleen de zaai-tak

        toegevoegd.Should().Be(2);
        (await NamenAsync(conn)).Should().Equal("2024-2025", "2025-2026");
    }

    /// <summary>
    /// De kern van issue 861: op 1 mei bestaat het aankomende seizoen nog niet en moet het erbij
    /// komen. Dit is precies wat een Postgres-installatie zonder deze procedure nooit zou doen —
    /// migratie 008 is dan al lang toegepast en draait niet opnieuw.
    /// </summary>
    [PostgresFact]
    public async Task EnsureSeasonsAsync_VanafTweeMaandenVoorDeStart_VoegtHetNieuweSeizoenToe()
    {
        await using var conn = await OpenSchoonAsync();
        await ZaaiAsync(conn, 2024);
        await ZaaiAsync(conn, 2025);

        // 30 april: één dag vóór de drempel — er mag nog niets gebeuren.
        (await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, new DateOnly(2026, 4, 30)))
            .Should().Be(0, "de doorrol begint pas twee maanden vóór de seizoensstart (1 mei bij startmaand 7)");
        (await NamenAsync(conn)).Should().Equal("2024-2025", "2025-2026");

        // 1 mei: de drempel is bereikt.
        (await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, new DateOnly(2026, 5, 1)))
            .Should().Be(1);
        (await NamenAsync(conn)).Should().Equal("2024-2025", "2025-2026", "2026-2027");

        var (van, tot) = await GrenzenAsync(conn, "2026-2027");
        van.Should().Be(new DateTime(2026, 7, 1), "het seizoen begint op de eerste van de seizoensstartmaand");
        tot.Should().Be(new DateTime(2027, 6, 30), "en loopt tot de dag vóór de volgende start");
    }

    /// <summary>
    /// De schade die §21 beschrijft, gemeten in plaats van beschreven: zonder doorrol wijst
    /// <c>MAX(dateuntil)</c> op enig moment naar het verleden, en dan levert de formule van
    /// <c>PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync</c>
    /// (<c>ceil((MAX(dateuntil) - vandaag) / 7)</c>) een <b>negatieve</b> week-offset op — een
    /// synchronisatievenster dat in het verleden eindigt, dus een sync die niets meer ophaalt.
    ///
    /// <para>
    /// De "vóór"-meting is essentieel: zonder haar zou de "na"-meting alleen aantonen dat de offset
    /// positief is, niet dat de doorrol daar iets aan verandert.
    /// </para>
    /// </summary>
    [PostgresFact]
    public async Task EnsureSeasonsAsync_ZonderDoorrol_LooptHetSynchronisatievensterInHetVerleden()
    {
        await using var conn = await OpenSchoonAsync();
        // Precies wat migratie 008 achterlaat op een installatie die in 2025 is opgezet.
        await ZaaiAsync(conn, 2024);
        await ZaaiAsync(conn, 2025);

        var vandaag = new DateOnly(2026, 8, 31); // ruim ná het einde van seizoen 2025-2026

        var voor = await WeekOffsetTotSeizoenseindeAsync(conn, vandaag);
        voor.Should().BeNegative(
            "MAX(dateuntil) is 2026-06-30 en ligt dus in het verleden — dit is het gat uit §21");

        (await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, vandaag)).Should().Be(1);

        var na = await WeekOffsetTotSeizoenseindeAsync(conn, vandaag);
        na.Should().BePositive("na de doorrol loopt het seizoen tot 2027-06-30");
        na.Should().BeGreaterThan(voor);
    }

    /// <summary>
    /// #631 in het origineel: een niet-sluitende guard voegde hetzelfde seizoen bij elke deploy
    /// opnieuw toe — er stonden drie identieke rijen in productie. Deze implementatie leunt op
    /// <c>ON CONFLICT (clubcode, name) DO NOTHING</c>, dus herhalen is per definitie een no-op.
    /// </summary>
    [PostgresFact]
    public async Task EnsureSeasonsAsync_HerhaaldAangeroepen_VoegtNietsDubbelToe()
    {
        await using var conn = await OpenSchoonAsync();

        await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, new DateOnly(2026, 5, 1));
        var naEerste = await NamenAsync(conn);

        for (var i = 0; i < 3; i++)
            (await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, new DateOnly(2026, 5, 1)))
                .Should().Be(0, "een tweede aanroep op dezelfde dag mag niets toevoegen");

        (await NamenAsync(conn)).Should().Equal(naEerste);
    }

    /// <summary>
    /// Een toekomstig seizoen in de tabel mag de doorrol niet in de war sturen — dat was precies de
    /// fout in #631, waar de guard op <c>MAX(DateUntil)</c> keek in plaats van op het bestaan van
    /// het te maken seizoen.
    /// </summary>
    [PostgresFact]
    public async Task EnsureSeasonsAsync_MetEenToekomstigSeizoenInDeTabel_BlijftCorrect()
    {
        await using var conn = await OpenSchoonAsync();
        await ZaaiAsync(conn, 2025);
        await ZaaiAsync(conn, 2027); // toekomstig seizoen, maar 2026-2027 ontbreekt nog

        (await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, new DateOnly(2026, 5, 1)))
            .Should().Be(1, "het ontbrekende seizoen 2026-2027 hoort erbij te komen, ondanks het latere 2027-2028");

        (await NamenAsync(conn)).Should().Equal("2025-2026", "2026-2027", "2027-2028");
    }

    /// <summary>
    /// Startmaand januari of februari laat <c>DATEFROMPARTS(jaar, startmaand - 2, 1)</c> uit het
    /// SQL Server-origineel op een ongeldige maand uitkomen. Migratie 008 loste dat al op met
    /// intervalrekenkunde en deze implementatie houdt die correctie aan — hier vastgelegd zodat de
    /// afwijking van het origineel bewust blijft en niet per ongeluk wordt "teruggerepareerd".
    /// </summary>
    [PostgresTheory]
    [InlineData(1)]   // januari: startMaand - 2 = -1 → geen geldige maand voor DATEFROMPARTS
    [InlineData(2)]   // februari: startMaand - 2 = 0 → idem
    public async Task EnsureSeasonsAsync_StartmaandJanuariOfFebruari_RekentZonderOngeldigeMaand(int startMaand)
    {
        await using var conn = await OpenSchoonAsync(startMaand);

        // Geen exception is hier de halve assertie: DATEFROMPARTS(jaar, startMaand - 2, 1) zou hier
        // op maand -1 respectievelijk 0 uitkomen.
        var vandaag = new DateOnly(2026, 3, 15);
        (await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, vandaag)).Should().Be(3);

        // Drie rijen, niet twee: bij een startmaand in januari/februari ligt de drempel voor
        // kalenderjaar Y op 1 november/december van Y-1, dus élke datum ín Y ligt erna. Dat is
        // functioneel juist — een seizoen dat in januari begint, is in maart al aan de gang en
        // hoort dus te bestaan. De doorrol-tak vuurt hier dus altijd, anders dan bij een
        // juli-startmaand waar 1 mei de grens is.
        var namen = await NamenAsync(conn);
        namen.Should().Equal("2024-2025", "2025-2026", "2026-2027");

        var (van, tot) = await GrenzenAsync(conn, "2026-2027");
        van.Should().Be(new DateTime(2026, startMaand, 1));
        tot.Should().Be(new DateTime(2027, startMaand, 1).AddDays(-1),
            "het seizoen loopt tot de dag vóór de volgende start — ook wanneer startMaand - 1 geen "
            + "geldige maand zou zijn (januari)");
    }

    /// <summary>
    /// #1715, de rode test voor twee clubs: elke club krijgt de seizoenen uit zijn eigen startmaand. Met
    /// de oude <c>MIN(seasonstartmonth)</c> over alle clubs kreeg de septemberclub juli-seizoenen.
    /// </summary>
    [PostgresFact]
    public async Task EnsureSeasonsAsync_TweeClubsMetVerschillendeStartmaand_KrijgenElkEenEigenSeizoenen()
    {
        await using var conn = await OpenSchoonAsync(7);
        await using (var tweede = new NpgsqlCommand(
            "INSERT INTO public.appsettings (clubcode, clubname, seasonstartmonth) VALUES ('SEASONTWEE', 'Season Twee', 9)", conn))
            await tweede.ExecuteNonQueryAsync();

        (await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, new DateOnly(2026, 10, 13))).Should().Be(6);

        (await GrenzenAsync(conn, "2026-2027", Club)).Tot.Should().Be(new DateTime(2027, 6, 30), "juli-start: einde 30 juni");
        (await GrenzenAsync(conn, "2026-2027", "SEASONTWEE")).Tot.Should().Be(new DateTime(2027, 8, 31), "september-start: einde 31 augustus");
        (await GrenzenAsync(conn, "2026-2027", "SEASONTWEE")).Van.Should().Be(new DateTime(2026, 9, 1));
    }

    [PostgresFact]
    public async Task HerbouwSeizoenenVoorClubAsync_PastAlleenDeEigenClubAanNaEenWijzigingVanDeMaand()
    {
        await using var conn = await OpenSchoonAsync(7);
        await using (var tweede = new NpgsqlCommand(
            "INSERT INTO public.appsettings (clubcode, clubname, seasonstartmonth) VALUES ('SEASONTWEE', 'Season Twee', 7)", conn))
            await tweede.ExecuteNonQueryAsync();
        var vandaag = new DateOnly(2026, 10, 13);
        await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, vandaag);

        await using (var tx = await conn.BeginTransactionAsync())
        {
            await ZetMaandAsync(conn, tx, Club, 9);
            await PostgresSeasonProcedures.HerbouwSeizoenenVoorClubAsync(conn, tx, Club, vandaag);
            await tx.CommitAsync();
        }

        (await GrenzenAsync(conn, "2026-2027", Club)).Tot.Should().Be(new DateTime(2027, 8, 31));
        (await GrenzenAsync(conn, "2026-2027", "SEASONTWEE")).Tot.Should().Be(new DateTime(2027, 6, 30), "een andere club blijft ongemoeid");
        (await NamenAsync(conn)).Should().Equal("2024-2025", "2025-2026", "2026-2027");
    }

    [PostgresFact]
    public async Task EnsureSeasonsAsync_OngeldigeStartmaandBijDeClub_ValtTerugOpJuli()
    {
        await using var conn = await OpenSchoonAsync(7);
        await using (var kapot = new NpgsqlCommand("UPDATE public.appsettings SET seasonstartmonth = 0 WHERE clubcode = @c", conn))
        {
            kapot.Parameters.AddWithValue("c", Club);
            await kapot.ExecuteNonQueryAsync();
        }

        await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, new DateOnly(2026, 10, 13));

        (await GrenzenAsync(conn, "2026-2027")).Van.Should().Be(new DateTime(2026, 7, 1));
    }

    // ── Codex-review #1743 ronde 1 ──────────────────────────────────────────────────────────────

    /// <summary>P2: een januari-/februariclub krijgt het seizoen van volgend jaar al vóór de jaarwisseling.</summary>
    [PostgresTheory]
    [InlineData(1, 10, 31, 11, 1)]
    [InlineData(2, 11, 30, 12, 1)]
    public async Task EnsureSeasonsAsync_JanuariOfFebruari_RoltVoorDeJaarwisselingDoor(
        int startMaand, int maandVoor, int dagVoor, int maandOp, int dagOp)
    {
        await using var conn = await OpenSchoonAsync(startMaand);

        await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, new DateOnly(2026, maandVoor, dagVoor));
        (await NamenAsync(conn)).Should().NotContain("2027-2028", "één dag vóór de drempel");

        (await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, new DateOnly(2026, maandOp, dagOp))).Should().Be(1);
        (await NamenAsync(conn)).Should().Contain("2027-2028", "op de drempel, twee maanden vóór de start in 2027");
        (await GrenzenAsync(conn, "2027-2028")).Van.Should().Be(new DateTime(2027, startMaand, 1));
    }

    /// <summary>
    /// P1, overgang: de vorige codeversie schrijft tijdens de deploy nog met haar eigen statement in
    /// <c>public.season</c> (letterlijk overgenomen uit origin/main, <c>PostgresSeasonProcedures.VoegSeizoenToeAsync</c>
    /// vóór #1715). Na migratie 043 moet dat blijven werken, de nieuwe code moet daarna haar eigen tabel vullen,
    /// en een herhaalde uitvoering van de migratie mag niets veranderen.
    /// </summary>
    [PostgresFact]
    public async Task Overgang_VorigeCodeSchrijftNogInPublicSeason_NieuweCodeEnHerhaaldeMigratieWerken()
    {
        const string VorigeWriter = @"
            INSERT INTO public.season (name, datefrom, dateuntil)
            VALUES (@name, @datefrom, @dateuntil)
            ON CONFLICT (name) DO NOTHING";
        await using var conn = await OpenSchoonAsync(9);
        await using (var leeg = new NpgsqlCommand("DELETE FROM public.season", conn))
            await leeg.ExecuteNonQueryAsync();

        // 1. Vorige code op het schema van deze PR: een insert en een herhaling (conflict) slagen allebei.
        for (var i = 0; i < 2; i++)
        {
            await using var oud = new NpgsqlCommand(VorigeWriter, conn);
            oud.Parameters.AddWithValue("name", "2025-2026");
            oud.Parameters.AddWithValue("datefrom", new DateTime(2025, 7, 1));
            oud.Parameters.AddWithValue("dateuntil", new DateTime(2026, 6, 30));
            (await oud.ExecuteNonQueryAsync()).Should().Be(i == 0 ? 1 : 0);
        }

        // 2. Herhaalde migratie op een database met clubloze rijen: de club zonder rijen krijgt ze per eigen maand.
        await VoerMigratie043UitAsync(conn);
        (await GrenzenAsync(conn, "2025-2026")).Should().Be((new DateTime(2025, 9, 1), new DateTime(2026, 8, 31)));

        // 3. Nieuwe code: vult de eigen tabel aan, raakt public.season niet.
        await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, new DateOnly(2026, 10, 13));
        (await NamenAsync(conn)).Should().Equal("2025-2026", "2026-2027");
        await using (var oudTelling = new NpgsqlCommand("SELECT COUNT(*) FROM public.season", conn))
            ((long)(await oudTelling.ExecuteScalarAsync())!).Should().Be(1, "de nieuwe code schrijft niet meer in de oude tabel");

        // 4. Nog een keer de migratie: de club heeft rijen, dus niets verandert (ook niet na een herbouw).
        var voor = await NamenAsync(conn);
        await VoerMigratie043UitAsync(conn);
        (await NamenAsync(conn)).Should().Equal(voor);
    }

    /// <summary>
    /// P2, overlappende wijzigingen: twee transacties die elk de maand wijzigen en in dezelfde transactie de
    /// seizoenen herbouwen, gelijktijdig. De tweede wacht op de rijlock van de eerste; de eindtoestand hoort bij
    /// de laatst vastgelegde maand. Het eindpunt-niveau (AdminSettingsFunction) staat in
    /// FunctionApp.Postgres.Tests/AdminSettingsSeizoenIntegrationTests.
    /// </summary>
    [PostgresFact]
    public async Task HerbouwSeizoenenVoorClubAsync_TweeOverlappendeWijzigingen_KalenderVolgtDeLaatsteMaand()
    {
        await using var conn = await OpenSchoonAsync(7);
        var vandaag = new DateOnly(2026, 10, 13);
        await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, vandaag);

        await using var eerste = new NpgsqlConnection(ConnectionString);
        await eerste.OpenAsync();
        await using var tx1 = await eerste.BeginTransactionAsync();
        await ZetMaandAsync(eerste, tx1, Club, 8);
        await PostgresSeasonProcedures.HerbouwSeizoenenVoorClubAsync(eerste, tx1, Club, vandaag);

        var tweede = Task.Run(async () =>
        {
            await using var c2 = new NpgsqlConnection(ConnectionString);
            await c2.OpenAsync();
            await using var tx2 = await c2.BeginTransactionAsync();
            await ZetMaandAsync(c2, tx2, Club, 9);
            await PostgresSeasonProcedures.HerbouwSeizoenenVoorClubAsync(c2, tx2, Club, vandaag);
            await tx2.CommitAsync();
        });
        await Task.Delay(300);
        tweede.IsCompleted.Should().BeFalse("de tweede wijziging wacht op de rijlock van de eerste");
        await tx1.CommitAsync();
        await tweede;

        (await GrenzenAsync(conn, "2026-2027")).Should().Be((new DateTime(2026, 9, 1), new DateTime(2027, 8, 31)));
        (await NamenAsync(conn)).Should().Equal("2024-2025", "2025-2026", "2026-2027");
    }

    /// <summary>P2, afgebroken wijziging: wordt de transactie teruggedraaid, dan blijven maand en kalender samen staan.</summary>
    [PostgresFact]
    public async Task HerbouwSeizoenenVoorClubAsync_AfgebrokenWijziging_LaatMaandEnKalenderOngemoeid()
    {
        await using var conn = await OpenSchoonAsync(7);
        var vandaag = new DateOnly(2026, 10, 13);
        await PostgresSeasonProcedures.EnsureSeasonsAsync(conn, vandaag);

        await using (var tx = await conn.BeginTransactionAsync())
        {
            await ZetMaandAsync(conn, tx, Club, 9);
            await PostgresSeasonProcedures.HerbouwSeizoenenVoorClubAsync(conn, tx, Club, vandaag);
            await tx.RollbackAsync();
        }

        (await GrenzenAsync(conn, "2026-2027")).Should().Be((new DateTime(2026, 7, 1), new DateTime(2027, 6, 30)));
        await using var maand = new NpgsqlCommand("SELECT seasonstartmonth FROM public.appsettings WHERE clubcode = @c", conn);
        maand.Parameters.AddWithValue("c", Club);
        ((int)(await maand.ExecuteScalarAsync())!).Should().Be(7);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static async Task ZetMaandAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string club, int maand)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE public.appsettings SET seasonstartmonth = @m WHERE clubcode = @c", conn, tx);
        cmd.Parameters.AddWithValue("m", maand);
        cmd.Parameters.AddWithValue("c", club);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Voert het migratiebestand 043 letterlijk opnieuw uit (zoals een herhaalde deploy zonder ledger).</summary>
    private static async Task VoerMigratie043UitAsync(NpgsqlConnection conn)
    {
        var map = new DirectoryInfo(AppContext.BaseDirectory);
        while (map is not null && !File.Exists(Path.Combine(map.FullName, "AGENTS.md")))
            map = map.Parent;
        var pad = Path.Combine(map?.FullName ?? throw new InvalidOperationException("Repositorywortel niet gevonden."),
            "Database.Postgres", "migrations", "043_clubseason.sql");
        await using var cmd = new NpgsqlCommand(await File.ReadAllTextAsync(pad), conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<NpgsqlConnection> OpenSchoonAsync(int seasonStartMonth = 7)
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        // Vormherstel vóór inhoud — zelfde noodzaak als FunctionApp.Postgres.Tests/HisTabelVorm.cs,
        // en om precies dezelfde reden (issue #925): twee andere testklassen in dít project
        // (PostgresPlannerViewIntegrationTests, TeambegeleidingImporterIntegrationTests) droppen
        // public.appsettings en bouwen hem terug met alleen clubcode/accommodatie/syncenabled.
        // xUnit legt de klassevolgorde niet vast, dus zonder deze stap slaagt deze klasse alleen
        // wanneer ze toevallig als eerste draait. De twee ALTER-regels zijn letterlijk die uit
        // migratie 003_admin_tables.sql; op een correct gemigreerde database zijn ze een no-op.
        //
        // #925 gaf dit project een eigen database, maar dat lost dít geval niet op: de slopende
        // klassen zitten in hetzelfde project en draaien dus tegen dezelfde database als deze.
        await using (var herstel = new NpgsqlCommand(
            "ALTER TABLE public.appsettings ADD COLUMN IF NOT EXISTS clubname VARCHAR(100) NOT NULL DEFAULT ''; " +
            "ALTER TABLE public.appsettings ADD COLUMN IF NOT EXISTS seasonstartmonth INTEGER NOT NULL DEFAULT 7;", conn))
        {
            await herstel.ExecuteNonQueryAsync();
        }

        // Bekende beginstand. De procedure loopt over alle clubs in appsettings (elk met de eigen
        // startmaand, #1715), dus élke andere rij moet weg — anders voegt een restant van een andere
        // testklasse seizoenen toe die deze tests niet verwachten.
        await using (var reset = new NpgsqlCommand(
            "DELETE FROM public.clubseason; " +
            "DELETE FROM public.appsettings; " +
            "INSERT INTO public.appsettings (clubcode, clubname, seasonstartmonth) VALUES (@club, 'Season Test', @maand);", conn))
        {
            reset.Parameters.AddWithValue("club", Club);
            reset.Parameters.AddWithValue("maand", seasonStartMonth);
            await reset.ExecuteNonQueryAsync();
        }
        return conn;
    }

    private static async Task ZaaiAsync(NpgsqlConnection conn, int jaar)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO public.clubseason (clubcode, name, datefrom, dateuntil) VALUES (@c, @n, @v, @t) ON CONFLICT (clubcode, name) DO NOTHING", conn);
        cmd.Parameters.AddWithValue("c", Club);
        cmd.Parameters.AddWithValue("n", $"{jaar}-{jaar + 1}");
        cmd.Parameters.AddWithValue("v", new DateTime(jaar, 7, 1));
        cmd.Parameters.AddWithValue("t", new DateTime(jaar + 1, 6, 30));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> NamenAsync(NpgsqlConnection conn, string club = Club)
    {
        await using var cmd = new NpgsqlCommand("SELECT name FROM public.clubseason WHERE clubcode = @c ORDER BY name", conn);
        cmd.Parameters.AddWithValue("c", club);
        var namen = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) namen.Add(reader.GetString(0));
        return namen;
    }

    /// <summary>
    /// Dezelfde berekening als <c>PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync</c>, hier lokaal
    /// omdat die klasse in <c>FunctionApp.Postgres</c> leeft en zijn connectiestring uit de
    /// procesconfiguratie haalt. Bewust dezelfde query én dezelfde afronding, zodat de meting
    /// hierboven over de echte formule gaat en niet over een benadering ervan.
    /// </summary>
    private static async Task<int> WeekOffsetTotSeizoenseindeAsync(NpgsqlConnection conn, DateOnly vandaag)
    {
        await using var cmd = new NpgsqlCommand("SELECT MAX(dateuntil) FROM public.clubseason WHERE clubcode = @c", conn);
        cmd.Parameters.AddWithValue("c", Club);
        var result = await cmd.ExecuteScalarAsync();
        // DateOnly, niet DateTime: Npgsql 10 leest een DATE-kolom via ExecuteScalar als DateOnly.
        // Deze assertie is precies de reden dat de helper hierboven de échte formule herhaalt —
        // hij viel om bij de Npgsql-upgrade, net als de productiecode die hij naspeelt.
        result.Should().BeOfType<DateOnly>("er moet minstens één seizoen in de tabel staan");
        var eind = (DateOnly)result!;
        return (int)Math.Ceiling((eind.ToDateTime(TimeOnly.MinValue) - vandaag.ToDateTime(TimeOnly.MinValue)).TotalDays / 7.0);
    }

    private static async Task<(DateTime Van, DateTime Tot)> GrenzenAsync(NpgsqlConnection conn, string naam, string club = Club)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT datefrom, dateuntil FROM public.clubseason WHERE clubcode = @c AND name = @n", conn);
        cmd.Parameters.AddWithValue("c", club);
        cmd.Parameters.AddWithValue("n", naam);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"seizoen {naam} moet bestaan");
        return (reader.GetDateTime(0), reader.GetDateTime(1));
    }
}
