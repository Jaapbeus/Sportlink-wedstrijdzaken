using AwesomeAssertions;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>Een niet beschikbaar toegangstoken loopt via de redirect-afhandeling, niet via de generieke foutvangst.</summary>
public class AdminApiTransportTokenTests
{
    private sealed class FakeNav : NavigationManager
    {
        public string? Doel { get; private set; }
        public FakeNav() => Initialize("http://localhost/", "http://localhost/teams");
        protected override void NavigateToCore(string uri, NavigationOptions options) => Doel = uri;
    }

    private sealed class TokenOntbreektHandler(NavigationManager nav) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new AccessTokenNotAvailableException(
                nav,
                new AccessTokenResult(AccessTokenResultStatus.RequiresRedirect, null, "authentication/login", new InteractiveRequestOptions { Interaction = InteractionType.GetToken, ReturnUrl = "http://localhost/teams" }),
                Array.Empty<string>());
    }

    [Fact]
    public async Task TokenNietBeschikbaar_StartDeRedirectEnMeldtEenFout()
    {
        var nav = new FakeNav();
        var transport = new AdminApiTransport(new HttpClient(new TokenOntbreektHandler(nav)) { BaseAddress = new Uri("http://localhost/api/") });

        var resultaat = await transport.GetAsync<object>("teams");

        resultaat.Success.Should().BeFalse();
        nav.Doel.Should().NotBeNull();
    }
}
