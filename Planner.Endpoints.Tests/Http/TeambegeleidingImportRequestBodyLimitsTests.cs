using AwesomeAssertions;
using Planner.Endpoints.Http;
using Xunit;

namespace Planner.Endpoints.Tests.Http;

public class TeambegeleidingImportRequestBodyLimitsTests
{
    [Fact]
    public void CsvExactlyFiveMiBUtf8_IsWithinLimit()
    {
        TeambegeleidingImportRequestBodyLimits.CsvFitsByteLimit(
            new string('a', TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes)).Should().BeTrue();
    }

    [Fact]
    public void CsvOneAsciiByteOverFiveMiB_IsOverLimit()
    {
        TeambegeleidingImportRequestBodyLimits.CsvFitsByteLimit(
            new string('a', TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes + 1)).Should().BeFalse();
    }

    [Fact]
    public void CsvWithMultibyteCharacters_UsesUtf8ByteCount()
    {
        var csv = new string('é', TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes / 2 + 1);

        TeambegeleidingImportRequestBodyLimits.CsvFitsByteLimit(csv).Should().BeFalse();
    }
}
