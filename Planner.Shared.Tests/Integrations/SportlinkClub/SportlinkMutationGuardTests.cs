using FluentAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

public class SportlinkMutationGuardTests
{
    [Fact]
    public void MagMuteren_IsHomeMatchFalse_GeeftGeblokkeerd()
    {
        // Arrange
        var match = new SportlinkMatch
        {
            PublicMatchId = "M000000001",
            IsHomeMatch = false, // Uitwedstrijd
            IsEditFieldAllowed = true
        };

        // Act
        var result = SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.Veld);

        // Assert
        result.IsToegstaan.Should().BeFalse();
        result.Reden.Should().Contain("IsHomeMatch");
    }

    [Fact]
    public void MagMuteren_KleedkamersMetToegestaan_GeeftToegestaan()
    {
        // Arrange
        var match = new SportlinkMatch
        {
            PublicMatchId = "M000000001",
            IsHomeMatch = true,
            IsAssignDressingRoomsAllowed = true
        };

        // Act
        var result = SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.Kleedkamers);

        // Assert
        result.IsToegstaan.Should().BeTrue();
        result.Reden.Should().BeNull();
    }

    [Fact]
    public void MagMuteren_KleedkamersZonderToegestaan_GeeftGeblokkeerd()
    {
        // Arrange
        var match = new SportlinkMatch
        {
            PublicMatchId = "M000000001",
            IsHomeMatch = true,
            IsAssignDressingRoomsAllowed = false
        };

        // Act
        var result = SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.Kleedkamers);

        // Assert
        result.IsToegstaan.Should().BeFalse();
        result.Reden.Should().Contain("Sportlink staat deze actie niet toe");
    }

    [Fact]
    public void MagMuteren_VeldMetToegestaan_GeeftToegestaan()
    {
        // Arrange
        var match = new SportlinkMatch
        {
            PublicMatchId = "M000000001",
            IsHomeMatch = true,
            IsEditFieldAllowed = true
        };

        // Act
        var result = SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.Veld);

        // Assert
        result.IsToegstaan.Should().BeTrue();
    }

    [Fact]
    public void MagMuteren_VeldSidePanelMetToegestaan_GeeftToegestaan()
    {
        // Arrange
        var match = new SportlinkMatch
        {
            PublicMatchId = "M000000001",
            IsHomeMatch = true,
            IsEditFieldSidePanelAllowed = true
        };

        // Act
        var result = SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.VeldSidePanel);

        // Assert
        result.IsToegstaan.Should().BeTrue();
    }

    [Fact]
    public void MagMuteren_OfficialsMetToegestaan_GeeftToegestaan()
    {
        // Arrange
        var match = new SportlinkMatch
        {
            PublicMatchId = "M000000001",
            IsHomeMatch = true,
            IsAssignOfficialsAllowed = true
        };

        // Act
        var result = SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.Officials);

        // Assert
        result.IsToegstaan.Should().BeTrue();
    }

    [Fact]
    public void MagMuteren_UitslagMetToegestaan_GeeftToegestaan()
    {
        // Arrange
        var match = new SportlinkMatch
        {
            PublicMatchId = "M000000001",
            IsHomeMatch = true,
            IsAddScoreAllowed = true
        };

        // Act
        var result = SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.Uitslag);

        // Assert
        result.IsToegstaan.Should().BeTrue();
    }

    [Fact]
    public void MagMuteren_UitslagZonderToegestaan_GeeftGeblokkeerd()
    {
        // Arrange
        var match = new SportlinkMatch
        {
            PublicMatchId = "M000000001",
            IsHomeMatch = true,
            IsAddScoreAllowed = false
        };

        // Act
        var result = SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.Uitslag);

        // Assert
        result.IsToegstaan.Should().BeFalse();
        result.Reden.Should().Contain("Uitslag");
    }
}
