using Microsoft.Data.SqlClient;
using Planner.Shared.Feedback;

namespace SportlinkFunction.Feedback;

/// <summary>
/// SQL Server-implementatie van <see cref="IFeedbackStore"/> (#764, #1476). Tegenhanger:
/// <c>FunctionApp.Postgres/Feedback/PostgresFeedbackStore.cs</c> — bewust een tier-eigen, parallelle
/// implementatie (geen gedeelde providerabstractie). Alle logica die niet over SQL gaat staat in
/// <c>Planner.Endpoints/Feedback</c> en <c>Planner.Shared/Feedback</c>.
///
/// Schema: <c>avg.Feedback</c>, <c>avg.FeedbackTelemetrie</c>, <c>avg.FeedbackInzageLog</c>
/// (Database/avg/Tables + Script.PostDeployment1.sql). Alle tijden zijn UTC (<c>GETUTCDATE()</c>).
/// </summary>
internal sealed class SqlFeedbackStore : IFeedbackStore
{
    private readonly string connectionString;

    internal SqlFeedbackStore(string connectionString) => this.connectionString = connectionString;

    private const string SamenvattingKolommen =
        "[FeedbackId], [mta_inserted], [Type], [Onderwerp], [MelderNaam], [IsGeanonimiseerd], [MelderRol], [Pagina], [IssueNummer], [IssueUrl], [Status]";

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static object Db(object? waarde) => waarde ?? DBNull.Value;

    public async Task<int> TelRecenteMeldingenAsync(string clubCode, string? melderObjectId, DateTime sindsUtc)
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT COUNT(*) FROM [avg].[Feedback]
            WHERE [ClubCode] = @Cc AND [mta_inserted] >= @Sinds
              AND (@Melder IS NULL OR [MelderObjectId] = @Melder)", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        cmd.Parameters.Add("@Sinds", System.Data.SqlDbType.DateTime2).Value = sindsUtc;
        cmd.Parameters.Add("@Melder", System.Data.SqlDbType.NVarChar, 64).Value = Db(melderObjectId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task BewaarAsync(FeedbackNieuw n, IReadOnlyList<FeedbackTelemetrieRegel> telemetrie)
    {
        using var conn = await OpenAsync();
        using var tx = conn.BeginTransaction();

        using (var cmd = new SqlCommand(@"
            INSERT INTO [avg].[Feedback]
                ([FeedbackId], [ClubCode], [Type], [Onderwerp], [Beschrijving], [VragenAntwoorden], [IssueBody],
                 [MelderObjectId], [MelderNaam], [MelderRol], [Pagina], [AppVersie], [Status])
            VALUES
                (@Id, @Cc, @Type, @Onderwerp, @Beschrijving, @Qa, @Body, @Oid, @Naam, @Rol, @Pagina, @Versie, @Status)", conn, tx))
        {
            cmd.Parameters.AddWithValue("@Id", n.FeedbackId);
            cmd.Parameters.AddWithValue("@Cc", n.ClubCode);
            cmd.Parameters.AddWithValue("@Type", n.Type);
            cmd.Parameters.AddWithValue("@Onderwerp", n.Onderwerp.Length > 200 ? n.Onderwerp[..200] : n.Onderwerp);
            cmd.Parameters.AddWithValue("@Beschrijving", n.Beschrijving);
            cmd.Parameters.AddWithValue("@Qa", Db(n.VragenAntwoordenJson));
            cmd.Parameters.AddWithValue("@Body", n.IssueBody);
            cmd.Parameters.AddWithValue("@Oid", Db(n.MelderObjectId));
            cmd.Parameters.AddWithValue("@Naam", Db(n.MelderNaam));
            cmd.Parameters.AddWithValue("@Rol", n.MelderRol);
            cmd.Parameters.AddWithValue("@Pagina", Db(n.Pagina));
            cmd.Parameters.AddWithValue("@Versie", Db(n.AppVersie));
            cmd.Parameters.AddWithValue("@Status", n.Status);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var regel in telemetrie)
        {
            using var cmd = new SqlCommand(
                "INSERT INTO [avg].[FeedbackTelemetrie] ([FeedbackId], [ClubCode], [Bron], [Payload]) VALUES (@Id, @Cc, @Bron, @Payload)", conn, tx);
            cmd.Parameters.AddWithValue("@Id", n.FeedbackId);
            cmd.Parameters.AddWithValue("@Cc", n.ClubCode);
            cmd.Parameters.AddWithValue("@Bron", regel.Bron);
            cmd.Parameters.AddWithValue("@Payload", regel.Payload);
            await cmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
    }

    public async Task<bool> ClaimPublicatieAsync(string clubCode, Guid feedbackId)
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [avg].[Feedback] SET [Status] = @Publiceren, [mta_modified] = GETUTCDATE()
            WHERE [FeedbackId] = @Id AND [ClubCode] = @Cc AND [Status] IN (@Wacht, @Mislukt)", conn);
        cmd.Parameters.AddWithValue("@Publiceren", FeedbackStatusWaarden.Publiceren);
        cmd.Parameters.AddWithValue("@Wacht", FeedbackStatusWaarden.WachtOpPublicatie);
        cmd.Parameters.AddWithValue("@Mislukt", FeedbackStatusWaarden.GitHubMislukt);
        cmd.Parameters.AddWithValue("@Id", feedbackId);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    public async Task ZetGepubliceerdAsync(string clubCode, Guid feedbackId, int issueNummer, string issueUrl)
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [avg].[Feedback]
            SET [Status] = @Status, [IssueNummer] = @Nr, [IssueUrl] = @Url, [mta_modified] = GETUTCDATE()
            WHERE [FeedbackId] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Status", FeedbackStatusWaarden.Gepubliceerd);
        cmd.Parameters.AddWithValue("@Nr", issueNummer);
        cmd.Parameters.AddWithValue("@Url", issueUrl.Length > 300 ? issueUrl[..300] : issueUrl);
        cmd.Parameters.AddWithValue("@Id", feedbackId);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ZetPublicatieMisluktAsync(string clubCode, Guid feedbackId)
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand(
            "UPDATE [avg].[Feedback] SET [Status] = @Status, [mta_modified] = GETUTCDATE() WHERE [FeedbackId] = @Id AND [ClubCode] = @Cc AND [Status] = @Publiceren", conn);
        cmd.Parameters.AddWithValue("@Status", FeedbackStatusWaarden.GitHubMislukt);
        cmd.Parameters.AddWithValue("@Publiceren", FeedbackStatusWaarden.Publiceren);
        cmd.Parameters.AddWithValue("@Id", feedbackId);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<FeedbackLijstResultaat> LijstAsync(string clubCode, FeedbackFilter f)
    {
        using var conn = await OpenAsync();
        const string where = @"
            WHERE [ClubCode] = @Cc
              AND (@Type IS NULL OR [Type] = @Type)
              AND (@Status IS NULL OR [Status] = @Status)
              AND (@Vanaf IS NULL OR [mta_inserted] >= @Vanaf)
              AND (@Tot IS NULL OR [mta_inserted] < @Tot)
              AND (@Zoek IS NULL OR [Onderwerp] LIKE @Zoek ESCAPE '\' OR [Beschrijving] LIKE @Zoek ESCAPE '\')";

        void Bind(SqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("@Cc", clubCode);
            cmd.Parameters.Add("@Type", System.Data.SqlDbType.NVarChar, 20).Value = Db(f.Type);
            cmd.Parameters.Add("@Status", System.Data.SqlDbType.NVarChar, 30).Value = Db(f.Status);
            cmd.Parameters.Add("@Vanaf", System.Data.SqlDbType.DateTime2).Value = Db(f.VanafUtc);
            cmd.Parameters.Add("@Tot", System.Data.SqlDbType.DateTime2).Value = Db(f.TotUtc);
            cmd.Parameters.Add("@Zoek", System.Data.SqlDbType.NVarChar, 250).Value = Db(f.Zoek is null ? null : "%" + EscapeLike(f.Zoek) + "%");
        }

        int totaal;
        using (var count = new SqlCommand("SELECT COUNT(*) FROM [avg].[Feedback]" + where, conn))
        {
            Bind(count);
            totaal = Convert.ToInt32(await count.ExecuteScalarAsync());
        }

        var items = new List<FeedbackSamenvatting>();
        using var cmd = new SqlCommand(
            $"SELECT {SamenvattingKolommen} FROM [avg].[Feedback]{where} ORDER BY [mta_inserted] DESC OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY", conn);
        Bind(cmd);
        cmd.Parameters.AddWithValue("@Offset", f.Offset);
        cmd.Parameters.AddWithValue("@Limit", f.Limit);
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) items.Add(LeesSamenvatting(r));
        return new FeedbackLijstResultaat(items, totaal);
    }

    /// <summary>Wildcards uit de zoektekst onschadelijk maken (\ % _ [), zodat ze letterlijk gezocht worden.</summary>
    internal static string EscapeLike(string tekst) =>
        tekst.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static FeedbackSamenvatting LeesSamenvatting(SqlDataReader r) => new(
        r.GetGuid(r.GetOrdinal("FeedbackId")),
        DateTime.SpecifyKind(r.GetDateTime(r.GetOrdinal("mta_inserted")), DateTimeKind.Utc),
        r.GetString(r.GetOrdinal("Type")),
        r.GetString(r.GetOrdinal("Onderwerp")),
        r.IsDBNull(r.GetOrdinal("MelderNaam")) ? null : r.GetString(r.GetOrdinal("MelderNaam")),
        r.GetBoolean(r.GetOrdinal("IsGeanonimiseerd")),
        r.GetString(r.GetOrdinal("MelderRol")),
        r.IsDBNull(r.GetOrdinal("Pagina")) ? null : r.GetString(r.GetOrdinal("Pagina")),
        r.IsDBNull(r.GetOrdinal("IssueNummer")) ? null : r.GetInt32(r.GetOrdinal("IssueNummer")),
        r.IsDBNull(r.GetOrdinal("IssueUrl")) ? null : r.GetString(r.GetOrdinal("IssueUrl")),
        r.GetString(r.GetOrdinal("Status")));

    public async Task<FeedbackDetail?> GetDetailAsync(string clubCode, Guid feedbackId)
    {
        using var conn = await OpenAsync();
        FeedbackSamenvatting? samenvatting;
        string beschrijving, body;
        string? qa, versie;
        using (var cmd = new SqlCommand(
            $"SELECT {SamenvattingKolommen}, [Beschrijving], [VragenAntwoorden], [IssueBody], [AppVersie] FROM [avg].[Feedback] WHERE [FeedbackId] = @Id AND [ClubCode] = @Cc", conn))
        {
            cmd.Parameters.AddWithValue("@Id", feedbackId);
            cmd.Parameters.AddWithValue("@Cc", clubCode);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            samenvatting = LeesSamenvatting(r);
            beschrijving = r.GetString(r.GetOrdinal("Beschrijving"));
            qa = r.IsDBNull(r.GetOrdinal("VragenAntwoorden")) ? null : r.GetString(r.GetOrdinal("VragenAntwoorden"));
            body = r.GetString(r.GetOrdinal("IssueBody"));
            versie = r.IsDBNull(r.GetOrdinal("AppVersie")) ? null : r.GetString(r.GetOrdinal("AppVersie"));
        }

        var telemetrie = new List<FeedbackTelemetrieRegel>();
        using (var cmd = new SqlCommand(
            "SELECT [Bron], [Payload] FROM [avg].[FeedbackTelemetrie] WHERE [FeedbackId] = @Id AND [ClubCode] = @Cc ORDER BY [Id]", conn))
        {
            cmd.Parameters.AddWithValue("@Id", feedbackId);
            cmd.Parameters.AddWithValue("@Cc", clubCode);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                telemetrie.Add(new FeedbackTelemetrieRegel(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        }

        return new FeedbackDetail(samenvatting, beschrijving, qa, body, versie, telemetrie);
    }

    public async Task LogInzageAsync(FeedbackInzageNieuw i)
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand(@"
            INSERT INTO [avg].[FeedbackInzageLog] ([ClubCode], [InzienDoorObjectId], [InzienDoorNaam], [Actie], [FeedbackId], [Filter])
            VALUES (@Cc, @Oid, @Naam, @Actie, @Id, @Filter)", conn);
        cmd.Parameters.AddWithValue("@Cc", i.ClubCode);
        cmd.Parameters.Add("@Oid", System.Data.SqlDbType.NVarChar, 64).Value = Db(i.InzienDoorObjectId);
        cmd.Parameters.Add("@Naam", System.Data.SqlDbType.NVarChar, 200).Value = Db(i.InzienDoorNaam);
        cmd.Parameters.AddWithValue("@Actie", i.Actie);
        cmd.Parameters.Add("@Id", System.Data.SqlDbType.UniqueIdentifier).Value = Db(i.FeedbackId);
        cmd.Parameters.Add("@Filter", System.Data.SqlDbType.NVarChar, 300).Value = Db(i.Filter is { Length: > 300 } f ? f[..300] : i.Filter);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<FeedbackInzageLijst> LijstInzageAsync(string clubCode, int limit, int offset)
    {
        using var conn = await OpenAsync();
        int totaal;
        using (var count = new SqlCommand("SELECT COUNT(*) FROM [avg].[FeedbackInzageLog] WHERE [ClubCode] = @Cc", conn))
        {
            count.Parameters.AddWithValue("@Cc", clubCode);
            totaal = Convert.ToInt32(await count.ExecuteScalarAsync());
        }

        var items = new List<FeedbackInzageRegel>();
        using var cmd = new SqlCommand(@"
            SELECT [mta_inserted], [InzienDoorNaam], [Actie], [FeedbackId], [Filter] FROM [avg].[FeedbackInzageLog]
            WHERE [ClubCode] = @Cc ORDER BY [mta_inserted] DESC, [Id] DESC OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        cmd.Parameters.AddWithValue("@Offset", offset);
        cmd.Parameters.AddWithValue("@Limit", limit);
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            items.Add(new FeedbackInzageRegel(
                DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetGuid(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        return new FeedbackInzageLijst(items, totaal);
    }

    public async Task<IReadOnlyList<FeedbackIssueVerwijzing>> TeControlerenIssuesAsync(int max)
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT TOP (@Max) [FeedbackId], [IssueNummer] FROM [avg].[Feedback]
            WHERE [IsGeanonimiseerd] = 0 AND [IssueNummer] IS NOT NULL AND [Status] = @Status
            ORDER BY [IssueStatusGecontroleerdOpUtc] ASC, [Id]", conn);
        cmd.Parameters.AddWithValue("@Status", FeedbackStatusWaarden.Gepubliceerd);
        cmd.Parameters.AddWithValue("@Max", max);
        var lijst = new List<FeedbackIssueVerwijzing>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) lijst.Add(new FeedbackIssueVerwijzing(r.GetGuid(0), r.GetInt32(1)));
        return lijst;
    }

    public async Task ZetIssueStatusAsync(Guid feedbackId, DateTime? geslotenOpUtc)
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [avg].[Feedback]
            SET [IssueGeslotenOpUtc] = @Gesloten, [IssueStatusGecontroleerdOpUtc] = GETUTCDATE(), [mta_modified] = GETUTCDATE()
            WHERE [FeedbackId] = @Id", conn);
        cmd.Parameters.Add("@Gesloten", System.Data.SqlDbType.DateTime2).Value = Db(geslotenOpUtc);
        cmd.Parameters.AddWithValue("@Id", feedbackId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<FeedbackRetentieResultaat> VoerRetentieUitAsync(DateTime nuUtc)
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand("[avg].[sp_CleanupFeedback]", conn) { CommandType = System.Data.CommandType.StoredProcedure, CommandTimeout = 120 };
        cmd.Parameters.Add("@NuUtc", System.Data.SqlDbType.DateTime2).Value = nuUtc;
        cmd.Parameters.AddWithValue("@IdentiteitMaanden", FeedbackRetentie.IdentiteitNaSluitingMaanden);
        cmd.Parameters.AddWithValue("@TelemetrieDagen", FeedbackRetentie.TelemetrieDagen);
        cmd.Parameters.AddWithValue("@InzageMaanden", FeedbackRetentie.InzageLogMaanden);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return new FeedbackRetentieResultaat(0, 0, 0);
        return new FeedbackRetentieResultaat(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
    }
}
