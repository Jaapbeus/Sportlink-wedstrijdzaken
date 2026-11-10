using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public class TeamContactDoorstuurAfhandelingTests
{
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
    public void ReviewProposal_DoesNotClaimForwardingHappened()
    {
        var (subject, body) = TeamContactDoorstuurAfhandeling.BouwReviewVoorstel();

        subject.Should().Contain("begeleiding");
        body.Should().Contain("niet bevestigd");
        body.Should().Contain("coördinator beoordeelt");
        body.Should().NotContain("is doorgestuurd");
    }
}
