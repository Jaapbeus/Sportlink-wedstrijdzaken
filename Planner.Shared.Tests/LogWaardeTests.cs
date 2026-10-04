using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>Legt <see cref="LogWaarde.Schoon"/> vast (#1472, log-forging).</summary>
public class LogWaardeTests
{
    [Fact]
    public void Null_geeft_lege_string() => LogWaarde.Schoon(null).Should().BeEmpty();

    [Fact]
    public void Gewone_tekst_blijft_ongewijzigd() => LogWaarde.Schoon("JO10-1 2026-10-04").Should().Be("JO10-1 2026-10-04");

    [Theory]
    [InlineData("a\r\nINFO valse regel")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb\u0000c\u001bd")]
    public void Stuurcodes_worden_verwijderd(string invoer)
    {
        var uitvoer = LogWaarde.Schoon(invoer);
        uitvoer.Should().NotContainAny("\r", "\n", "\t", "\u0000", "\u001b");
    }

    [Fact]
    public void CRLF_wordt_een_spatie_per_teken() => LogWaarde.Schoon("a\r\nb").Should().Be("a  b");

    [Fact]
    public void Lange_waarde_wordt_afgekapt()
    {
        var uitvoer = LogWaarde.Schoon(new string('x', 1000));
        uitvoer.Length.Should().Be(LogWaarde.MaxLengte + 1);
        uitvoer.Should().EndWith("…");
    }

    [Fact]
    public void Waarde_op_de_grens_wordt_niet_afgekapt()
    {
        var invoer = new string('x', LogWaarde.MaxLengte);
        LogWaarde.Schoon(invoer).Should().Be(invoer);
    }
}
