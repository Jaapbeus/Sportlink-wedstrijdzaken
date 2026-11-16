using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class SnapshotAntwoordInstellingenTests
{
    [Fact]
    public void GeselecteerdeClubMetOntbrekendVeldLeestNooitAndereCache()
    {
        var cacheReads = 0;
        var snapshot = new ClubAppSettingsSnapshot(null, null, null, null, null, EmailReviewModusActief: false);
        var settings = new SnapshotAntwoordInstellingen(snapshot,
            _ => { cacheReads++; return "verboden"; }, "club ontbreekt", "cache ontbreekt");

        settings.PlannerAfzenderNaam.Should().BeNull();
        settings.EmailVoetnoot.Should().BeNull();
        settings.EmailReviewModusActief.Should().BeFalse();
        settings.OntbrekendeAfzenderNaamMelding.Should().Be("club ontbreekt");
        cacheReads.Should().Be(0);
    }

    [Fact]
    public void ZonderSnapshotLeestCacheAlleenBijOpgevraagdVeldEnReviewBlijftAan()
    {
        var gelezen = new List<string>();
        var settings = new SnapshotAntwoordInstellingen(null,
            veld => { gelezen.Add(veld); return veld; }, "club ontbreekt", "cache ontbreekt");

        gelezen.Should().BeEmpty();
        settings.EmailReviewModusActief.Should().BeTrue();
        settings.PlannerAfzenderNaam.Should().Be("plannerAfzenderNaam");
        settings.OntbrekendeAfzenderNaamMelding.Should().Be("cache ontbreekt");
        gelezen.Should().ContainSingle().Which.Should().Be("plannerAfzenderNaam");
    }
}
