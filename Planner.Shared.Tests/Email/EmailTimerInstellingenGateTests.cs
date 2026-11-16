using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class EmailTimerInstellingenGateTests
{
    private static EmailVerwerkingsInstellingenLeesResultaat Snapshot(bool aan) =>
        new(new(aan, true, null, true, false), EmailVerwerkingsInstellingenLeesStatus.Beschikbaar);

    [Fact]
    public async Task LegeInbox_RaaktDatabaseNochSnapshot()
    {
        var calls = new List<string>();
        var result = await EmailTimerInstellingenGate.LeesNaInboxProbeAsync(
            () => { calls.Add("probe"); return Task.FromResult(false); },
            () => { calls.Add("wake"); return Task.CompletedTask; },
            () => { calls.Add("snapshot"); return Task.FromResult(Snapshot(true)); },
            () => calls.Add("probe-fout"), () => calls.Add("lees-fout"),
            _ => calls.Add("uit"));

        result.Should().BeNull();
        calls.Should().Equal("probe");
    }

    [Fact]
    public async Task ProbeFout_StoptZonderDatabase()
    {
        var calls = new List<string>();
        var result = await EmailTimerInstellingenGate.LeesNaInboxProbeAsync(
            () => { calls.Add("probe"); throw new InvalidOperationException(); },
            () => { calls.Add("wake"); return Task.CompletedTask; },
            () => { calls.Add("snapshot"); return Task.FromResult(Snapshot(true)); },
            () => calls.Add("probe-fout"), () => calls.Add("lees-fout"),
            _ => calls.Add("uit"));

        result.Should().BeNull();
        calls.Should().Equal("probe", "probe-fout");
    }

    [Fact]
    public async Task OngelezenMail_WektDatabaseVoorVerseSnapshotEnLaatAlleenIngeschakeldDoor()
    {
        var calls = new List<string>();
        var result = await EmailTimerInstellingenGate.LeesNaInboxProbeAsync(
            () => { calls.Add("probe"); return Task.FromResult(true); },
            () => { calls.Add("wake"); return Task.CompletedTask; },
            () => { calls.Add("snapshot"); return Task.FromResult(Snapshot(true)); },
            () => calls.Add("probe-fout"), () => calls.Add("lees-fout"),
            _ => calls.Add("uit"));

        result.Should().Be(Snapshot(true));
        calls.Should().Equal("probe", "wake", "snapshot");
    }

    [Fact]
    public async Task WekFout_StoptVoorSnapshotEnVerwerking()
    {
        var calls = new List<string>();
        var result = await EmailTimerInstellingenGate.LeesNaInboxProbeAsync(
            () => { calls.Add("probe"); return Task.FromResult(true); },
            () => { calls.Add("wake"); throw new InvalidOperationException(); },
            () => { calls.Add("snapshot"); return Task.FromResult(Snapshot(true)); },
            () => calls.Add("probe-fout"), () => calls.Add("lees-fout"),
            _ => calls.Add("uit"));

        result.Should().BeNull();
        calls.Should().Equal("probe", "wake", "lees-fout");
    }

    [Theory]
    [InlineData(false, EmailVerwerkingsInstellingenLeesStatus.Beschikbaar)]
    [InlineData(true, EmailVerwerkingsInstellingenLeesStatus.Onleesbaar)]
    public async Task UitOfOnleesbaar_StoptNaSnapshot(bool processing, EmailVerwerkingsInstellingenLeesStatus status)
    {
        var calls = new List<string>();
        var snapshot = status == EmailVerwerkingsInstellingenLeesStatus.Onleesbaar
            ? EmailVerwerkingsInstellingenLezer.LeesVeilig(null, false) : Snapshot(processing);
        var result = await EmailTimerInstellingenGate.LeesNaInboxProbeAsync(
            () => { calls.Add("probe"); return Task.FromResult(true); },
            () => { calls.Add("wake"); return Task.CompletedTask; },
            () => { calls.Add("snapshot"); return Task.FromResult(snapshot); },
            () => calls.Add("probe-fout"), () => calls.Add("lees-fout"),
            value => calls.Add("uit:" + value));

        result.Should().BeNull();
        calls.Should().Equal("probe", "wake", "snapshot", "uit:" + status);
    }
}
