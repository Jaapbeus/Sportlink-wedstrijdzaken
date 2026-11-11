using AwesomeAssertions;
using Xunit;

namespace FunctionApp.Postgres.Tests.Admin;

/// <summary>
/// #1862 (review P2): de beheerderstester (<c>test/email</c>) doet nooit een afzender-opzoeking in de begeleiding; een vrij
/// ingetypt adres mag niet tonen welk team iemand begeleidt. De dry-run is niet zonder database aan te roepen, dus dit is een
/// bewust tekstuele bewaking (zoals <c>VeldResolutieDriftTests</c>) op het argument van de pipelineaanroep; het gedrag zelf
/// (stap <c>Overgeslagen</c>, geen opzoeking) staat in <c>AfzenderTeamBepalingTests</c> in Planner.Shared.Tests.
/// </summary>
public class EmailTestFunctionAfzenderOvergeslagenTests
{
    private const string Pad = "FunctionApp.Postgres/Admin/EmailTestFunction.cs";

    private static string Bron()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("de testrunner moet ergens onder de repository-root draaien");
        var pad = Path.Combine(dir!.FullName, Pad);
        File.Exists(pad).Should().BeTrue($"{Pad} moet bestaan");
        return File.ReadAllText(pad);
    }

    [Fact]
    public void Proefverwerking_GeeftDeOvergeslagenAfzenderTeamBronMee()
    {
        var bron = Bron();
        var start = bron.IndexOf("BerichtPipeline.VerwerkMetPlannerAsync(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "de tester moet de planner-pipeline aanroepen");
        var einde = bron.IndexOf(");", start, StringComparison.Ordinal);

        bron[start..einde].Should().Contain("afzenderTeamBron: OvergeslagenAfzenderTeamBron.Proefverwerking",
            "de tester doet nooit een opzoeking van de afzender in de begeleiding (#1862)");
    }
}
