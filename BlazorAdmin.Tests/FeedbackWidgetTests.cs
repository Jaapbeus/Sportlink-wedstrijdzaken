using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Planner.Shared.Feedback;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>Teksten, weergave en technische context van de feedbackwidget en het overzicht (#764).</summary>
public class FeedbackWidgetTests
{
    [Fact]
    public void Types_BehoudenDeVasteServerwaarden_AlleenHetLabelIsGebruikerstaal()
    {
        FeedbackWidgetState.Types.Select(t => t.Waarde).Should().Equal("Fout", "Verzoek", "Vraag");
        FeedbackWidgetState.LabelVoor("Fout").Should().Be("Er gaat iets mis");
        FeedbackWidgetState.LabelVoor("Onbekend").Should().Be("Onbekend");
    }

    [Fact]
    public void GewoneGebruiker_KrijgtGeenGitHubJargon_EnGeenIssuelink()
    {
        var tekst = FeedbackWidgetState.PubliciteitWaarschuwing(isAdmin: false) + FeedbackWidgetState.PrivacyUitleg(false);

        tekst.Should().NotContain("GitHub").And.NotContain("issue");
        FeedbackWidgetState.BevestigingTekst(false, new FeedbackSubmitResponse { Gepubliceerd = false })
            .Should().Contain("reageert niet persoonlijk").And.Contain("Meld het gerust opnieuw");
    }

    [Fact]
    public void Beheerder_KrijgtDeBestaandePublicatiewaarschuwing()
    {
        FeedbackWidgetState.PubliciteitWaarschuwing(isAdmin: true).Should().Contain("openbaar").And.Contain("GitHub-issue");
        FeedbackWidgetState.BevestigingTekst(true, new FeedbackSubmitResponse { Gepubliceerd = true }).Should().Contain("ontvangen");
        FeedbackWidgetState.BevestigingTekst(true, new FeedbackSubmitResponse { Gepubliceerd = false }).Should().Contain("bewaard");
    }

    [Theory]
    [InlineData("wacht-op-publicatie", true)]
    [InlineData("github-mislukt", true)]
    [InlineData("gepubliceerd", false)]
    [InlineData("publiceren", false)]
    public void KanPubliceren_AlleenVoorWachtendeOfMislukteMeldingen(string status, bool verwacht) =>
        FeedbackWeergave.KanPubliceren(status).Should().Be(verwacht);

    [Fact]
    public void MelderTekst_ToontGeanonimiseerdInPlaatsVanDeNaam()
    {
        FeedbackWeergave.MelderTekst("Jan", false).Should().Be("Jan");
        FeedbackWeergave.MelderTekst(null, true).Should().Be("— (geanonimiseerd)");
        FeedbackWeergave.MelderTekst(null, false).Should().Be("—");
    }

    [Fact]
    public void Filter_Querystring_SluitLegeWaardenUitEnEscaped()
    {
        var filter = new FeedbackFilterDto { Type = "Fout", Zoek = "a&b c", Limit = 20, Offset = 40 };

        filter.NaarQuerystring().Should().Be("?type=Fout&q=a%26b%20c&limit=20&offset=40");
    }

    // ── Technische context ─────────────────────────────────────────────────────────────────────

    private sealed class FakeNav : NavigationManager
    {
        public FakeNav() => Initialize("http://localhost/", "http://localhost/teams?zoek=JO13-1");

        public void Ga(string pad)
        {
            Uri = "http://localhost" + pad;
            NotifyLocationChanged(false);
        }

        protected override void NavigateToCore(string uri, NavigationOptions options) => Ga(uri);
    }

    private sealed class FakeJs(string[] consoleFouten, string browser = "Chrome 141 op Windows") : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object? waarde = identifier switch
            {
                "feedbackTelemetry.consoleFouten" => consoleFouten,
                "feedbackTelemetry.schermbreedte" => 1920,
                "blazorHelpers.getUserAgent" => browser,
                _ => throw new JSException("onbekend")
            };
            return ValueTask.FromResult((TValue)waarde!);
        }
    }

    [Fact]
    public async Task Verzamel_GeeftGeredigeerdeContext_ZonderQuerystringsEnPersoonsgegevens()
    {
        var nav = new FakeNav();
        using var service = new ClientTelemetryService(nav, new FakeJs(["Fout voor trainer@voorbeeld.nl"]));
        nav.Ga("/velden?team=JO13-1");
        nav.Ga("/instellingen");
        service.MeldMislukteAanroep("POST", "api/beheer/velden?id=42", 500, "a3f9c2");

        var context = await service.VerzamelAsync();
        var tekst = context.NaarTekst();

        tekst.Should().NotContain("trainer@").And.NotContain("zoek=").And.NotContain("team=").And.NotContain("id=42");
        context.Navigatiespoor.Should().Equal("/teams", "/velden");
        tekst.Should().Contain("POST /api/beheer/velden").And.Contain("500").And.Contain("Chrome 141").And.Contain("1920");
    }

    [Fact]
    public async Task Verzamel_BehoudtMaximaalVijfMislukteAanroepen()
    {
        using var service = new ClientTelemetryService(new FakeNav(), new FakeJs([]));
        for (var i = 1; i <= 8; i++)
            service.MeldMislukteAanroep("GET", $"api/x{i}", 500, null);

        var context = await service.VerzamelAsync();

        context.MislukteAanroepen.Should().HaveCount(5);
        context.MislukteAanroepen[0].Pad.Should().Be("/api/x4");
    }
}
