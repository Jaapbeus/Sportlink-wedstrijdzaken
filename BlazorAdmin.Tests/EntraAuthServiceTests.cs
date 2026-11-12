using System.Security.Claims;
using AwesomeAssertions;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// Covers the retained Entra auth-state projection. App.razor remains the role-gate authority;
/// these tests verify that cleanup of unused auth members doesn't alter either layer.
/// </summary>
public sealed class EntraAuthServiceTests
{
    private const string AppUri = "https://example.invalid/dagplanning";

    private sealed class FakeAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(user));
    }

    private sealed class RecordingNavigationManager : NavigationManager
    {
        public string? NavigatedTo { get; private set; }

        public RecordingNavigationManager()
            => Initialize(AppUri, AppUri);

        protected override void NavigateToCore(string uri, NavigationOptions options)
            => NavigatedTo = uri;
    }

    [Theory]
    [InlineData(true, "admin", true, AppState.Authenticated)]
    [InlineData(true, "user", false, AppState.Authenticated)]
    [InlineData(true, null, false, AppState.AccessDenied)]
    [InlineData(false, null, false, AppState.RedirectingToLogin)]
    public void Auth_state_and_app_gate_keep_the_four_supported_profiles(
        bool authenticated, string? role, bool expectedAdmin, AppState expectedAppState)
    {
        var user = Gebruiker(authenticated, role);
        var service = CreateService(user, new RecordingNavigationManager());

        service.IsAuthenticated.Should().Be(authenticated);
        service.UserName.Should().Be(authenticated ? "Test User" : "");
        service.IsAdmin.Should().Be(expectedAdmin);
        AuthGate.Bepaal(AppUri, user).Should().Be(expectedAppState);
    }

    [Fact]
    public async Task Logout_preserves_the_Entra_logout_redirect()
    {
        var navigation = new RecordingNavigationManager();
        var service = CreateService(Gebruiker(authenticated: true, role: "admin"), navigation);

        await service.LogoutAsync();

        navigation.NavigatedTo.Should().Contain("authentication/logout");
    }

    private static EntraAuthService CreateService(ClaimsPrincipal user, NavigationManager navigation)
        => new(new FakeAuthenticationStateProvider(user), navigation);

    private static ClaimsPrincipal Gebruiker(bool authenticated, string? role)
    {
        if (!authenticated)
            return new ClaimsPrincipal(new ClaimsIdentity());

        var identity = new ClaimsIdentity(authenticationType: "TestEntra", nameType: "name", roleType: "roles");
        identity.AddClaim(new Claim("name", "Test User"));
        if (role is not null)
            identity.AddClaim(new Claim("roles", role));
        return new ClaimsPrincipal(identity);
    }
}
