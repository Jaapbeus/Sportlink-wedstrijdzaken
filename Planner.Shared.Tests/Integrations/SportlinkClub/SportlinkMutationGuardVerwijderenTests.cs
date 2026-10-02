using System.Text.Json;
using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>
/// Guard voor het verwijderen van een clubwedstrijd (#1440). Sportlinks eigen frontend toont de knop
/// "Verwijder" alleen als <c>IsKernelMatch === false</c> (uit de publieke frontend-bundle, niet live
/// gezien). De guard is fail-closed: alleen een expliciete <c>false</c> laat verwijderen toe.
/// </summary>
public class SportlinkMutationGuardVerwijderenTests
{
    private static SportlinkMatch ClubWedstrijd(bool? isKernelMatch) => new()
    {
        PublicMatchId = "M000000001",
        IsHomeMatch = true,
        IsKernelMatch = isKernelMatch
    };

    [Fact]
    public void Verwijderen_ClubwedstrijdIsKernelMatchFalse_IsToegestaan()
    {
        var result = SportlinkMutationGuard.MagMuteren(ClubWedstrijd(false), SportlinkMutationSoort.Verwijderen);

        result.IsToegstaan.Should().BeTrue();
        result.Reden.Should().BeNull();
    }

    [Fact]
    public void Verwijderen_CompetitieOfBekerwedstrijdIsKernelMatchTrue_IsGeblokkeerd()
    {
        var result = SportlinkMutationGuard.MagMuteren(ClubWedstrijd(true), SportlinkMutationSoort.Verwijderen);

        result.IsToegstaan.Should().BeFalse();
        result.Reden.Should().Contain("IsKernelMatch");
    }

    [Fact]
    public void Verwijderen_IsKernelMatchOntbreekt_IsGeblokkeerd()
    {
        // Fail-closed: geeft Sportlink het veld niet mee, dan is niet vast te stellen dat het een
        // clubwedstrijd is — dan nooit verwijderen.
        var result = SportlinkMutationGuard.MagMuteren(ClubWedstrijd(null), SportlinkMutationSoort.Verwijderen);

        result.IsToegstaan.Should().BeFalse();
        result.Reden.Should().Contain("IsKernelMatch");
    }

    [Fact]
    public void Verwijderen_Uitwedstrijd_IsGeblokkeerd()
    {
        // De generieke IsHomeMatch-regel blijft gelden: strenger dan Sportlinks eigen knop, bewust.
        var match = ClubWedstrijd(false) with { IsHomeMatch = false };

        SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.Verwijderen).IsToegstaan.Should().BeFalse();
    }

    [Fact]
    public void Verwijderen_KijktNietNaarDeAnderePermissievlaggen()
    {
        // Alle Is…Allowed-vlaggen staan op true, maar zonder IsKernelMatch=false blijft het nee:
        // een bewerkpermissie is geen verwijderpermissie.
        var match = ClubWedstrijd(null) with
        {
            IsEditFieldAllowed = true,
            IsAssignDressingRoomsAllowed = true,
            IsAssignOfficialsAllowed = true,
            IsEditFieldSidePanelAllowed = true,
            IsAddScoreAllowed = true
        };

        SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.Verwijderen).IsToegstaan.Should().BeFalse();
    }

    [Fact]
    public void IsKernelMatch_WordtGelezenUitDeMatchRespons()
    {
        // Sportlink levert PascalCase ("IsKernelMatch"); de client leest hoofdletterongevoelig.
        var opties = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        JsonSerializer.Deserialize<SportlinkMatch>("""{"IsKernelMatch": false}""", opties)!.IsKernelMatch.Should().BeFalse();
        JsonSerializer.Deserialize<SportlinkMatch>("""{"IsKernelMatch": true}""", opties)!.IsKernelMatch.Should().BeTrue();
        JsonSerializer.Deserialize<SportlinkMatch>("""{}""", opties)!.IsKernelMatch.Should().BeNull();
    }
}
