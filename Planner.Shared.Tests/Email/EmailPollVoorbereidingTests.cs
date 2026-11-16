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
            graphBeschikbaar ? () => { calls.Add("probe"); return Task.FromResult(false); } : null,
            () => { calls.Add("wake"); return Task.CompletedTask; },
            () => { calls.Add("cache"); return false; },
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
            () => { calls.Add("probe"); return Task.FromResult(true); },
            () => { calls.Add("wake"); return Task.CompletedTask; },
            () => { calls.Add("cache"); return ladenMislukt; },
            () => { calls.Add("snapshot"); return Task.FromResult(Snapshot()); },
            NullLogger.Instance);

        calls.Should().Equal(ladenMislukt ? ["probe", "wake", "cache"] : ["probe", "wake", "cache", "snapshot"]);
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
            () => Stap("probe", true),
            async () => { await Stap("wake", true); },
            () => false,
            () => Stap("snapshot", Snapshot()),
            NullLogger.Instance);

        result.Should().BeNull();
        calls.Should().Equal(foutStap switch
        {
            "probe" => ["probe"],
            "wake" => ["probe", "wake"],
            _ => ["probe", "wake", "snapshot"]
        });
    }

    [Fact]
    public async Task UitgeschakeldeVerseSnapshot_LaatVerwerkingNietDoor()
    {
        var result = await EmailPollVoorbereiding.LeesInstellingenAsync(
            () => Task.FromResult(true), () => Task.CompletedTask, () => false,
            () => Task.FromResult(Snapshot(false)), NullLogger.Instance);

        result.Should().BeNull();
    }
}
