using Database.Postgres;
using Npgsql;
using Planner.Shared.Feedback;

namespace FunctionApp.Postgres.Feedback;

/// <summary>
/// Postgres-implementatie van <see cref="IFeedbackStore"/> (#764, #1476). Tegenhanger:
/// <c>FunctionApp/Feedback/SqlFeedbackStore.cs</c> — bewust een tier-eigen, parallelle
/// implementatie (geen gedeelde providerabstractie). Alle logica die niet over SQL gaat staat in
/// <c>Planner.Endpoints/Feedback</c> en <c>Planner.Shared/Feedback</c>.
///
/// Schema: <c>avg.feedback</c>, <c>avg.feedbacktelemetrie</c>, <c>avg.feedbackinzagelog</c>
/// (migratie 034). Alle tijden zijn UTC (<c>TIMESTAMPTZ</c>).
/// </summary>
internal sealed class PostgresFeedbackStore : IFeedbackStore
{
    private readonly string connectionString;

    internal PostgresFeedbackStore(string connectionString) => this.connectionString = connectionString;

    private const string SamenvattingKolommen =
        "feedbackid, mta_inserted, type, onderwerp, meldernaam, isgeanonimiseerd, melderrol, pagina, issuenummer, issueurl, status";

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<int> TelRecenteMeldingenAsync(string clubCode, string? melderObjectId, DateTime sindsUtc)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM avg.feedback
            WHERE clubcode = @cc AND mta_inserted >= @sinds
              AND (@melder::varchar IS NULL OR melderobjectid = @melder)", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("sinds", sindsUtc);
        cmd.Parameters.AddWithValue("melder", (object?)melderObjectId ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task BewaarAsync(FeedbackNieuw n, IReadOnlyList<FeedbackTelemetrieRegel> telemetrie)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO avg.feedback
                (feedbackid, clubcode, type, onderwerp, beschrijving, vragenantwoorden, issuebody,
                 melderobjectid, meldernaam, melderrol, pagina, appversie, status)
            VALUES
                (@id, @cc, @type, @onderwerp, @beschrijving, @qa, @body, @oid, @naam, @rol, @pagina, @versie, @status)", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", n.FeedbackId);
            cmd.Parameters.AddWithValue("cc", n.ClubCode);
            cmd.Parameters.AddWithValue("type", n.Type);
            cmd.Parameters.AddWithValue("onderwerp", n.Onderwerp.Length > 200 ? n.Onderwerp[..200] : n.Onderwerp);
            cmd.Parameters.AddWithValue("beschrijving", n.Beschrijving);
            cmd.Parameters.AddWithValue("qa", (object?)n.VragenAntwoordenJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("body", n.IssueBody);
            cmd.Parameters.AddWithValue("oid", (object?)n.MelderObjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("naam", (object?)n.MelderNaam ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rol", n.MelderRol);
            cmd.Parameters.AddWithValue("pagina", (object?)n.Pagina ?? DBNull.Value);
            cmd.Parameters.AddWithValue("versie", (object?)n.AppVersie ?? DBNull.Value);
            cmd.Parameters.AddWithValue("status", n.Status);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var regel in telemetrie)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO avg.feedbacktelemetrie (feedbackid, clubcode, bron, payload) VALUES (@id, @cc, @bron, @payload)", conn, tx);
            cmd.Parameters.AddWithValue("id", n.FeedbackId);
            cmd.Parameters.AddWithValue("cc", n.ClubCode);
            cmd.Parameters.AddWithValue("bron", regel.Bron);
            cmd.Parameters.AddWithValue("payload", regel.Payload);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task<bool> ClaimPublicatieAsync(string clubCode, Guid feedbackId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE avg.feedback SET status = @publiceren, mta_modified = NOW()
            WHERE feedbackid = @id AND clubcode = @cc AND status IN (@wacht, @mislukt)", conn);
        cmd.Parameters.AddWithValue("publiceren", FeedbackStatusWaarden.Publiceren);
        cmd.Parameters.AddWithValue("wacht", FeedbackStatusWaarden.WachtOpPublicatie);
        cmd.Parameters.AddWithValue("mislukt", FeedbackStatusWaarden.GitHubMislukt);
        cmd.Parameters.AddWithValue("id", feedbackId);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    public async Task ZetGepubliceerdAsync(string clubCode, Guid feedbackId, int issueNummer, string issueUrl)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE avg.feedback
            SET status = @status, issuenummer = @nr, issueurl = @url, mta_modified = NOW()
            WHERE feedbackid = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("status", FeedbackStatusWaarden.Gepubliceerd);
        cmd.Parameters.AddWithValue("nr", issueNummer);
        cmd.Parameters.AddWithValue("url", issueUrl.Length > 300 ? issueUrl[..300] : issueUrl);
        cmd.Parameters.AddWithValue("id", feedbackId);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ZetPublicatieMisluktAsync(string clubCode, Guid feedbackId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE avg.feedback SET status = @status, mta_modified = NOW() WHERE feedbackid = @id AND clubcode = @cc AND status = @publiceren", conn);
        cmd.Parameters.AddWithValue("status", FeedbackStatusWaarden.GitHubMislukt);
        cmd.Parameters.AddWithValue("publiceren", FeedbackStatusWaarden.Publiceren);
        cmd.Parameters.AddWithValue("id", feedbackId);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<FeedbackLijstResultaat> LijstAsync(string clubCode, FeedbackFilter f)
    {
        await using var conn = await OpenAsync();
        const string where = @"
            WHERE clubcode = @cc
              AND (@type::varchar IS NULL OR type = @type)
              AND (@status::varchar IS NULL OR status = @status)
              AND (@vanaf::timestamptz IS NULL OR mta_inserted >= @vanaf)
              AND (@tot::timestamptz IS NULL OR mta_inserted < @tot)
              AND (@zoek::varchar IS NULL OR onderwerp ILIKE @zoek ESCAPE '\' OR beschrijving ILIKE @zoek ESCAPE '\')";

        void Bind(NpgsqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("cc", clubCode);
            cmd.Parameters.AddWithValue("type", (object?)f.Type ?? DBNull.Value);
            cmd.Parameters.AddWithValue("status", (object?)f.Status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("vanaf", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)f.VanafUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("tot", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)f.TotUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("zoek", (object?)(f.Zoek is null ? null : "%" + EscapeLike(f.Zoek) + "%") ?? DBNull.Value);
        }

        int totaal;
        await using (var count = new NpgsqlCommand("SELECT COUNT(*) FROM avg.feedback" + where, conn))
        {
            Bind(count);
            totaal = Convert.ToInt32(await count.ExecuteScalarAsync());
        }

        var items = new List<FeedbackSamenvatting>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SamenvattingKolommen} FROM avg.feedback{where} ORDER BY mta_inserted DESC LIMIT @limit OFFSET @offset", conn);
        Bind(cmd);
        cmd.Parameters.AddWithValue("limit", f.Limit);
        cmd.Parameters.AddWithValue("offset", f.Offset);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) items.Add(LeesSamenvatting(r));
        return new FeedbackLijstResultaat(items, totaal);
    }

    /// <summary>Wildcards uit de zoektekst onschadelijk maken (\ % _), zodat ze letterlijk gezocht worden.</summary>
    internal static string EscapeLike(string tekst) =>
        tekst.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static FeedbackSamenvatting LeesSamenvatting(NpgsqlDataReader r) => new(
        r.GetGuid(r.GetOrdinal("feedbackid")),
        r.GetFieldValue<DateTime>(r.GetOrdinal("mta_inserted")),
        r.GetString(r.GetOrdinal("type")),
        r.GetString(r.GetOrdinal("onderwerp")),
        r.IsDBNull(r.GetOrdinal("meldernaam")) ? null : r.GetString(r.GetOrdinal("meldernaam")),
        r.GetBoolean(r.GetOrdinal("isgeanonimiseerd")),
        r.GetString(r.GetOrdinal("melderrol")),
        r.IsDBNull(r.GetOrdinal("pagina")) ? null : r.GetString(r.GetOrdinal("pagina")),
        r.IsDBNull(r.GetOrdinal("issuenummer")) ? null : r.GetInt32(r.GetOrdinal("issuenummer")),
        r.IsDBNull(r.GetOrdinal("issueurl")) ? null : r.GetString(r.GetOrdinal("issueurl")),
        r.GetString(r.GetOrdinal("status")));

    public async Task<FeedbackDetail?> GetDetailAsync(string clubCode, Guid feedbackId)
    {
        await using var conn = await OpenAsync();
        FeedbackSamenvatting? samenvatting;
        string beschrijving, body;
        string? qa, versie;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {SamenvattingKolommen}, beschrijving, vragenantwoorden, issuebody, appversie FROM avg.feedback WHERE feedbackid = @id AND clubcode = @cc", conn))
        {
            cmd.Parameters.AddWithValue("id", feedbackId);
            cmd.Parameters.AddWithValue("cc", clubCode);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            samenvatting = LeesSamenvatting(r);
            beschrijving = r.GetString(r.GetOrdinal("beschrijving"));
            qa = r.IsDBNull(r.GetOrdinal("vragenantwoorden")) ? null : r.GetString(r.GetOrdinal("vragenantwoorden"));
            body = r.GetString(r.GetOrdinal("issuebody"));
            versie = r.IsDBNull(r.GetOrdinal("appversie")) ? null : r.GetString(r.GetOrdinal("appversie"));
        }

        var telemetrie = new List<FeedbackTelemetrieRegel>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT bron, payload FROM avg.feedbacktelemetrie WHERE feedbackid = @id AND clubcode = @cc ORDER BY id", conn))
        {
            cmd.Parameters.AddWithValue("id", feedbackId);
            cmd.Parameters.AddWithValue("cc", clubCode);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                telemetrie.Add(new FeedbackTelemetrieRegel(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        }

        return new FeedbackDetail(samenvatting, beschrijving, qa, body, versie, telemetrie);
    }

    public async Task LogInzageAsync(FeedbackInzageNieuw i)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO avg.feedbackinzagelog (clubcode, inziendoorobjectid, inziendoornaam, actie, feedbackid, filter)
            VALUES (@cc, @oid, @naam, @actie, @id, @filter)", conn);
        cmd.Parameters.AddWithValue("cc", i.ClubCode);
        cmd.Parameters.AddWithValue("oid", (object?)i.InzienDoorObjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("naam", (object?)i.InzienDoorNaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actie", i.Actie);
        cmd.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)i.FeedbackId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("filter", (object?)(i.Filter is { Length: > 300 } f ? f[..300] : i.Filter) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<FeedbackInzageLijst> LijstInzageAsync(string clubCode, int limit, int offset)
    {
        await using var conn = await OpenAsync();
        int totaal;
        await using (var count = new NpgsqlCommand("SELECT COUNT(*) FROM avg.feedbackinzagelog WHERE clubcode = @cc", conn))
        {
            count.Parameters.AddWithValue("cc", clubCode);
            totaal = Convert.ToInt32(await count.ExecuteScalarAsync());
        }

        var items = new List<FeedbackInzageRegel>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT mta_inserted, inziendoornaam, actie, feedbackid, filter FROM avg.feedbackinzagelog
            WHERE clubcode = @cc ORDER BY mta_inserted DESC, id DESC LIMIT @limit OFFSET @offset", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("offset", offset);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            items.Add(new FeedbackInzageRegel(
                r.GetFieldValue<DateTime>(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetGuid(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        return new FeedbackInzageLijst(items, totaal);
    }

    public async Task<IReadOnlyList<FeedbackIssueVerwijzing>> TeControlerenIssuesAsync(int max)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT feedbackid, issuenummer FROM avg.feedback
            WHERE isgeanonimiseerd = FALSE AND issuenummer IS NOT NULL AND status = @status
            ORDER BY issuestatusgecontroleerdoputc ASC NULLS FIRST, id
            LIMIT @max", conn);
        cmd.Parameters.AddWithValue("status", FeedbackStatusWaarden.Gepubliceerd);
        cmd.Parameters.AddWithValue("max", max);
        var lijst = new List<FeedbackIssueVerwijzing>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) lijst.Add(new FeedbackIssueVerwijzing(r.GetGuid(0), r.GetInt32(1)));
        return lijst;
    }

    public async Task ZetIssueStatusAsync(Guid feedbackId, DateTime? geslotenOpUtc)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE avg.feedback
            SET issuegeslotenoputc = @gesloten, issuestatusgecontroleerdoputc = NOW(), mta_modified = NOW()
            WHERE feedbackid = @id", conn);
        cmd.Parameters.AddWithValue("gesloten", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)geslotenOpUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", feedbackId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<FeedbackRetentieResultaat> VoerRetentieUitAsync(DateTime nuUtc)
    {
        await using var conn = await OpenAsync();
        var (anon, telemetrie, inzage) = await PostgresCleanupProcedures.CleanupFeedbackAsync(
            conn, nuUtc, FeedbackRetentie.IdentiteitNaSluitingMaanden, FeedbackRetentie.TelemetrieDagen, FeedbackRetentie.InzageLogMaanden);
        return new FeedbackRetentieResultaat(anon, telemetrie, inzage);
    }
}
