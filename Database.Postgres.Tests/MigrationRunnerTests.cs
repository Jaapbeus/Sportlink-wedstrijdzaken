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

    /// <summary>
    /// #1098: de ingesloten lijst is een afgeleide van <c>Database.Postgres/migrations/</c>. Loopt
    /// die uit de pas (nieuw bestand niet ingesloten, of andersom), dan meldt <c>/api/health</c>
    /// een verkeerd beeld van openstaande migraties — dat is precies het signaal dat bij het
    /// v3.3.0.0-incident ontbrak, dus dat mag niet stil wegzakken.
    /// </summary>
    [Fact]
    public void BundledMigrationNames_IsGelijkAanDeMigratiemap()
    {
        var map = Path.Combine(ZoekRepositoryRoot(), "Database.Postgres", "migrations");
        var opSchijf = Directory.GetFiles(map, "*.sql")
            .Select(Path.GetFileName)
            .OrderBy(n => MigrationRunner.ExtractSequenceNumber(n!))
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();

        MigrationRunner.BundledMigrationNames.Should().Equal(opSchijf);
        MigrationRunner.BundledMigrationNames.Should().Contain("001_baseline.sql")
            .And.Contain("012_sportlink_extension.sql");
    }

    [Fact]
    public void ComputePending_GeeftAlleenNietToegepasteMigraties_InVolgorde()
    {
        var bundled = new[] { "001_baseline.sql", "002_iets.sql", "012_sportlink_extension.sql", "013_audit.sql" };
        var applied = new HashSet<string>(StringComparer.Ordinal) { "001_baseline.sql", "002_iets.sql" };

        MigrationRunner.ComputePending(bundled, applied)
            .Should().Equal("012_sportlink_extension.sql", "013_audit.sql");
    }

    [Fact]
    public void ComputePending_AllesToegepast_IsLeeg()
    {
        var bundled = new[] { "001_baseline.sql", "002_iets.sql" };
        var applied = new HashSet<string>(StringComparer.Ordinal) { "001_baseline.sql", "002_iets.sql" };

        MigrationRunner.ComputePending(bundled, applied).Should().BeEmpty();
    }

    // Zelfde "loop omhoog tot .sln gevonden"-patroon als Database.Postgres.Cli en
    // VeldResolutieDriftTests — werkt ongeacht vanuit welke build-output de test draait.
    private static string ZoekRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository-root niet gevonden.");
    }
}
