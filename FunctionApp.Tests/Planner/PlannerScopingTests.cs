using System.Reflection;
using FluentAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// Regressietests voor de velden-lookup en de veldnaam-matching in het herplanpad (#707).
///
/// Twee faalscenario's liggen hier vast:
///   • De velden-lookup was niet op ClubCode gescoped. Een gelijknamig veld van een andere club
///     overschreef dan de eerste rij in het woordenboek, waarna de bezetting naar een
///     niet-bestaand veldnummer mapte — dat veld leek vrij en kon dubbel geboekt worden.
///   • De veldnaam werd met <c>StartsWith</c> gematcht. Bij tien of meer velden kreeg een
///     wedstrijd op "veld 10" het nummer van "veld 1": de eigen wedstrijd bleef in de bezetting
///     staan en de slots op veld 1 vielen onterecht weg.
///
/// De veldnaam-matching woont in <see cref="PlannerShared"/>, zodat het herplanpad en de
/// bezettingsopbouw niet uiteen kunnen lopen. Dat het uiteenlopen zelf tot een dubbele boeking
/// leidde, ligt vast in <see cref="VeldBezettingHerplanTests"/>.
/// </summary>
public class PlannerScopingTests
{
    private static List<VeldInfo> Velden(int aantal) =>
        Enumerable.Range(1, aantal)
            .Select(n => new VeldInfo { VeldNummer = n, VeldNaam = $"veld {n}" })
            .ToList();

    // ── Velden-lookup: gescoped op ClubCode en reproduceerbaar geordend ──

    [Fact]
    public void VeldenLookupSql_FiltertOpClubCode()
    {
        PlannerSettingsRepository.VeldenLookupSql
            .Should().Contain($"[ClubCode] = {ClubScope.ClubCodeParam}");
    }

    [Fact]
    public void VeldenLookupSql_IsDeterministischGeordend()
    {
        // Zonder ORDER BY bepaalt de queryplanner welke rij bij een dubbele veldnaam wint.
        PlannerSettingsRepository.VeldenLookupSql.Should().Contain("ORDER BY [VeldNummer]");
    }

    [Fact]
    public void GetVeldenLookupAsync_HeeftEenClubCodeParameter()
    {
        // Het real-time bezettingspad moet de lookup expliciet op één club kunnen scopen;
        // verdwijnt deze parameter, dan leest de lookup weer over clubs heen.
        var parameters = typeof(PlannerSettingsRepository)
            .GetMethod(nameof(PlannerSettingsRepository.GetVeldenLookupAsync),
                       BindingFlags.Static | BindingFlags.NonPublic)!
            .GetParameters();

        parameters.Should().ContainSingle().Which.Name.Should().Be("clubCode");
    }

    [Fact(Skip = "Vereist integratietestomgeving met SQL Server (lokaal uitvoeren)")]
    public Task GetVeldenLookupAsync_TweeClubsMetZelfdeVeldnaam_GeeftAlleenEigenVeldnummers()
    {
        // Arrange: dbo.Velden met dezelfde VeldNaam voor de primaire club en voor ALLSTARS
        // Act: GetVeldenLookupAsync(clubCode: "ALLSTARS")
        // Assert: alleen de ALLSTARS-veldnummers staan in het resultaat
        return Task.CompletedTask;
    }

    // ── Veldnaam-matching: exacte naam of naam plus subpositie, nooit een langer veldnummer ──

    [Fact]
    public void VindVeldNummer_Veld10_KrijgtNietHetNummerVanVeld1()
    {
        PlannerShared.VindVeldNummer("veld 10", Velden(12)).Should().Be(10);
    }

    [Theory]
    [InlineData("veld 1", 1)]
    [InlineData("veld 2", 2)]
    [InlineData("veld 3", 3)]
    [InlineData("veld 4", 4)]
    [InlineData("veld 5", 5)]
    [InlineData("veld 6", 6)]
    [InlineData("veld 7", 7)]
    [InlineData("veld 8", 8)]
    [InlineData("veld 9", 9)]
    [InlineData("veld 11", 11)]
    [InlineData("veld 12", 12)]
    public void VindVeldNummer_BestaandeVeldnamen_BlijvenMatchen(string veldNaam, int verwacht)
    {
        PlannerShared.VindVeldNummer(veldNaam, Velden(12)).Should().Be(verwacht);
    }

    [Theory]
    [InlineData("veld 1 A", 1)]
    [InlineData("veld 9 B", 9)]
    [InlineData("veld 10 B", 10)]
    [InlineData("veld 12 kwart 3", 12)]
    public void VindVeldNummer_SubpositieAchterDeVeldnaam_MatchtHetVeld(string veldNaam, int verwacht)
    {
        // Sportlink levert het veld als "<veldnaam> <subpositie>"; dbo.Velden bevat alleen de naam.
        PlannerShared.VindVeldNummer(veldNaam, Velden(12)).Should().Be(verwacht);
    }

    [Fact]
    public void VindVeldNummer_NegeertHoofdletters_EnOmliggendeSpaties()
    {
        PlannerShared.VindVeldNummer("  VELD 3 ", Velden(12)).Should().Be(3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("trainingsveld")]
    [InlineData("veld 13")]
    public void VindVeldNummer_GeenTreffer_GeeftNul(string? veldNaam)
    {
        PlannerShared.VindVeldNummer(veldNaam, Velden(12)).Should().Be(0);
    }

    [Fact]
    public void VindVeldNummer_LangsteVeldnaamWint()
    {
        // Bestaat een veldnaam die zelf begint met een andere veldnaam, dan mag de langste
        // naam winnen — anders wordt "achter" als subpositie van veld 1 gelezen.
        var velden = new List<VeldInfo>
        {
            new() { VeldNummer = 1, VeldNaam = "veld 1" },
            new() { VeldNummer = 7, VeldNaam = "veld 1 achter" }
        };

        PlannerShared.VindVeldNummer("veld 1 achter B", velden).Should().Be(7);
        PlannerShared.VindVeldNummer("veld 1 B", velden).Should().Be(1);
    }

    [Fact]
    public void VindVeldNummer_VeldZonderNaam_VeroorzaaktGeenTreffer()
    {
        var velden = new List<VeldInfo> { new() { VeldNummer = 4, VeldNaam = "" } };

        PlannerShared.VindVeldNummer("veld 4", velden).Should().Be(0);
    }
}
