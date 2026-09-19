using System.Security.Claims;
using AwesomeAssertions;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// Auth-laag 4 — de frontend role-gate (#1277).
/// </summary>
/// <remarks>
/// Vóór #1277 had deze laag geen enkele automatische controle: de beslissing stond inline in het
/// <c>@code</c>-blok van <c>App.razor</c> en viel daardoor buiten elk testproject.
/// <para>
/// Laag 5 (<c>EasyAuthHelper.RequireAdmin</c>) blijft leidend voor databescherming — de server is
/// de waarheid. Deze tests gaan over de vraag of de app-shell wordt getoond, niet of data
/// beschermd is.
/// </para>
/// </remarks>
public class AuthGateTests
{
    private const string AppUri  = "https://example.invalid/dagplanning";
    private const string AuthUri = "https://example.invalid/authentication/login-callback";

    private static ClaimsPrincipal Gebruiker(params string[] rollen)
    {
        // roleType "roles" — exact wat Entra levert en wat Program.cs via
        // options.UserOptions.RoleClaim instelt. Een andere waarde hier zou de test
        // laten slagen op een claimtype dat in productie niet bestaat.
        var identity = new ClaimsIdentity(authenticationType: "TestEntra", nameType: "name", roleType: "roles");
        identity.AddClaim(new Claim("name", "Testgebruiker"));
        foreach (var rol in rollen) identity.AddClaim(new Claim("roles", rol));
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal Anoniem() => new(new ClaimsIdentity());

    // ── De drie gevallen uit issue #1277 ──────────────────────────────────────

    [Fact]
    public void Rol_admin_krijgt_de_volledige_app()
        => AuthGate.Bepaal(AppUri, Gebruiker("admin")).Should().Be(AppState.Authenticated);

    [Fact]
    public void Rol_user_krijgt_de_volledige_app()
        => AuthGate.Bepaal(AppUri, Gebruiker("user")).Should().Be(AppState.Authenticated);

    [Fact]
    public void Ingelogd_zonder_rol_krijgt_NoAccess_en_dus_geen_MainLayout()
        => AuthGate.Bepaal(AppUri, Gebruiker()).Should().Be(AppState.AccessDenied);

    // ── Randgevallen die dezelfde laag dragen ────────────────────────────────

    [Fact]
    public void Een_rol_die_wij_niet_kennen_geeft_geen_toegang()
        => AuthGate.Bepaal(AppUri, Gebruiker("Wedstrijdzaken")).Should().Be(AppState.AccessDenied);

    [Fact]
    public void Niet_ingelogd_wordt_naar_de_login_gestuurd()
        => AuthGate.Bepaal(AppUri, Anoniem()).Should().Be(AppState.RedirectingToLogin);

    [Fact]
    public void De_MSAL_callbackroute_gaat_altijd_door_ook_zonder_sessie()
    {
        // Zou de gate deze route naar de login sturen, dan ontstaat er een redirect-lus:
        // dit is juist de route die de sessie tot stand brengt.
        AuthGate.Bepaal(AuthUri, Anoniem()).Should().Be(AppState.OnAuthRoute);
        AuthGate.IsAuthRoute(AuthUri).Should().BeTrue();
        AuthGate.IsAuthRoute(AppUri).Should().BeFalse();
    }

    [Fact]
    public void Rolvergelijking_is_hoofdlettergevoelig_zoals_Entra_hem_levert()
    {
        // Entra levert de rol exact zoals hij in het App Registration-manifest staat.
        // Deze test legt het huidige gedrag vast: "Admin" is niet "admin". Verandert dat,
        // dan is dat een bewuste keuze en geen toevalligheid.
        AuthGate.Bepaal(AppUri, Gebruiker("Admin")).Should().Be(AppState.AccessDenied);
    }

    [Fact]
    public void Beide_rollen_tegelijk_geeft_gewoon_toegang()
        => AuthGate.Bepaal(AppUri, Gebruiker("admin", "user")).Should().Be(AppState.Authenticated);
}
