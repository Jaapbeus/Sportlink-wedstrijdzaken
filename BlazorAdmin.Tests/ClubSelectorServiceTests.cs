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

    private sealed class HangendeJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => new(new TaskCompletionSource<TValue>().Task);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var tcs = new TaskCompletionSource<TValue>();
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return new(tcs.Task);
        }
    }

    [Fact]
    public async Task InitializeAsync_NooitVoltooideOpslag_KomtNaTimeoutTerugZonderClub()
    {
        var service = new ClubSelectorService(new HangendeJs());

        var taak = service.InitializeAsync(TimeSpan.FromMilliseconds(50));
        var klaar = await Task.WhenAny(taak, Task.Delay(TimeSpan.FromSeconds(10)));

        klaar.Should().BeSameAs(taak);
        service.SelectedClubCode.Should().BeNull();
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

/// <summary>
/// #1578: de drie opstartscenario's als logica-keten (service + startpoort + tracker). Dit bewijst de
/// beslislogica, niet de Blazor-component-lifecycle zelf (daar is geen componenttest voor).
/// </summary>
public class PaginaStartPoortTests
{
    private sealed class Pagina
    {
        private readonly ClubWisselTracker _tracker = new();
        public int Laadrondes { get; private set; }

        public Pagina(ClubSelectorService service)
        {
            // Nabootsing van ClubSelectorPageBase + de eerste load in OnInitializedAsync.
            _tracker.Markeer(service.SelectedClubCode);
            Laadrondes = 1;
            service.OnChange += () => { if (_tracker.MoetHerladen(service.SelectedClubCode)) Laadrondes++; };
        }
    }

    private sealed class OpslagJs(string? opgeslagen) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => new(identifier == "localStorage.getItem" ? (TValue)(object?)opgeslagen! : default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }

    [Fact]
    public async Task LegeOpslag_PaginaWachtOpClublijst_PreciesEenLaadronde()
    {
        var service = new ClubSelectorService(new OpslagJs(null));
        var poort = new PaginaStartPoort();

        await service.InitializeAsync();
        poort.MarkeerOpslagGelezen(service.SelectedClubCode);
        poort.IsVrij.Should().BeFalse("zonder club en zonder clublijst mag de pagina nog niet laden");

        await service.SelectClubAsync("AAA", "Club A"); // primaire club uit de clublijst
        poort.MarkeerClublijstAfgerond(service.SelectedClubCode);
        poort.IsVrij.Should().BeTrue();

        var pagina = new Pagina(service);
        service.SynchroniseerClubNaam("Club A");

        pagina.Laadrondes.Should().Be(1);
    }

    [Fact]
    public async Task GeldigeOpgeslagenClub_PaginaLaadtMeteen_PreciesEenLaadronde()
    {
        var service = new ClubSelectorService(new OpslagJs("AAA"));
        var poort = new PaginaStartPoort();

        await service.InitializeAsync();
        poort.MarkeerOpslagGelezen(service.SelectedClubCode);
        poort.IsVrij.Should().BeTrue();

        var pagina = new Pagina(service);
        await service.SelectClubAsync("AAA", "Club A"); // naamsync na clublijst
        service.SynchroniseerClubNaam("Club A");
        poort.MarkeerClublijstAfgerond(service.SelectedClubCode);

        pagina.Laadrondes.Should().Be(1);
    }

    [Fact]
    public async Task EchteClubwissel_HerlaadtDePaginaEenmaal()
    {
        var service = new ClubSelectorService(new OpslagJs("AAA"));
        await service.InitializeAsync();
        var pagina = new Pagina(service);

        await service.SelectClubAsync("BBB", "Club B");

        pagina.Laadrondes.Should().Be(2);
    }

    [Fact]
    public void ClublijstMislukt_PoortKomtTochVrij_ZodatPaginasHunFoutTonen()
    {
        var poort = new PaginaStartPoort();
        poort.MarkeerOpslagGelezen(null);
        poort.IsVrij.Should().BeFalse();

        poort.MarkeerClublijstAfgerond(null);

        poort.IsVrij.Should().BeTrue();
    }

    [Fact]
    public void OpslagNogNietGelezen_PoortBlijftDicht_OokAlIsDeClublijstAfgerond()
    {
        var poort = new PaginaStartPoort();
        poort.MarkeerClublijstAfgerond("AAA");

        poort.IsVrij.Should().BeFalse();
    }
}
