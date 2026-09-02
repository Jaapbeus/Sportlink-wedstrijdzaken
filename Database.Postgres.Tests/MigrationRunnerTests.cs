using Database.Postgres;
using FluentAssertions;
using Xunit;

namespace Database.Postgres.Tests;

public class MigrationRunnerTests
{
    [Theory]
    [InlineData("001_baseline.sql", 1)]
    [InlineData("002_iets.sql", 2)]
    [InlineData("042_nog_iets.sql", 42)]
    public void ExtractSequenceNumber_ParsedLeidendeCijfers(string bestand, int verwacht)
        => MigrationRunner.ExtractSequenceNumber(bestand).Should().Be(verwacht);

    [Fact]
    public void ExtractSequenceNumber_ZonderVolgnummer_GooitExceptie()
    {
        var act = () => MigrationRunner.ExtractSequenceNumber("baseline.sql");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ComputeChecksum_IsDeterministisch()
    {
        var a = MigrationRunner.ComputeChecksum("CREATE TABLE x (y INT);");
        var b = MigrationRunner.ComputeChecksum("CREATE TABLE x (y INT);");
        a.Should().Be(b);
    }

    [Fact]
    public void ComputeChecksum_VerschillendeInhoud_GeeftAndereChecksum()
    {
        var a = MigrationRunner.ComputeChecksum("CREATE TABLE x (y INT);");
        var b = MigrationRunner.ComputeChecksum("CREATE TABLE x (y TEXT);");
        a.Should().NotBe(b);
    }
}
