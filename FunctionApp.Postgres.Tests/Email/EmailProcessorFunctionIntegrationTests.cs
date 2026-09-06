using Database.Postgres.Tests;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Integratietests voor de idempotentie-/foutafhandelingslogica van
/// <see cref="EmailProcessorFunction"/> (#972 — port van EmailProcessorFunction) tegen een echte
/// Postgres-instantie.
/// <para>
/// Woordelijke tests bestaan al op de SQL Server-tier tegen een <c>RecordingEmailPersistenceService</c>-
/// fake (<c>FunctionApp.Tests/Email/EmailProcessorFunctionTests.cs</c>/<c>EmailHardeningTests.cs</c>).
/// Die abstractie bestaat op deze tier bewust niet (zie <c>EmailReplyPolicyService</c>'s
/// klassekop) — <c>BepaalVerwerkingIdAsync</c>/<c>HandelBuitenScopeAsync</c>/
/// <c>RegistreerClassificatieFoutAsync</c> roepen <see cref="SqlEmailPersistenceRepository"/>
/// rechtstreeks aan, dus is een echte database het natuurlijke testpunt — zelfde patroon als
/// <c>PostgresEmailPersistenceIntegrationTests</c>.
/// </para>
/// <para>
/// <b>Bewust niet geport:</b> de exacte concurrency-race uit
/// <c>EmailHardeningTests.BepaalVerwerkingIdAsync_GelijktijdigeRegistratie_StoptZonderFoutstatus</c>
/// (twee invocaties die gelijktijdig dezelfde MessageId registreren). Dat vereist een write die
/// ná de <c>HaalVerwerkingStandOpAsync</c>-lezing van déze aanroep binnenkomt — deterministisch
/// reproduceren zonder een tweede thread/proces is onbetrouwbaar. De vertaling van de
/// unique-violation naar <see cref="DubbeleMessageIdException"/> zelf is al gedekt door
/// <c>PostgresEmailPersistenceIntegrationTests.InsertEmailVerwerking_TweemaalDezelfdeMessageId_LevertDubbeleMessageIdException</c>.
/// </para>
/// </summary>
public class EmailProcessorFunctionIntegrationTests
{
    private const string ClubCode = "testclub-emailprocessor";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    private static InkomendBericht Bericht(string messageId, string afzender = "trainer@voorbeeld.nl") => new()
    {
        MessageId = messageId,
        // Fictieve waarden conform CLAUDE.md's AVG-uitzonderingenlijst — nooit echte adressen.
        Afzender = afzender,
        Onderwerp = "Verzoek verplaatsen wedstrijd",
        OntvangstDatum = new DateTime(2026, 3, 14, 10, 0, 0, DateTimeKind.Utc),
        Body = "Kunnen we zaterdag een uur later spelen?"
    };

    // ── BepaalVerwerkingIdAsync ───────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task BepaalVerwerkingIdAsync_NieuwBericht_MaaktNieuweRijAan()
    {
        await SchoonAsync();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-nieuw-{Guid.NewGuid():N}";

        var verwerkingId = await EmailProcessorFunction.BepaalVerwerkingIdAsync(
            ConnectionString, ClubCode, Bericht(messageId), graph, NullLogger.Instance);

        verwerkingId.Should().NotBeNull();
        graph.MarkedAsReadIds.Should().BeEmpty();
        (await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId))
            .Should().NotBeNull();
    }

    [PostgresFact]
    public async Task BepaalVerwerkingIdAsync_NietAfgerondeRij_HergebruiktIdEnVerhoogtPogingen()
    {
        await SchoonAsync();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-herhaal-{Guid.NewGuid():N}";
        var eersteId = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(ConnectionString, Bericht(messageId), ClubCode);

        var verwerkingId = await EmailProcessorFunction.BepaalVerwerkingIdAsync(
            ConnectionString, ClubCode, Bericht(messageId), graph, NullLogger.Instance);

        verwerkingId.Should().Be(eersteId);
        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand!.Pogingen.Should().Be(2, "de insert zet pogingen op 1, hergebruik verhoogt naar 2");
        graph.MarkedAsReadIds.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task BepaalVerwerkingIdAsync_AlDefinitiefAfgehandeld_MarkeertGelezenEnGeeftNullTerug()
    {
        await SchoonAsync();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-afgerond-{Guid.NewGuid():N}";
        var id = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(ConnectionString, Bericht(messageId), ClubCode);
        await SqlEmailPersistenceRepository.UpdateAntwoordVerstuurdAsync(ConnectionString, id, "trainer@voorbeeld.nl", "Prima, tot zaterdag.");

        var verwerkingId = await EmailProcessorFunction.BepaalVerwerkingIdAsync(
            ConnectionString, ClubCode, Bericht(messageId), graph, NullLogger.Instance);

        verwerkingId.Should().BeNull();
        graph.MarkedAsReadIds.Should().ContainSingle(mid => mid == messageId);
    }

    // ── HandelBuitenScopeAsync ────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task HandelBuitenScopeAsync_BuitenScope_ZetStatus_LabeltEnVerstuurtNiets()
    {
        await SchoonAsync();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-buitenscope-{Guid.NewGuid():N}";
        var id = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(ConnectionString, Bericht(messageId), ClubCode);

        var afgehandeld = await EmailProcessorFunction.HandelBuitenScopeAsync(
            ConnectionString, id, messageId,
            new BerichtClassificatie { Type = VerzoekType.BuitenScope },
            "{\"Type\":4}",
            graph,
            NullLogger.Instance);

        afgehandeld.Should().BeTrue();
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(mid => mid == messageId);
        graph.SentReplies.Should().BeEmpty();

        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand!.Status.Should().Be("BuitenScope");
    }

    [PostgresTheory]
    [InlineData(VerzoekType.BeschikbaarheidCheck)]
    [InlineData(VerzoekType.HerplanVerzoek)]
    [InlineData(VerzoekType.TeamContactOpvragen)]
    public async Task HandelBuitenScopeAsync_BinnenScope_LaatVerwerkingDoorlopen(VerzoekType type)
    {
        await SchoonAsync();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-binnenscope-{Guid.NewGuid():N}";
        var id = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(ConnectionString, Bericht(messageId), ClubCode);

        var afgehandeld = await EmailProcessorFunction.HandelBuitenScopeAsync(
            ConnectionString, id, messageId,
            new BerichtClassificatie { Type = type },
            "{}",
            graph,
            NullLogger.Instance);

        afgehandeld.Should().BeFalse();
        graph.CategoryUpdates.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().BeEmpty();
    }

    // ── RegistreerClassificatieFoutAsync ──────────────────────────────────────────────────────

    [PostgresFact]
    public async Task RegistreerClassificatieFoutAsync_EersteFout_LegtRijVastEnLaatBerichtOngelezen()
    {
        await SchoonAsync();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-classfout-{Guid.NewGuid():N}";

        await EmailProcessorFunction.RegistreerClassificatieFoutAsync(
            ConnectionString, ClubCode, Bericht(messageId), graph, NullLogger.Instance);

        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand.Should().NotBeNull();
        stand!.Status.Should().Be("Fout");

        // Ongelezen laten is bewust: de volgende poll probeert het opnieuw, binnen de pogingenlimiet.
        graph.MarkedAsReadIds.Should().BeEmpty();
        graph.SentReplies.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task RegistreerClassificatieFoutAsync_HerhaaldeFout_VerhoogtDePogingenteller()
    {
        await SchoonAsync();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-classfout-herhaald-{Guid.NewGuid():N}";
        var id = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(ConnectionString, Bericht(messageId), ClubCode);
        await SqlEmailPersistenceRepository.UpdateFoutAsync(ConnectionString, id, "eerdere classificatiefout");

        await EmailProcessorFunction.RegistreerClassificatieFoutAsync(
            ConnectionString, ClubCode, Bericht(messageId), graph, NullLogger.Instance);

        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand!.Pogingen.Should().Be(2);
        graph.MarkedAsReadIds.Should().BeEmpty();
    }

    /// <summary>
    /// Kern van de wachtrij-blokkade: de poll haalt de 10 oudste ongelezen berichten op. Blijft een
    /// structureel falend bericht ongelezen, dan houdt het samen met negen soortgenoten alle nieuwe
    /// post tegen. Na de pogingenlimiet moet het bericht dus als gelezen worden gemarkeerd, met een
    /// definitieve foutstatus als spoor.
    /// </summary>
    [PostgresFact]
    public async Task RegistreerClassificatieFoutAsync_NaMaxPogingen_MarkeertGelezenEnGeeftOp()
    {
        await SchoonAsync();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-classfout-max-{Guid.NewGuid():N}";
        var id = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(ConnectionString, Bericht(messageId), ClubCode);
        for (var i = 1; i < EmailIdempotentie.MaxPogingen; i++)
            await SqlEmailPersistenceRepository.VerhoogPogingenAsync(ConnectionString, id);

        await EmailProcessorFunction.RegistreerClassificatieFoutAsync(
            ConnectionString, ClubCode, Bericht(messageId), graph, NullLogger.Instance);

        graph.MarkedAsReadIds.Should().ContainSingle(mid => mid == messageId);
        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand!.Status.Should().Be("Fout");
        graph.SentReplies.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task RegistreerClassificatieFoutAsync_AlDefinitiefAfgehandeld_RaaktDeRijNietAan()
    {
        await SchoonAsync();
        var graph = new FakeEmailGraphService();
        var messageId = $"msg-classfout-afgerond-{Guid.NewGuid():N}";
        var id = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(ConnectionString, Bericht(messageId), ClubCode);
        await SqlEmailPersistenceRepository.UpdateAntwoordVerstuurdAsync(ConnectionString, id, "trainer@voorbeeld.nl", "Prima, tot zaterdag.");

        await EmailProcessorFunction.RegistreerClassificatieFoutAsync(
            ConnectionString, ClubCode, Bericht(messageId), graph, NullLogger.Instance);

        graph.MarkedAsReadIds.Should().ContainSingle(mid => mid == messageId);
        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        stand!.Status.Should().Be("AntwoordVerstuurd", "een al verstuurd antwoord mag niet als 'Fout' overschreven worden");
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
