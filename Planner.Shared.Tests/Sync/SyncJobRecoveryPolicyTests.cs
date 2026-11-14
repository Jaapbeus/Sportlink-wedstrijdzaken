using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

/// <summary>#1839: eigenaarsbesluit optie B — 30 minuten op running = onderbroken, hoogstens 2 herhalingen.</summary>
public class SyncJobRecoveryPolicyTests
{
    private static readonly DateTime Nu = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Besluit_Is30MinutenEnTweeHerhalingen()
    {
        SyncJobRecoveryPolicy.StaleAfter.Should().Be(TimeSpan.FromMinutes(30));
        SyncJobRecoveryPolicy.MaxRetries.Should().Be(2);
        SyncJobRecoveryPolicy.MaxAttempts.Should().Be(3);
    }

    [Theory]
    [InlineData(29, 59, false)]
    [InlineData(30, 0, true)]
    [InlineData(30, 1, true)]
    [InlineData(5, 0, false)]
    public void IsInterrupted_Grenswaarde30Minuten(int minuten, int seconden, bool verwacht)
    {
        var gestart = Nu - TimeSpan.FromMinutes(minuten) - TimeSpan.FromSeconds(seconden);

        SyncJobRecoveryPolicy.IsInterrupted(gestart, Nu).Should().Be(verwacht);
    }

    [Fact]
    public void IsInterrupted_ZonderStarttijd_IsNooitOnderbroken()
        => SyncJobRecoveryPolicy.IsInterrupted(null, Nu).Should().BeFalse();

    [Fact]
    public void StaleCutoff_IsNuMinus30Minuten()
        => SyncJobRecoveryPolicy.StaleCutoff(Nu).Should().Be(Nu.AddMinutes(-30));

    [Theory]
    [InlineData(1, 29, SyncJobRecoveryAction.None)]
    [InlineData(1, 30, SyncJobRecoveryAction.Retry)]
    [InlineData(2, 30, SyncJobRecoveryAction.Retry)]
    [InlineData(3, 29, SyncJobRecoveryAction.None)]
    [InlineData(3, 30, SyncJobRecoveryAction.GiveUp)]
    [InlineData(4, 90, SyncJobRecoveryAction.GiveUp)]
    public void Decide_Running_TweeHerhalingenDaarnaOpgeven(int poging, int minuten, SyncJobRecoveryAction verwacht)
        => SyncJobRecoveryPolicy.Decide("running", poging, Nu.AddMinutes(-minuten), Nu).Should().Be(verwacht);

    [Fact]
    public void Decide_LegacyRunningZonderPogingen_WordtHerhaald()
        => SyncJobRecoveryPolicy.Decide("running", 0, Nu.AddMinutes(-45), Nu).Should().Be(SyncJobRecoveryAction.Retry);

    [Theory]
    [InlineData(0, 31, SyncJobRecoveryAction.None)]   // nooit geclaimd: geen herstel
    [InlineData(1, 29, SyncJobRecoveryAction.None)]
    [InlineData(1, 30, SyncJobRecoveryAction.Retry)]  // bericht kwijt: opnieuw aanbieden
    [InlineData(3, 30, SyncJobRecoveryAction.Retry)]  // verbruikt geen poging; de claim telt
    public void Decide_Pending(int poging, int minuten, SyncJobRecoveryAction verwacht)
        => SyncJobRecoveryPolicy.Decide("pending", poging, Nu.AddMinutes(-minuten), Nu).Should().Be(verwacht);

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public void Decide_TerminaleStatus_Nooit(string status)
        => SyncJobRecoveryPolicy.Decide(status, 1, Nu.AddHours(-5), Nu).Should().Be(SyncJobRecoveryAction.None);
}
