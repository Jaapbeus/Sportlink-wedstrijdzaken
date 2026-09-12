using Database.Postgres.Tests;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Npgsql;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Integratietests voor <see cref="EmailReplyPolicyService"/> (#972 — port van
/// EmailProcessorFunction) tegen een echte Postgres-instantie.
/// <para>
/// De SQL Server-tier test dit met een <c>RecordingEmailPersistenceService</c>-fake
/// (<c>FunctionApp.Tests/Email/EmailReplyPolicyServiceTests.cs</c>). Die abstractie bestaat op
/// deze tier bewust niet — <see cref="EmailReplyPolicyService"/> roept
/// <see cref="SqlEmailPersistenceRepository"/> rechtstreeks aan met de connectiestring (zie de
/// klassekop van dat bestand), dus is een echte database het natuurlijke testpunt. De Graph-kant
/// blijft gefaket (<see cref="FakeEmailGraphService"/>) — nooit een echte mailbox aanraken.
/// </para>
/// </summary>
public class EmailReplyPolicyServiceIntegrationTests
{
    private const string ClubCode = "testclub-replypolicy";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    private static InkomendBericht Bericht(string messageId, string? conversationId = null) => new()
    {
        MessageId = messageId,
        ConversationId = conversationId,
        Afzender = "afzender@voorbeeld.test",
        Onderwerp = "Test",
        OntvangstDatum = new DateTime(2026, 3, 14, 10, 0, 0, DateTimeKind.Utc),
        Body = "Test"
    };

    private static Task<int> NieuweRijAsync(string messageId) =>
        SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(ConnectionString, Bericht(messageId), ClubCode);

    /// <summary>
    /// Review mode moet een te beoordelen antwoord opleveren. Zonder <c>reviewRecipient</c> gaat er
    /// nog steeds geen mail de deur uit.
    /// </summary>
    [PostgresFact]
    public async Task ReviewMode_ZonderReviewRecipient_SlaatVoorgesteldAntwoordOp_EnVerstuurtNiet()
    {
        await SchoonAsync();
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-review-{Guid.NewGuid():N}";
        var id = await NieuweRijAsync(messageId);
        var buildCalled = false;

        var result = await service.HandelReplyFlowAfAsync(
            ConnectionString,
            id,
            Bericht(messageId),
            new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            "{}",
            reviewMode: true,
            reviewRecipient: null,
            graphService: graph,
            bouwTemplateAntwoordAsync: () =>
            {
                buildCalled = true;
                return Task.FromResult(("subj", "voorgestelde-body"));
            },
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        buildCalled.Should().BeTrue();
        graph.SentReplies.Should().BeEmpty();
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(mid => mid == messageId);

        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand!.Status.Should().Be("Review");
        stand.AntwoordVerstuurd.Should().BeFalse();
    }

    /// <summary>Het voorstel gaat ook naar <c>reviewRecipient</c>, nooit naar de originele afzender.</summary>
    [PostgresFact]
    public async Task ReviewMode_MetReviewRecipient_VerstuurtTestantwoordNaarRecipient()
    {
        await SchoonAsync();
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-review-recipient-{Guid.NewGuid():N}";
        var id = await NieuweRijAsync(messageId);

        var result = await service.HandelReplyFlowAfAsync(
            ConnectionString,
            id,
            Bericht(messageId, conversationId: "conv-1"),
            new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            "{}",
            reviewMode: true,
            reviewRecipient: "reviewer@voorbeeld.test",
            graphService: graph,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "voorgestelde-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        graph.SentReplies.Should().ContainSingle(r =>
            r.To == "reviewer@voorbeeld.test" && r.Subject == "subj" && r.ConversationId == "conv-1");
    }

    [PostgresFact]
    public async Task GeenReplyNodig_ZetStatusEnHandmatigePlanningLabel()
    {
        await SchoonAsync();
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-geenreply-{Guid.NewGuid():N}";
        var id = await NieuweRijAsync(messageId);

        var result = await service.HandelReplyFlowAfAsync(
            ConnectionString,
            id,
            Bericht(messageId),
            new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck },
            JsonConvert.SerializeObject(new { beschikbaar = true }),
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);
        graph.SentReplies.Should().BeEmpty();
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Handmatige planning"));
        graph.MarkedAsReadIds.Should().ContainSingle(mid => mid == messageId);

        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand!.Status.Should().Be("GeenAntwoordNodig");
    }

    [PostgresFact]
    public async Task ReplyVersturen_SlaatAntwoordOp_EnMarkeertGelezen()
    {
        await SchoonAsync();
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-versturen-{Guid.NewGuid():N}";
        var id = await NieuweRijAsync(messageId);

        var result = await service.HandelReplyFlowAfAsync(
            ConnectionString,
            id,
            Bericht(messageId),
            new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        graph.SentReplies.Should().ContainSingle(r => r.To == "afzender@voorbeeld.test" && r.Subject == "antwoord-subject");
        graph.MarkedAsReadIds.Should().ContainSingle(mid => mid == messageId);

        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand!.AntwoordVerstuurd.Should().BeTrue();
        stand.Status.Should().Be("AntwoordVerstuurd");
    }

    [PostgresFact]
    public async Task SendFout_UpdateFout_EnGeeftVerzendFoutTerug()
    {
        await SchoonAsync();
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService { ThrowOnSendReply = true };
        var messageId = $"msg-sendfout-{Guid.NewGuid():N}";
        var id = await NieuweRijAsync(messageId);

        var result = await service.HandelReplyFlowAfAsync(
            ConnectionString,
            id,
            Bericht(messageId),
            new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: _ => "sanitized",
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.VerzendFout);
        graph.MarkedAsReadIds.Should().BeEmpty();

        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand!.Status.Should().Be("Fout");
        stand.VerzendPogingOnbeslist.Should().BeFalse(
            "de verzendintentie moet gewist zijn na een aantoonbaar mislukte verzending — anders zou de "
            + "volgende poll dit als 'uitkomst onbekend' zien en niet opnieuw proberen");
    }

    /// <summary>
    /// De kern van de bescherming tegen een dubbel antwoord: de verzendintentie moet al in de
    /// database staan vóórdat de mail de deur uit gaat. Wordt de invocatie daartussen hard
    /// afgebroken, dan is er een spoor en stuurt de volgende poll geen tweede antwoord.
    /// </summary>
    [PostgresFact]
    public async Task VerzendIntentie_WordtVastgelegdVoordatErVerstuurdWordt()
    {
        await SchoonAsync();
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-intentie-{Guid.NewGuid():N}";
        var id = await NieuweRijAsync(messageId);
        EmailVerwerkingStand? standBijVersturen = null;

        // Synchrone hook — vraagt de stand ook synchroon op. Dit is een testhook, geen
        // productiecode, dus GetAwaiter().GetResult() is hier acceptabel.
        graph.OnSendReply = () =>
            standBijVersturen = SqlEmailPersistenceRepository
                .HaalVerwerkingStandOpAsync(ConnectionString, messageId)
                .GetAwaiter().GetResult();

        var result = await service.HandelReplyFlowAfAsync(
            ConnectionString,
            id,
            Bericht(messageId),
            new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            "{}",
            reviewMode: false,
            reviewRecipient: null,
            graphService: graph,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        result.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        standBijVersturen.Should().NotBeNull();
        standBijVersturen!.VerzendPogingOnbeslist.Should().BeTrue(
            "op het moment van versturen staat de intentie al vast, maar is er nog geen antwoord "
            + "vastgelegd — dat gebeurt pas ná een geslaagde verzending");
    }

    /// <summary>
    /// In review mode gaat er niets de deur uit, dus hoort er ook geen verzendintentie te staan.
    /// </summary>
    [PostgresFact]
    public async Task ReviewMode_LegtGeenVerzendIntentieVast()
    {
        await SchoonAsync();
        var service = new EmailReplyPolicyService();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-review-geenintentie-{Guid.NewGuid():N}";
        var id = await NieuweRijAsync(messageId);

        await service.HandelReplyFlowAfAsync(
            ConnectionString,
            id,
            Bericht(messageId),
            new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            "{}",
            reviewMode: true,
            reviewRecipient: null,
            graphService: graph,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("subj", "body")),
            sanitizeFoutMelding: s => s,
            log: NullLogger.Instance);

        graph.SentReplies.Should().BeEmpty();
        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand!.VerzendPogingOnbeslist.Should().BeFalse();
    }

    private static async Task SchoonAsync() =>
        await ExecAsync("DELETE FROM planner.emailverwerking WHERE clubcode = @club", ("club", ClubCode));

    private static async Task ExecAsync(string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        await cmd.ExecuteNonQueryAsync();
    }
}
