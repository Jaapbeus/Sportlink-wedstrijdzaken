using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

public class SyncTriggerCoreTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"season\":2025}")]
    public void ZonderReset_IsStandaardGedrag_SeasonGenegeerd(string? body)
    {
        var (req, fout) = SyncTriggerCore.LeesBody(body);
        fout.Should().BeNull();
        var keuze = SyncTriggerCore.Valideer(req, 2026);
        keuze.Geldig.Should().BeTrue();
        keuze.SeasonStartYear.Should().BeNull();
    }

    [Fact]
    public void MetResetEnSeason_GeeftStartjaar()
    {
        var (req, _) = SyncTriggerCore.LeesBody("{\"reset\":true,\"season\":2025}");
        var keuze = SyncTriggerCore.Valideer(req, 2026);
        keuze.Geldig.Should().BeTrue();
        keuze.SeasonStartYear.Should().Be(2025);
    }

    [Theory]
    [InlineData("{\"reset\":true}")]
    [InlineData("{\"reset\":true,\"season\":1999}")]
    [InlineData("{\"reset\":true,\"season\":2028}")]
    public void ResetMetOntbrekendOfBuitenBereikSeason_IsFout(string body)
    {
        var (req, _) = SyncTriggerCore.LeesBody(body);
        var keuze = SyncTriggerCore.Valideer(req, 2026);
        keuze.Geldig.Should().BeFalse();
        keuze.Fout.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("niet-json")]
    [InlineData("{\"reset\":\"ja\"}")]
    [InlineData("{\"reset\":true,\"season\":\"abc\"}")]
    public void OngeldigeBody_IsFout(string body)
    {
        var (req, fout) = SyncTriggerCore.LeesBody(body);
        req.Should().BeNull();
        fout.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VanWeekOffset_ZonderReset_IsMinEenEnDatabaseWordtNietGeraadpleegd()
    {
        var aangeroepen = false;
        var van = await SyncTriggerCore.BepaalVanWeekOffsetAsync(new SyncTriggerKeuze(null, null), _ => { aangeroepen = true; return Task.FromResult(0); });
        van.Should().Be(-1);
        aangeroepen.Should().BeFalse();
    }

    [Fact]
    public async Task VanWeekOffset_MetReset_GeeftSeizoensstartDoor()
    {
        int? ontvangen = null;
        var van = await SyncTriggerCore.BepaalVanWeekOffsetAsync(new SyncTriggerKeuze(null, 2025), j => { ontvangen = j; return Task.FromResult(-60); });
        van.Should().Be(-60);
        ontvangen.Should().Be(2025);
    }
}
