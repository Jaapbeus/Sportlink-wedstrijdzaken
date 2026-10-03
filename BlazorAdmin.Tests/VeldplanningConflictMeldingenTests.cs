using System.Text.Json;
using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// #1430: de conflictmeldingen na een handmatige zet op "Veld optimalisatie". De beslisregels zelf
/// zijn getest in <c>Planner.Shared.Tests/Planning</c>; hier de vertaling van het auto-plancontract
/// naar die regels en de Nederlandse teksten (die bewust gelijk zijn gebleven aan vóór #1430).
/// </summary>
public class VeldplanningConflictMeldingenTests
{
    private static AutoPlanWedstrijdItemDto Item(string team, string? veldNaam, string? veld, string? tijd,
        int duur = 60, int? voor = null, int? na = null) => new()
    {
        TeamNaam = team,
        OptimaalVeldNaam = veldNaam,
        OptimaalVeld = veld,
        OptimaalTijd = tijd,
        DuurMinuten = duur,
        TeamBufferVoor = voor,
        TeamBufferNa = na,
    };

    [Fact]
    public void Scenario1430_TeamBufferNa60_Gat30_GeeftNuEenMelding()
    {
        var meldingen = VeldplanningConflictMeldingen.Bepaal(
        [
            Item("JO13-1", "Veld 1", "Veld 1", "10:00", na: 60),
            Item("JO15-1", "Veld 1", "Veld 1", "11:30"),
        ], algemeneBuffer: 15);

        meldingen.Should().Equal(
            "Veld 1: tussen JO13-1 en JO15-1 zit 30 min, minder dan de vereiste teambuffer van 60 min.");
    }

    [Fact]
    public void Scenario1430_ZelfdeTeamOpAnderVeld_GeeftTeambufferMelding()
    {
        var meldingen = VeldplanningConflictMeldingen.Bepaal(
        [
            Item("JO13-1", "Veld 1", "Veld 1", "10:00", na: 60),
            Item("JO13-1", "Veld 2", "Veld 2", "11:30"),
        ], algemeneBuffer: 15);

        meldingen.Should().Equal(
            "JO13-1: tussen de wedstrijd op Veld 1 en die op Veld 2 zit 30 min, minder dan de vereiste teambuffer van 60 min.");
    }

    [Fact]
    public void BestaandeTeksten_BlijvenOngewijzigd()
    {
        var meldingen = VeldplanningConflictMeldingen.Bepaal(
        [
            Item("A", "Veld 1", "Veld 1 A1", "10:00"),
            Item("B", "Veld 1", "Veld 1 A1", "10:30"),
            Item("C", "Veld 2", "Veld 2", "10:00"),
            Item("D", "Veld 2", "Veld 2", "11:10"),
            Item("C", "Veld 3", "Veld 3", "10:15"),
            Item("D", "Veld 3", "Veld 3", "12:20"),
        ], algemeneBuffer: 15);

        meldingen.Should().Equal(
            "Veld 1: A en B staan op hetzelfde veldgedeelte op dezelfde tijd.",
            "Veld 2: tussen C en D zit 10 min, minder dan de ingestelde buffer van 15 min.",
            "C: staat tegelijk ingepland op Veld 2 en Veld 3 om 10:00.",
            "D: tussen de wedstrijd op Veld 2 en die op Veld 3 zit 10 min, minder dan de ingestelde buffer van 15 min.");
    }

    [Fact]
    public void SubpositieUitDeVeldtekst_HalveVeldenNaastElkaarZijnGeenConflict()
    {
        VeldplanningConflictMeldingen.Bepaal(
        [
            Item("A", "Veld 1", "Veld 1 A", "10:00"),
            Item("B", "Veld 1", "Veld 1 B", "10:00"),
        ], algemeneBuffer: 15).Should().BeEmpty();
    }

    [Fact]
    public void OngeldigeOfOntbrekendeTijdEnDuur_WordenVeiligGenegeerd()
    {
        VeldplanningConflictMeldingen.Bepaal(
        [
            Item("A", "Veld 1", "Veld 1", null),
            Item("B", "Veld 1", "Veld 1", "geen-tijd"),
            Item("C", "Veld 1", "Veld 1", "10:00", duur: 0),
            Item("D", "Veld 1", "Veld 1", "10:00"),
            Item("E", null, null, "10:00"),
        ], algemeneBuffer: 15).Should().BeEmpty();
    }

    [Fact]
    public void LagereAlgemeneBufferNaHerplannen_TeamregelBlijftGelden()
    {
        // De gebruiker zet de buffer op 0: de teamregel van 60 blijft dan nog steeds de maat.
        VeldplanningConflictMeldingen.Bepaal(
        [
            Item("A", "Veld 1", "Veld 1", "10:00", na: 60),
            Item("B", "Veld 1", "Veld 1", "11:30"),
        ], algemeneBuffer: 0).Should().ContainSingle().Which.Should().Contain("teambuffer van 60 min");
    }

    [Fact]
    public void ContractVanDeServer_WordtMetDeTeambuffersGedeserialiseerd()
    {
        // Zelfde opties als AdminApiClient; de servertests leggen deze draadvorm aan hun kant vast.
        var dto = JsonSerializer.Deserialize<AutoPlanWedstrijdItemDto>(
            """{"teamNaam":"A","teamBufferVoor":45,"teamBufferNa":null}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        dto!.TeamBufferVoor.Should().Be(45);
        dto.TeamBufferNa.Should().BeNull();
    }
}
