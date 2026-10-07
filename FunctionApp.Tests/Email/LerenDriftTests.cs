using System.Text.RegularExpressions;
using AwesomeAssertions;
using Planner.Shared.Email;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Bewaakt de afspraken van het leren vanuit de trace (#1568 deel C) die geen database nodig hebben:
/// de lijst geldige verzoektypen blijft gelijk aan de enum, en de retentie raakt een admin-leermoment nooit.
/// Bewust tekstueel (zelfde patroon als <c>VeldResolutieDriftTests</c>): draait in de gewone build, dus vóór een merge.
/// </summary>
public class LerenDriftTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("de testrunner moet ergens onder de repository-root draaien");
        return dir!.FullName;
    }

    private static string Lees(string relatiefPad)
    {
        var pad = Path.Combine(RepoRoot(), relatiefPad);
        File.Exists(pad).Should().BeTrue($"{relatiefPad} moet bestaan");
        return File.ReadAllText(pad);
    }

    [Fact]
    public void GeldigeVerzoekTypes_BlijftGelijkAanDeEnum()
        => LeermomentInvoer.GeldigeVerzoekTypes.Should().BeEquivalentTo(Enum.GetNames<VerzoekType>());

    [Fact]
    public void BlazorVerzoekTypeLijst_BlijftGelijkAanDeServerlijst()
    {
        var bron = Lees("BlazorAdmin/Shared/LeermomentFormulier.razor.cs");
        var blok = Regex.Match(bron, @"VerzoekTypes\s*=\s*\[(?<lijst>[^\]]*)\]").Groups["lijst"].Value;
        var namen = Regex.Matches(blok, "\"(?<naam>\\w+)\"").Select(m => m.Groups["naam"].Value).ToList();

        namen.Should().BeEquivalentTo(LeermomentInvoer.GeldigeVerzoekTypes);
    }

    [Theory]
    [InlineData("Database/planner/System Stored Procedures/sp_CleanupClassificatieCorrectie.sql", 2)]
    [InlineData("Database/planner/System Stored Procedures/sp_CleanupEmailVerwerking.sql", 1)]
    public void SqlServerRetentie_RaaktAlleenHerkomstReply(string pad, int minimaalAantal)
        => Regex.Matches(Lees(pad), @"\[Herkomst\]\s*=\s*N'Reply'").Count.Should().BeGreaterThanOrEqualTo(minimaalAantal,
            "een admin-leermoment is permanent (besluit eigenaar 2026-10-06)");

    [Fact]
    public void PostDeployment_RetentieprocedureskomenOvereenMetDeBronbestanden_EnLeggenDeUqNietTerug()
    {
        var script = Lees("Database/Script.PostDeployment1.sql");

        // De gedeployde definities (CREATE OR ALTER) moeten dezelfde Herkomst-guards hebben als de bronbestanden.
        Regex.Matches(script, @"WHERE\s+\[Herkomst\]\s*=\s*N'Reply'").Count.Should().BeGreaterThanOrEqualTo(2);
        script.Should().Contain("WHERE cc.[Herkomst] = N'Reply'");

        // Het oude ontdubbelblok mag admin-leermomenten (paar NULL, NULL) nooit als duplicaat verwijderen.
        script.Should().Contain("AND NOT EXISTS (SELECT 1 FROM sys.indexes\n                   WHERE name = 'UX_ClassificatieCorrectie_Paar'");
    }

    [Fact]
    public void PostgresRetentie_RaaktAlleenHerkomstReply()
    {
        var bron = Lees("Database.Postgres/PostgresCleanupProcedures.cs");

        Regex.Matches(bron, @"herkomst = 'Reply'").Count.Should().BeGreaterThanOrEqualTo(3,
            "anonimiseren, de eigen 90-dagen-DELETE en de FK-opruiming van fase 2a");
    }

    [Fact]
    public void Migratie039_ZetRlsOpDeNieuweTabel_EnMaaktDeVerwerkingIdsNullable()
    {
        var sql = Lees("Database.Postgres/migrations/039_leren_van_de_trace.sql");

        sql.Should().Contain("ALTER TABLE planner.onbekendeteamtekst ENABLE ROW LEVEL SECURITY;");
        sql.Should().Contain("ALTER COLUMN origineleverwerkingid DROP NOT NULL");
        sql.Should().Contain("ALTER COLUMN correctionverwerkingid DROP NOT NULL");
        sql.Should().NotContain("REFERENCES planner.emailverwerking", "een admin-leermoment en de wachtrij mogen geen harde FK hebben (#424)");
    }
}
