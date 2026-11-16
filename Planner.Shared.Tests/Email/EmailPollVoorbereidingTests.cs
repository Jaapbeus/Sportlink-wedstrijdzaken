using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class EmailPollVoorbereidingTests
{
    private static EmailVerwerkingsInstellingenLeesResultaat Snapshot(bool verwerking = true) =>
        new(new(verwerking, true, null, true, false), EmailVerwerkingsInstellingenLeesStatus.Beschikbaar);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZonderGraphOfZonderWerk_RaaktDatabaseEnInstellingenNiet(bool graphBeschikbaar)
    {
        var calls = new List<string>();
        var result = await EmailPollVoorbereiding.LeesInstellingenAsync(
            graphBeschikbaar ? () => { calls.Add("probe"); return Task.FromResult(0); } : null,
            () => { calls.Add("wake"); return Task.CompletedTask; },
            () => { calls.Add("cache"); return false; },
            () => { calls.Add("load"); return Task.CompletedTask; },
            () => { calls.Add("snapshot"); return Task.FromResult(Snapshot()); },
            NullLogger.Instance);

        result.Should().BeNull();
        calls.Should().Equal(graphBeschikbaar ? ["probe"] : []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetWerk_ControleertLaadstatusNaWekkenEnVoorSnapshot(bool ladenMislukt)
    {
        var calls = new List<string>();
        var result = await EmailPollVoorbereiding.LeesInstellingenAsync(
            () => { calls.Add("probe"); return Task.FromResult(2); },
            () => { calls.Add("wake"); return Task.CompletedTask; },
            () => { calls.Add("cache"); return ladenMislukt; },
            () => { calls.Add("load"); return Task.CompletedTask; },
            () => { calls.Add("snapshot"); return Task.FromResult(Snapshot()); },
            NullLogger.Instance);

        calls.Should().Equal(ladenMislukt ? ["probe", "wake", "load", "cache"] : ["probe", "wake", "load", "cache", "snapshot"]);
        if (ladenMislukt) result.Should().BeNull();
        else result.Should().Be(Snapshot());
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("wake")]
    [InlineData("snapshot")]
    public async Task Fout_StoptVoorVolgendeStap(string foutStap)
    {
        var calls = new List<string>();
        Task<T> Stap<T>(string naam, T waarde)
        {
            calls.Add(naam);
            if (naam == foutStap) throw new InvalidOperationException("synthetic failure");
            return Task.FromResult(waarde);
        }
        var result = await EmailPollVoorbereiding.LeesInstellingenAsync(
            () => Stap("probe", 2),
            async () => { await Stap("wake", true); },
            () => false,
            () => Stap("load", true),
            () => Stap("snapshot", Snapshot()),
            NullLogger.Instance);

        result.Should().BeNull();
        calls.Should().Equal(foutStap switch
        {
            "probe" => ["probe"],
            "wake" => ["probe", "wake"],
            _ => ["probe", "wake", "load", "snapshot"]
        });
    }

    [Fact]
    public async Task UitgeschakeldeVerseSnapshot_LaatVerwerkingNietDoor()
    {
        var result = await EmailPollVoorbereiding.LeesInstellingenAsync(
            () => Task.FromResult(1), () => Task.CompletedTask, () => false,
            () => Task.CompletedTask,
            () => Task.FromResult(Snapshot(false)), NullLogger.Instance);

        result.Should().BeNull();
    }
}
