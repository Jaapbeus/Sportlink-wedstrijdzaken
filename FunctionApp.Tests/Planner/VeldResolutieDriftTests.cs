using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// Bewaakt dat de veldnaam→veldnummer-vertaling niet opnieuw uiteen gaat lopen (#719).
///
/// <para>
/// De vertaling staat op drie plekken: <c>FunctionApp/Planner/VeldResolutie.cs</c> (die
/// <c>PlannerShared.ResolveVeld</c> spiegelt), de view in het DB-project, en de kopie van die view in
/// <c>Script.PostDeployment1.sql</c>. Dat laatste script is het enige dat CI uitrolt, dus een wijziging
/// die alleen in het DB-project landt verdwijnt geruisloos — precies de val die #719 beschrijft.
/// </para>
///
/// <para>
/// Deze tests zijn bewust tekstueel: ze draaien zonder database en falen in de CI-job die ook de
/// builds doet, dus vóór een merge. Een integratietest zou dit pas ná een deploy vinden.
/// </para>
/// </summary>
public class VeldResolutieDriftTests
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

    /// <summary>
    /// De zes-tekens-afkap mag nergens meer terugkomen. Dat patroon vereist dat élke veldnaam maximaal
    /// zes tekens is én in de eerste zes uniek: "veld 10" werd "veld 1" (bezetting op het verkeerde
    /// veld → dubbele boeking) en "hoofdveld" matchte niets (viel volledig uit de bezetting).
    /// </summary>
    [Theory]
    [InlineData("Database/planner/Views/AlleWedstrijdenOpVeld.sql")]
    [InlineData("Database/Script.PostDeployment1.sql")]
    [InlineData("FunctionApp/Planner/Repositories/PlannerMatchRepository.cs")]
    public void GeenAfkapOpZesTekensMeer(string relatiefPad)
    {
        var inhoud = Lees(relatiefPad);

        // Zowel LEFT(...) als SUBSTRING(..., 7, ...) op de veldkolom: de twee helften van dezelfde
        // aanname. Commentaarregels die de oude vorm citeren worden weggefilterd.
        var zonderCommentaar = string.Join(
            '\n',
            inhoud.Split('\n').Where(r => !r.TrimStart().StartsWith("--") && !r.TrimStart().StartsWith("///")));

        Regex.IsMatch(zonderCommentaar, @"LEFT\s*\(\s*m\.\[veld\]\s*,\s*6\s*\)", RegexOptions.IgnoreCase)
            .Should().BeFalse($"{relatiefPad} mag de veldnaam niet op zes tekens afkappen (#719)");

        Regex.IsMatch(zonderCommentaar, @"SUBSTRING\s*\(\s*m\.\[veld\]\s*,\s*7\s*,", RegexOptions.IgnoreCase)
            .Should().BeFalse($"{relatiefPad} mag de subpositie niet op positie 7 hardcoderen (#719)");
    }

    /// <summary>
    /// De view staat op twee plekken en CI rolt alleen het PostDeployment-script uit. Beide definities
    /// moeten dus tekstueel gelijk zijn, op de CREATE-regel en witruimte na.
    /// </summary>
    [Fact]
    public void ViewDefinitieIsGelijkInDbProjectEnPostDeployment()
    {
        var dbProject = Lees("Database/planner/Views/AlleWedstrijdenOpVeld.sql");
        var postDeploy = Lees("Database/Script.PostDeployment1.sql");

        var uitDbProject = NormaliseerViewBody(dbProject);
        var uitPostDeploy = NormaliseerViewBody(SnijViewUitPostDeployment(postDeploy));

        uitDbProject.Should().NotBeEmpty("de view-body moet uit het DB-projectbestand te halen zijn");
        uitPostDeploy.Should().Be(uitDbProject,
            "planner.AlleWedstrijdenOpVeld staat op twee plekken en CI rolt alleen "
            + "Script.PostDeployment1.sql uit — lopen ze uiteen, dan verdwijnt een wijziging geruisloos (#719)");
    }

    /// <summary>
    /// Haalt het <c>CREATE OR ALTER VIEW [planner].[AlleWedstrijdenOpVeld]</c>-blok uit het
    /// PostDeployment-script, tot de afsluitende batch-scheiding.
    /// </summary>
    private static string SnijViewUitPostDeployment(string script)
    {
        var start = script.IndexOf("CREATE OR ALTER VIEW [planner].[AlleWedstrijdenOpVeld]", StringComparison.OrdinalIgnoreCase);
        start.Should().BeGreaterThan(-1, "het CREATE OR ALTER VIEW-blok moet in het PostDeployment-script staan");

        var regels = script[start..].Split('\n');
        var body = regels.TakeWhile(r => r.Trim() != "GO");
        return string.Join('\n', body);
    }

    /// <summary>
    /// Reduceert een view-definitie tot vergelijkbare tekst: zonder de CREATE-regel (die verschilt
    /// bewust — <c>CREATE VIEW</c> in het DB-project, <c>CREATE OR ALTER VIEW</c> in het script),
    /// zonder commentaar en zonder witruimteverschillen.
    /// </summary>
    private static string NormaliseerViewBody(string definitie)
    {
        var regels = definitie
            .Split('\n')
            .Select(r => r.Trim())
            .SkipWhile(r => !r.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .Where(r => r.Length > 0 && !r.StartsWith("--"))
            .Select(r => Regex.Replace(r, @"\s+", " "))
            .Select(r => r.TrimEnd(';'));

        return string.Join('\n', regels);
    }
}
