using AwesomeAssertions;
using Planner.Shared.Calendars;
using Xunit;

namespace Planner.Shared.Tests.Calendars;

public class KnvbKalenderSelectieTests
{
    private static readonly DateOnly Zaterdag1 = new(2026, 9, 5);
    private static readonly DateOnly Zaterdag2 = new(2026, 9, 12);
    private static readonly DateOnly Vrijdag = new(2026, 9, 4);

    [Fact]
    public void SluitNietZaterdagenEnBezetteDataUitVoorDeLimiet()
    {
        KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            [Vrijdag, Zaterdag1, Zaterdag2], new HashSet<DateOnly> { Zaterdag1 }, 1)
            .Should().Equal(Zaterdag2);
    }

    [Fact]
    public void BehoudtQueryVolgordeEnHerhalingen()
    {
        // The helper owns no sorting or distinct rule; native queries own eligibility/order.
        KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            [Zaterdag2, Zaterdag2, Zaterdag1], new HashSet<DateOnly>(), 2)
            .Should().Equal(Zaterdag2, Zaterdag2);
    }

    [Fact]
    public void MaxAantalNietPositiefGeeftLegeLijst()
    {
        foreach (var maxAantal in new[] { 0, -1 })
            KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
                [Zaterdag1], new HashSet<DateOnly>(), maxAantal).Should().BeEmpty();
    }
}
