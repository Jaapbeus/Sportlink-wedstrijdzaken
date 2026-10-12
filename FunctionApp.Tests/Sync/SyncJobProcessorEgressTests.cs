using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction;
using Xunit;

namespace FunctionApp.Tests.Sync;

/// <summary>
/// #1676: de queue-verwerking (SQL Server-tier) voert buiten productie geen sync uit. Getest wordt
/// de beslissing die <see cref="SyncJobProcessor.Run"/> als eerste neemt na het lezen van het
/// bericht: bij <c>true</c> keert Run terug vóór MarkRunning en de sync-aanroep.
/// </summary>
public class SyncJobProcessorEgressTests
{
    private readonly List<(Guid Id, bool Gelukt, string? Fout)> _afgerond = [];

    private Task Vastleggen(Guid id, bool gelukt, string? fout)
    {
        _afgerond.Add((id, gelukt, fout));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EgressGuardUit_JobWordtMislukt_EnRunStopt()
    {
        var job = new SyncJobMessage { JobId = Guid.NewGuid(), WeekOffsetFrom = -1, WeekOffsetTo = 4 };

        var gestopt = await SyncJobProcessor.BlokkeerBuitenProductieAsync(
            job, NullLogger.Instance, () => false, Vastleggen);

        gestopt.Should().BeTrue("Run moet vóór MarkRunning en de sync terugkeren");
        _afgerond.Should().ContainSingle();
        _afgerond[0].Id.Should().Be(job.JobId);
        _afgerond[0].Gelukt.Should().BeFalse("de beheerder moet de job als mislukt zien");
        _afgerond[0].Fout.Should().Contain("buiten productie");
    }

    [Fact]
    public async Task EgressGuardAan_JobLooptDoor_ZonderVastlegging()
    {
        var job = new SyncJobMessage { JobId = Guid.NewGuid() };

        var gestopt = await SyncJobProcessor.BlokkeerBuitenProductieAsync(
            job, NullLogger.Instance, () => true, Vastleggen);

        gestopt.Should().BeFalse();
        _afgerond.Should().BeEmpty();
    }
}
