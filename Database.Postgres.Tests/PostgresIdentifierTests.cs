using Database.Postgres;
using FluentAssertions;
using Xunit;

namespace Database.Postgres.Tests;

public class PostgresIdentifierTests
{
    [Fact]
    public void Quote_PascalCaseNaam_BehoudtExacteCasing()
    {
        // Kern van #818's acceptatiecriterium: Postgres vouwt een ongequote identifier naar
        // lowercase; gequote blijft de exacte, aangeleverde casing behouden (empirisch bevestigd
        // tegen een lokale Postgres 16-container, zie docs/ARCHITECTUUR-DATABASE-TIERS.md §3).
        PostgresIdentifier.Quote("ClubCode").Should().Be("\"ClubCode\"");
    }

    [Fact]
    public void Quote_NaamMetDubbelAanhalingsteken_Verdubbelt()
    {
        PostgresIdentifier.Quote("foo\"bar").Should().Be("\"foo\"\"bar\"");
    }

    [Fact]
    public void Quote_LegeNaam_GooitArgumentException()
    {
        var act = () => PostgresIdentifier.Quote("");

        act.Should().Throw<ArgumentException>();
    }
}
