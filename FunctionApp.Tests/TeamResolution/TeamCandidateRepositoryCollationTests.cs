using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

/// <summary>
/// Bewaakt dat de teamherkenning-lookups in <c>TeamCandidateRepository.cs</c> niet stilzwijgend
/// terugvallen op database-collatie (#820). SQL Server draait vandaag op een case-insensitive
/// default-collatie (<c>Database/SportlinkSqlDb.sqlproj</c>: <c>ModelCollation = 1033, CI</c>) — een
/// kale <c>=</c>-vergelijking "werkt" daardoor toevallig. Postgres' default-collatie is
/// case-sensitive; zonder expliciete <c>UPPER()</c>-wrapping matcht diezelfde vergelijking daar
/// stilzwijgend nul rijen zodra de opgeslagen casing afwijkt van de vers berekende sleutel.
/// <para>
/// Bewust tekstueel, net als <c>VeldResolutieDriftTests</c>: draait zonder database, dus faalt
/// vóór een merge in plaats van pas bij een latere Postgres-integratietest.
/// </para>
/// </summary>
public class TeamCandidateRepositoryCollationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("de testrunner moet ergens onder de repository-root draaien");
        return dir!.FullName;
    }

    private static string Lees()
    {
        var pad = Path.Combine(RepoRoot(), "FunctionApp/TeamResolution/TeamCandidateRepository.cs");
        File.Exists(pad).Should().BeTrue();
        return File.ReadAllText(pad);
    }

    /// <summary>Filtert doc-comments weg — die citeren het verboden patroon soms letterlijk ter illustratie.</summary>
    private static string ZonderCommentaar(string inhoud)
        => string.Join('\n', inhoud.Split('\n').Where(r => !r.TrimStart().StartsWith("///")));

    [Fact]
    public void FindExactTeamAsync_VergelijktTeamnaamGenormaliseerdViaUpper()
    {
        var inhoud = Lees();
        Regex.IsMatch(inhoud, @"UPPER\(\[TeamnaamGenormaliseerd\]\)\s*=\s*UPPER\(@sleutel\)")
            .Should().BeTrue("de lookup mag niet stilzwijgend op kolom-collatie leunen (#820)");
    }

    [Fact]
    public void FindValidatedAliasAsync_VergelijktRuweTekstEnGenormaliseerdViaUpper()
    {
        var inhoud = Lees();
        Regex.IsMatch(inhoud, @"UPPER\(a\.\[RuweTekst\]\)\s*=\s*UPPER\(@ruweTekst\)")
            .Should().BeTrue("RuweTekst-vergelijking moet expliciet UPPER() gebruiken (#820) — " +
                "anders gedragswijziging t.o.v. de huidige, feitelijk hoofdletterongevoelige CI-collatie");
        Regex.IsMatch(inhoud, @"UPPER\(a\.\[RuweTekstGenormaliseerd\]\)\s*=\s*UPPER\(@sleutel\)")
            .Should().BeTrue("RuweTekstGenormaliseerd-vergelijking moet expliciet UPPER() gebruiken (#820)");
    }

    /// <summary>
    /// Geen enkele van de drie sleutelkolommen mag nog kaal (zonder UPPER) tegen een parameter
    /// vergeleken worden — vangt een toekomstige nieuwe query die de collatie-afhankelijkheid
    /// per ongeluk opnieuw introduceert.
    /// </summary>
    [Theory]
    [InlineData(@"\[TeamnaamGenormaliseerd\]\s*=\s*@sleutel")]
    [InlineData(@"a\.\[RuweTekst\]\s*=\s*@ruweTekst")]
    [InlineData(@"a\.\[RuweTekstGenormaliseerd\]\s*=\s*@sleutel")]
    public void GeenKaleVergelijkingZonderUpper(string verbodenPatroon)
    {
        var inhoud = ZonderCommentaar(Lees());
        Regex.IsMatch(inhoud, verbodenPatroon).Should().BeFalse(
            $"'{verbodenPatroon}' zonder UPPER() leunt stilzwijgend op database-collatie (#820)");
    }
}
