using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Email;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het derde acceptatiecriterium van issue 889 vast: *"e-mail-persistentie (audit-trail,
/// dedup) is met een test vastgelegd tegen een Postgres-instantie"*.
///
/// <para>
/// #904 verifieerde dit al empirisch, maar met een wegwerpharnas — het criterium vraagt expliciet
/// om een test. Dit is dezelfde meting, blijvend en env-gestuurd (#866). Zie de klasse-doc-comment
/// van <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.
/// </para>
///
/// <para>
/// <b>Waarom de dedup-assertie op de exceptie-vertaling zit en niet op een rijaantal.</b> De
/// SQL Server-tier herkent een dubbele <c>MessageId</c> aan <c>SqlException.Number 2601/2627</c>;
/// de Postgres-tier moet daarvoor <c>PostgresException.SqlState</c> gebruiken. Een test die alleen
/// "er staat één rij" zou slagen ook als die vertaling ontbrak — dan zou de aanroeper een rauwe
/// <see cref="PostgresException"/> krijgen in plaats van de <c>DubbeleMessageIdException</c> waar
/// hij op filtert, en zou een dubbel binnengekomen e-mail de verwerking laten crashen in plaats van
/// netjes overslaan.
/// </para>
/// </summary>
public class PostgresEmailPersistenceIntegrationTests
{
    private const string ClubCode = "testclub-email";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    private static InkomendBericht Bericht(string messageId, string? conversationId = null) => new()
    {
        MessageId = messageId,
        ConversationId = conversationId,
        // Fictieve waarden conform CLAUDE.md's AVG-uitzonderingenlijst — nooit echte adressen.
        Afzender = "trainer@voorbeeld.nl",
        Onderwerp = "Verzoek verplaatsen wedstrijd",
        OntvangstDatum = new DateTime(2026, 3, 14, 10, 0, 0, DateTimeKind.Utc),
        Body = "Kunnen we zaterdag een uur later spelen?",
    };

    [PostgresFact]
    public async Task InsertEmailVerwerking_TweemaalDezelfdeMessageId_LevertDubbeleMessageIdException()
    {
        await SchoonAsync();
        var messageId = $"msg-dedup-{Guid.NewGuid():N}";

        var id = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(
            ConnectionString, Bericht(messageId), ClubCode);
        id.Should().BeGreaterThan(0, "RETURNING id is de Postgres-vertaling van SCOPE_IDENTITY()");

        var tweede = async () => await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(
            ConnectionString, Bericht(messageId), ClubCode);

        await tweede.Should().ThrowAsync<Exception>()
            .Where(ex => ex.GetType().Name == "DubbeleMessageIdException",
                "de unique-violation moet naar de tier-onafhankelijke DubbeleMessageIdException "
                + "vertaald worden (SqlState 23505), niet als rauwe PostgresException doorlekken");

        (await CountAsync("SELECT count(*) FROM planner.emailverwerking WHERE messageid = @messageid", messageId))
            .Should().Be(1, "de tweede poging mag geen tweede rij achterlaten");
    }

    [PostgresFact]
    public async Task StatusEnPogingen_WordenBijgehoudenEnZijnLosVanElkaarOpvraagbaar()
    {
        await SchoonAsync();
        var messageId = $"msg-status-{Guid.NewGuid():N}";

        var id = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(
            ConnectionString, Bericht(messageId), ClubCode);

        var beginstand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        beginstand.Should().NotBeNull();
        beginstand!.VerwerkingId.Should().Be(id);
        beginstand.Status.Should().Be("Ontvangen");
        beginstand.Pogingen.Should().Be(1, "de insert zet pogingen op 1");
        beginstand.AntwoordVerstuurd.Should().BeFalse();

        await SqlEmailPersistenceRepository.VerhoogPogingenAsync(ConnectionString, id);
        await SqlEmailPersistenceRepository.UpdateStatusAsync(
            ConnectionString, id, EmailStatus.Geclassificeerd, "{\"Type\":\"Verzetverzoek\"}");

        var na = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId);
        na!.Pogingen.Should().Be(2);
        na.Status.Should().Be("Geclassificeerd");

        // verzoektype wordt uit de JSON gehaald — de Postgres-vertaling van dezelfde JObject-parse
        // op de SQL Server-tier; een lege of onparsebare payload valt terug op "Onbekend".
        (await ScalarAsync<string?>(
                "SELECT verzoektype FROM planner.emailverwerking WHERE messageid = @messageid", messageId))
            .Should().Be("Verzetverzoek");
    }

    /// <summary>
    /// <c>IsBeantwoord</c> moet losstaan van het te anonimiseren <c>VerstuurdNaar</c>-veld: de
    /// AVG-opschoonprocedure (#861) wist het ontvangeradres na de bewaartermijn, en als de
    /// "is er al geantwoord?"-vraag daarvan afhing, zou een opgeschoonde rij daarna opnieuw
    /// beantwoord worden.
    /// </summary>
    [PostgresFact]
    public async Task AntwoordVerstuurd_BlijftWaarNadatHetOntvangeradresIsGeanonimiseerd()
    {
        await SchoonAsync();
        var messageId = $"msg-avg-{Guid.NewGuid():N}";

        var id = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(
            ConnectionString, Bericht(messageId), ClubCode);
        await SqlEmailPersistenceRepository.UpdateAntwoordVerstuurdAsync(
            ConnectionString, id, verstuurdNaar: "trainer@voorbeeld.nl",
            antwoordEmail: "Prima, we verplaatsen naar 15:00.");

        (await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId))!
            .AntwoordVerstuurd.Should().BeTrue();

        await ExecAsync("UPDATE planner.emailverwerking SET verstuurdnaar = NULL WHERE id = @id", ("id", id));

        (await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(ConnectionString, messageId))!
            .AntwoordVerstuurd.Should().BeTrue(
                "isbeantwoord is een eigen kolom en mag niet uit de aanwezigheid van verstuurdnaar afgeleid worden");
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

    private static async Task<long> CountAsync(string sql, string messageId) =>
        await ScalarAsync<long>(sql, messageId);

    private static async Task<T?> ScalarAsync<T>(string sql, string messageId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("messageid", messageId);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }
}
