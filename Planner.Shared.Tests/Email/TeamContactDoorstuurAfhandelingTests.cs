using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public class TeamContactDoorstuurAfhandelingTests
{
    [Fact]
    public async Task Adapter_SendsOnceWithCapturedSafeMessageAndReplyTo()
    {
        var messages = new List<(string Recipient, string Subject, string Body, string? ReplyTo, string? Audit)>();
        var request = new TeamContactDoorstuurVerzoek("JO13-1", "trainer@voorbeeld.nl", "Vraagsteller", "Mijn vraag");
        var outcome = await TeamContactDoorstuurAfhandeling.VerstuurViaAdapterAsync(
            request, _ => Task.FromResult<string?>("coach@allstars-fc.test"),
            () => "planner@allstars-fc.test",
            (recipient, subject, body, replyTo, audit) =>
            {
                messages.Add((recipient, subject, body, replyTo, audit));
                return Task.CompletedTask;
            }, NullLogger.Instance);

        outcome.Should().Be(TeamContactDoorstuurVerzendresultaat.Geaccepteerd);
        messages.Should().ContainSingle();
        messages[0].Body.Should().Contain("Mijn vraag");
        messages[0].ReplyTo.Should().Be("trainer@voorbeeld.nl");
    }

    [Fact]
    public async Task Adapter_NoRecipient_DoesNotCallGraph()
    {
        var graphCalls = 0;
        var outcome = await TeamContactDoorstuurAfhandeling.VerstuurViaAdapterAsync(
            new TeamContactDoorstuurVerzoek("JO13-1", "trainer@voorbeeld.nl", "Vraagsteller", "Mijn vraag"),
            _ => Task.FromResult<string?>(null), () => null,
            (_, _, _, _, _) => { graphCalls++; return Task.CompletedTask; }, NullLogger.Instance);

        outcome.Should().Be(TeamContactDoorstuurVerzendresultaat.GeenOntvanger);
        graphCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Adapter_MissingAuditAddress_WarnsOnceWithoutAddressOrContentAndStillForwards(string? configured)
    {
        var logger = new ListLogger();
        var messages = new List<string?>();
        var outcome = await TeamContactDoorstuurAfhandeling.VerstuurViaAdapterAsync(
            new TeamContactDoorstuurVerzoek("JO13-1", "trainer@voorbeeld.nl", "Vraagsteller", "Geheime vraag"),
            _ => Task.FromResult<string?>("coach@allstars-fc.test"), () => configured,
            (_, _, _, _, audit) => { messages.Add(audit); return Task.CompletedTask; }, logger);

        outcome.Should().Be(TeamContactDoorstuurVerzendresultaat.Geaccepteerd);
        messages.Should().ContainSingle().Which.Should().BeNull();
        logger.Warnings.Should().ContainSingle();
        logger.Warnings[0].Should().NotContain("@").And.NotContain("Geheime vraag")
            .And.NotContain("Vraagsteller").And.NotContain("JO13-1");
    }

    [Fact]
    public async Task Adapter_ConfiguredAuditAddress_PassesBccWithoutWarning()
    {
        var logger = new ListLogger();
        var messages = new List<string?>();
        await TeamContactDoorstuurAfhandeling.VerstuurViaAdapterAsync(
            new TeamContactDoorstuurVerzoek("JO13-1", "trainer@voorbeeld.nl", "Vraagsteller", "Mijn vraag"),
            _ => Task.FromResult<string?>("coach@allstars-fc.test"), () => "planner@allstars-fc.test",
            (_, _, _, _, audit) => { messages.Add(audit); return Task.CompletedTask; }, logger);

        messages.Should().ContainSingle().Which.Should().Be("planner@allstars-fc.test");
        logger.Warnings.Should().BeEmpty();
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class FakePersistentie : ITeamContactDoorstuurPersistentie
    {
        public TeamContactDoorstuurStatus? Status { get; private set; }
        public bool FaalClaim { get; init; }
        public bool FaalTerminalWrite { get; init; }
        public bool NulRijenBijTerminalWrite { get; init; }

        public FakePersistentie(TeamContactDoorstuurStatus? status = TeamContactDoorstuurStatus.NietGestart)
            => Status = status;

        public Task<TeamContactDoorstuurClaim> ClaimAsync(int verwerkingId)
        {
            if (FaalClaim) throw new InvalidOperationException("claim unavailable");
            if (Status == TeamContactDoorstuurStatus.Geaccepteerd)
                return Task.FromResult(TeamContactDoorstuurClaim.AlGeaccepteerd);
            if (Status != TeamContactDoorstuurStatus.NietGestart)
                return Task.FromResult(TeamContactDoorstuurClaim.NietBeschikbaar);

            Status = TeamContactDoorstuurStatus.Bezig;
            return Task.FromResult(TeamContactDoorstuurClaim.Geclaimd);
        }

        public Task<bool> MarkeerTerminalAsync(int verwerkingId, TeamContactDoorstuurStatus status)
        {
            if (FaalTerminalWrite) throw new InvalidOperationException("terminal write unavailable");
            if (NulRijenBijTerminalWrite) return Task.FromResult(false);
            if (Status != TeamContactDoorstuurStatus.Bezig)
                throw new InvalidOperationException("row is not claimed");
            Status = status;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task GraphAcceptedAndDurablyStored_AllowsOnlyResumeWithoutSecondSend()
    {
        var db = new FakePersistentie();
        var graphCalls = 0;
        Task<TeamContactDoorstuurVerzendresultaat> Send()
        {
            graphCalls++;
            return Task.FromResult(TeamContactDoorstuurVerzendresultaat.Geaccepteerd);
        }

        var first = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(7, db, Send);
        var resumed = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(7, db, Send);

        first.Should().Be(TeamContactDoorstuurUitkomst.Doorgestuurd);
        resumed.Should().Be(TeamContactDoorstuurUitkomst.Doorgestuurd);
        db.Status.Should().Be(TeamContactDoorstuurStatus.Geaccepteerd);
        graphCalls.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentDuplicate_CannotClaimOrSendTwice()
    {
        var db = new FakePersistentie();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var graphCalls = 0;
        async Task<TeamContactDoorstuurVerzendresultaat> Send()
        {
            graphCalls++;
            sendStarted.SetResult();
            await finishSend.Task;
            return TeamContactDoorstuurVerzendresultaat.Geaccepteerd;
        }

        var firstTask = TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(70, db, Send);
        await sendStarted.Task;
        var duplicate = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(70, db, Send);
        finishSend.SetResult();
        var first = await firstTask;

        duplicate.Should().Be(TeamContactDoorstuurUitkomst.Onbekend);
        first.Should().Be(TeamContactDoorstuurUitkomst.Doorgestuurd);
        graphCalls.Should().Be(1);
        db.Status.Should().Be(TeamContactDoorstuurStatus.Geaccepteerd);
    }

    [Fact]
    public async Task NoRecipient_IsTerminalAndNeverCallsGraph()
    {
        var db = new FakePersistentie();
        var lookupCalls = 0;
        var graphCalls = 0;

        Task<TeamContactDoorstuurVerzendresultaat> ResolveAndSend()
        {
            lookupCalls++;
            var ontvangerGevonden = false;
            if (!ontvangerGevonden)
                return Task.FromResult(TeamContactDoorstuurVerzendresultaat.GeenOntvanger);
            graphCalls++;
            return Task.FromResult(TeamContactDoorstuurVerzendresultaat.Geaccepteerd);
        }

        var result = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(8, db, ResolveAndSend);
        var retry = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(8, db, ResolveAndSend);

        result.Should().Be(TeamContactDoorstuurUitkomst.NietDoorgestuurd);
        retry.Should().Be(TeamContactDoorstuurUitkomst.Onbekend);
        db.Status.Should().Be(TeamContactDoorstuurStatus.NietVerstuurd);
        lookupCalls.Should().Be(1);
        graphCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(TeamContactDoorstuurVerzendresultaat.NietBevestigd)]
    [InlineData(TeamContactDoorstuurVerzendresultaat.Geaccepteerd)]
    public async Task UnconfirmedSendOrFailedAcceptanceWrite_CannotBeRetried(TeamContactDoorstuurVerzendresultaat sendResult)
    {
        var db = new FakePersistentie { FaalTerminalWrite = sendResult == TeamContactDoorstuurVerzendresultaat.Geaccepteerd };
        var graphCalls = 0;
        Task<TeamContactDoorstuurVerzendresultaat> Send()
        {
            graphCalls++;
            return Task.FromResult(sendResult);
        }

        var first = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(9, db, Send);
        var retry = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(9, db, Send);

        first.Should().Be(TeamContactDoorstuurUitkomst.Onbekend);
        retry.Should().Be(TeamContactDoorstuurUitkomst.Onbekend);
        graphCalls.Should().Be(1);
        db.Status.Should().Be(sendResult == TeamContactDoorstuurVerzendresultaat.Geaccepteerd
            ? TeamContactDoorstuurStatus.Bezig
            : TeamContactDoorstuurStatus.Onbekend);
    }

    [Fact]
    public async Task SendException_BecomesUnknownAndCannotBeRetried()
    {
        var db = new FakePersistentie();
        var graphCalls = 0;
        Task<TeamContactDoorstuurVerzendresultaat> Send()
        {
            graphCalls++;
            throw new InvalidOperationException("ambiguous Graph outcome");
        }

        var first = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(10, db, Send);
        var retry = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(10, db, Send);

        first.Should().Be(TeamContactDoorstuurUitkomst.Onbekend);
        retry.Should().Be(TeamContactDoorstuurUitkomst.Onbekend);
        graphCalls.Should().Be(1);
        db.Status.Should().Be(TeamContactDoorstuurStatus.Onbekend);
    }

    [Fact]
    public async Task ZeroRowAcceptanceWrite_RemainsUnknownAndCannotBeRetried()
    {
        var db = new FakePersistentie { NulRijenBijTerminalWrite = true };
        var calls = 0;
        Task<TeamContactDoorstuurVerzendresultaat> Send()
        {
            calls++;
            return Task.FromResult(TeamContactDoorstuurVerzendresultaat.Geaccepteerd);
        }

        (await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(13, db, Send))
            .Should().Be(TeamContactDoorstuurUitkomst.Onbekend);
        (await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(13, db, Send))
            .Should().Be(TeamContactDoorstuurUitkomst.Onbekend);
        calls.Should().Be(1);
        TeamContactDoorstuurAfhandeling.BouwReviewVoorstel().Inhoud
            .Should().Contain("niet bevestigd").And.NotContain("nog niet doorgestuurd");
    }

    [Fact]
    public async Task ClaimFailure_DoesNotCallGraph()
    {
        var db = new FakePersistentie { FaalClaim = true };
        var graphCalls = 0;

        var result = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(11, db, () =>
        {
            graphCalls++;
            return Task.FromResult(TeamContactDoorstuurVerzendresultaat.Geaccepteerd);
        });

        result.Should().Be(TeamContactDoorstuurUitkomst.Onbekend);
        graphCalls.Should().Be(0);
        db.Status.Should().Be(TeamContactDoorstuurStatus.NietGestart);
    }

    [Fact]
    public async Task LegacyNullState_IsNotClaimable()
    {
        var db = new FakePersistentie(status: null);
        var graphCalls = 0;

        var result = await TeamContactDoorstuurAfhandeling.VoerEenmaligUitAsync(12, db, () =>
        {
            graphCalls++;
            return Task.FromResult(TeamContactDoorstuurVerzendresultaat.Geaccepteerd);
        });

        result.Should().Be(TeamContactDoorstuurUitkomst.Onbekend);
        graphCalls.Should().Be(0);
        db.Status.Should().BeNull();
    }

    [Fact]
    public async Task StandardForwarding_ClaimsLazilySendsOnceToSingleRecipientAndStoresAcceptance()
    {
        var db = new FakePersistentie();
        var persistenceCreated = 0;
        var lookups = new List<string>();
        var sends = new List<(IReadOnlyList<string> Recipients, string Subject, string Body, string? ReplyTo, string? Audit)>();
        var forward = TeamContactDoorstuurAfhandeling.MaakStandaardDoorstuur(
            () => { persistenceCreated++; return db; },
            team => { lookups.Add(team); return Task.FromResult<string?>("coach@allstars-fc.test"); },
            () => "planner@allstars-fc.test",
            (recipients, subject, body, replyTo, audit) =>
            {
                sends.Add((recipients, subject, body, replyTo, audit));
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        persistenceCreated.Should().Be(0, "tier settings for the persistence must be resolved per forwarding call");

        var request = new TeamContactDoorstuurVerzoek("JO13-1", "trainer@voorbeeld.nl", "Vraagsteller", "Mijn vraag");
        var first = await forward(7, request);
        var resumed = await forward(7, request);

        first.Should().Be(TeamContactDoorstuurUitkomst.Doorgestuurd);
        resumed.Should().Be(TeamContactDoorstuurUitkomst.Doorgestuurd);
        persistenceCreated.Should().Be(2);
        db.Status.Should().Be(TeamContactDoorstuurStatus.Geaccepteerd);
        lookups.Should().Equal("JO13-1");
        sends.Should().ContainSingle();
        sends[0].Recipients.Should().Equal("coach@allstars-fc.test");
        sends[0].Body.Should().Contain("Mijn vraag");
        sends[0].ReplyTo.Should().Be("trainer@voorbeeld.nl");
        sends[0].Audit.Should().Be("planner@allstars-fc.test");
    }

    [Fact]
    public async Task StandardForwarding_NoRecipientIsTerminalWithoutGraphCall()
    {
        var db = new FakePersistentie();
        var graphCalls = 0;
        var forward = TeamContactDoorstuurAfhandeling.MaakStandaardDoorstuur(
            () => db,
            _ => Task.FromResult<string?>(null),
            () => "planner@allstars-fc.test",
            (_, _, _, _, _) => { graphCalls++; return Task.CompletedTask; },
            NullLogger.Instance);

        var outcome = await forward(8, new TeamContactDoorstuurVerzoek("JO13-1", "trainer@voorbeeld.nl", "Vraagsteller", "Mijn vraag"));

        outcome.Should().Be(TeamContactDoorstuurUitkomst.NietDoorgestuurd);
        db.Status.Should().Be(TeamContactDoorstuurStatus.NietVerstuurd);
        graphCalls.Should().Be(0);
    }

    [Fact]
    public void ReviewProposal_DoesNotClaimForwardingHappened()
    {
        var (subject, body) = TeamContactDoorstuurAfhandeling.BouwReviewVoorstel();

        subject.Should().Contain("begeleiding");
        body.Should().Contain("niet bevestigd");
        body.Should().Contain("coördinator beoordeelt");
        body.Should().NotContain("is doorgestuurd");
    }
}
