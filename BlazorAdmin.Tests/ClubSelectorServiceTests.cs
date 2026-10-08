using AwesomeAssertions;
using BlazorAdmin.Services;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>#1578: <c>OnChange</c> gaat alleen af bij een echte clubwissel; naam en menuvlag hebben eigen gebeurtenissen.</summary>
public class ClubSelectorServiceTests
{
    private sealed class FakeJs(string? opgeslagen = null) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => new(identifier == "localStorage.getItem" ? (TValue)(object?)opgeslagen! : default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }

    private static (ClubSelectorService Service, Counters Teller) Maak(string? opgeslagen = null)
    {
        var service = new ClubSelectorService(new FakeJs(opgeslagen));
        var teller = new Counters();
        service.OnChange += () => teller.Club++;
        service.OnClubNameChange += () => teller.Naam++;
        service.OnSportlinkExtensionChange += () => teller.Vlag++;
        return (service, teller);
    }

    private sealed class Counters { public int Club, Naam, Vlag; }

    [Fact]
    public async Task SelectClubAsync_AndereClub_VuurtOnChange()
    {
        var (service, teller) = Maak();
        await service.SelectClubAsync("AAA", "Club A");
        await service.SelectClubAsync("BBB", "Club B");

        teller.Club.Should().Be(2);
        service.SelectedClubCode.Should().Be("BBB");
        service.SelectedClubName.Should().Be("Club B");
    }

    [Fact]
    public async Task SelectClubAsync_ZelfdeClub_VuurtGeenOnChange()
    {
        var (service, teller) = Maak();
        await service.SelectClubAsync("AAA", "Club A");
        teller.Club = 0;

        await service.SelectClubAsync("AAA", "Club A");
        await service.SelectClubAsync("AAA");

        teller.Club.Should().Be(0);
        service.SelectedClubName.Should().Be("Club A");
    }

    [Fact]
    public async Task SelectClubAsync_ZelfdeClubAndereNaam_WerktNaamStilBijZonderOnChange()
    {
        var (service, teller) = Maak();
        await service.SelectClubAsync("AAA", null);
        teller.Club = 0;

        await service.SelectClubAsync("AAA", "Club A");

        teller.Club.Should().Be(0);
        teller.Naam.Should().Be(1);
        service.SelectedClubName.Should().Be("Club A");
    }

    [Fact]
    public async Task SynchroniseerClubNaam_LegeOfGelijkeNaam_DoetNiets()
    {
        var (service, teller) = Maak();
        await service.SelectClubAsync("AAA", "Club A");

        service.SynchroniseerClubNaam("Club A");
        service.SynchroniseerClubNaam(null);
        service.SynchroniseerClubNaam("  ");

        teller.Naam.Should().Be(0);
        service.SelectedClubName.Should().Be("Club A");
    }

    [Fact]
    public void ZetSportlinkExtensionEnabled_VuurtAlleenDeVlaggebeurtenis_EnAlleenBijWijziging()
    {
        var (service, teller) = Maak();

        service.ZetSportlinkExtensionEnabled(true);
        service.ZetSportlinkExtensionEnabled(true);
        service.ZetSportlinkExtensionEnabled(false);

        teller.Vlag.Should().Be(2);
        teller.Club.Should().Be(0);
        service.SportlinkExtensionEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_OpgeslagenClubWijktAf_VuurtOnChangeEenmaal()
    {
        var (service, teller) = Maak("AAA");

        await service.InitializeAsync();
        await service.InitializeAsync();

        service.SelectedClubCode.Should().Be("AAA");
        teller.Club.Should().Be(1);
    }
}

/// <summary>#1578: een pagina slaat een herlaadronde over als de club gelijk is.</summary>
public class ClubWisselTrackerTests
{
    [Fact]
    public void MoetHerladen_ZelfdeClubAlsBijInitialisatie_IsFalse()
    {
        var tracker = new ClubWisselTracker();
        tracker.Markeer("AAA");

        tracker.MoetHerladen("AAA").Should().BeFalse();
    }

    [Fact]
    public void MoetHerladen_EchteClubwissel_IsTrue_EnOnthoudtDeNieuweClub()
    {
        var tracker = new ClubWisselTracker();
        tracker.Markeer("AAA");

        tracker.MoetHerladen("BBB").Should().BeTrue();
        tracker.MoetHerladen("BBB").Should().BeFalse();
        tracker.MoetHerladen("AAA").Should().BeTrue();
    }

    [Fact]
    public void MoetHerladen_VanGeenClubNaarClub_IsTrue()
    {
        var tracker = new ClubWisselTracker();
        tracker.Markeer(null);

        tracker.MoetHerladen("AAA").Should().BeTrue();
    }
}
