using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

/// <summary>De gedeelde ondergrensregel voor reconciliatie (#1558); beide tiers delen deze ene regel.</summary>
public class ReconciliatieOndergrensTests
{
    [Fact]
    public void EersteTeReconcilierenDatum_IsMorgen()
        => ReconciliatieOndergrens.EersteTeReconcilierenDatum(new DateOnly(2026, 10, 10)).Should().Be(new DateOnly(2026, 10, 11));

    [Fact]
    public void VandaagInNederland_GebruiktNederlandseDatum_OokVlakNaMiddernacht()
        => ReconciliatieOndergrens.VandaagInNederland(new DateTime(2026, 10, 9, 22, 30, 0, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById)
            .Should().Be(new DateOnly(2026, 10, 10));

    [Fact]
    public void VandaagInNederland_GooitNooit_EnKiestDeVeiligeKant_ZonderTijdzonegegevens()
        => ReconciliatieOndergrens.VandaagInNederland(new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc), _ => throw new TimeZoneNotFoundException())
            .Should().Be(new DateOnly(2026, 10, 10));
}
