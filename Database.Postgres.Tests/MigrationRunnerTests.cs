using Database.Postgres;
using AwesomeAssertions;
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
    /// #1112: dezelfde migratie uit een Windows-werkmap (CRLF, <c>core.autocrlf=true</c> onder
    /// <c>* text=auto</c>) en uit een macOS/Linux/CI-werkmap (LF) moet één en dezelfde checksum
    /// opleveren — anders blokkeert de ledger de hele keten zodra een tweede platform migreert.
    /// </summary>
    [Fact]
    public void ComputeChecksum_CrlfEnLf_GevenDezelfdeChecksum()
    {
        var lf = "CREATE TABLE x (\n    y INT\n);\n";
        var crlf = "CREATE TABLE x (\r\n    y INT\r\n);\r\n";

        MigrationRunner.ComputeChecksum(crlf).Should().Be(MigrationRunner.ComputeChecksum(lf));
    }

    /// <summary>Een puur-LF-bestand (zoals git ze bewaart) hasht na #1112 exact zoals ervoor, dus
    /// bestaande ledgers die vanaf LF gevuld zijn blijven zonder reparatie kloppen.</summary>
    [Fact]
    public void ComputeChecksum_LfInhoud_IsOngewijzigdTenOpzichteVanRauweSha256()
    {
        var lf = "CREATE TABLE x (\n    y INT\n);\n";
        var rauw = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(lf))).ToLowerInvariant();

        MigrationRunner.ComputeChecksum(lf).Should().Be(rauw);
    }

    [Fact]
    public void IsLineEndingVariant_RauweCrlfChecksumVanHetzelfdeBestand_IsTrue()
    {
        var lf = "CREATE TABLE x (\n    y INT\n);\n";
        var crlf = lf.Replace("\n", "\r\n");
        var oudeLedgerWaarde = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(crlf))).ToLowerInvariant();

        MigrationRunner.IsLineEndingVariant(oudeLedgerWaarde, lf).Should().BeTrue(
            "een ledger die vanaf een CRLF-checkout is gevuld, herkent hetzelfde LF-bestand");
        MigrationRunner.IsLineEndingVariant(oudeLedgerWaarde, crlf).Should().BeTrue(
            "en andersom: het bestand staat nu zelf als CRLF op schijf");
    }

    [Fact]
    public void IsLineEndingVariant_InhoudelijkGewijzigdBestand_IsFalse()
    {
        var origineel = "CREATE TABLE x (\n    y INT\n);\n";
        var gewijzigd = "CREATE TABLE x (\n    y TEXT\n);\n";
        var ledger = MigrationRunner.ComputeChecksum(origineel);

        MigrationRunner.IsLineEndingVariant(ledger, gewijzigd).Should().BeFalse(
            "een echte inhoudswijziging mag nooit als regeleinde-artefact doorgaan");
        MigrationRunner.IsLineEndingVariant(ledger, gewijzigd.Replace("\n", "\r\n")).Should().BeFalse();
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
