using System.Security.Claims;

namespace BlazorAdmin.Services;

/// <summary>
/// De toestand die <c>App.razor</c> rendert. Publiek omdat de beslissing die eruit volgt
/// (auth-laag 4, de frontend role-gate) los getest moet kunnen worden — zie <see cref="AuthGate"/>.
/// </summary>
public enum AppState
{
    /// <summary>Nog geen uitspraak gedaan; toont alleen een spinner, nooit een layout.</summary>
    Initializing,
    /// <summary>MSAL-callbackroute (<c>/authentication/...</c>) — eigen Router-tak zonder layout.</summary>
    OnAuthRoute,
    /// <summary>Ingelogd mét <c>admin</c>- of <c>user</c>-rol: de volledige app met MainLayout.</summary>
    Authenticated,
    /// <summary>Ingelogd zonder rol: uitsluitend de NoAccess-pagina, géén MainLayout.</summary>
    AccessDenied,
    /// <summary>Niet ingelogd: er is naar de Microsoft-login doorgestuurd.</summary>
    RedirectingToLogin,
}

/// <summary>
/// Auth-laag 4 — de frontend role-gate uit de defense-in-depth-tabel in <c>CLAUDE.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deze beslissing stond tot #1277 als inline logica in het <c>@code</c>-blok van
/// <c>App.razor</c> en had daardoor <b>geen enkele automatische controle</b>: een
/// <c>@code</c>-blok is niet los te instantiëren, dus er viel niets te asserten zonder de
/// hele component te renderen. Hij staat hier nu als pure functie zonder framework-afhankelijkheden,
/// zodat <c>BlazorAdmin.Tests</c> hem rechtstreeks kan bevragen.
/// </para>
/// <para>
/// <b>Laag 5 blijft leidend voor databescherming.</b> De server is de waarheid: een gebruiker die
/// hier langskomt wordt alsnog door <c>EasyAuthHelper.RequireAdmin()</c> tegengehouden. Laag 4
/// bestaat zodat iemand zonder rol niet de volledige app-shell te zien krijgt.
/// </para>
/// </remarks>
public static class AuthGate
{
    /// <summary>De twee rollen die toegang tot de app-shell geven.</summary>
    public static readonly string[] ToegangsRollen = ["admin", "user"];

    /// <summary>
    /// Is dit de MSAL-callbackroute? Apart aanroepbaar omdat <c>App.razor</c> op die route
    /// bewust géén authenticatiestatus ophaalt — die route brengt de sessie juist tot stand.
    /// </summary>
    public static bool IsAuthRoute(string huidigeUri) =>
        huidigeUri.Contains("/authentication/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Bepaalt welke toestand <c>App.razor</c> moet renderen.
    /// </summary>
    /// <param name="huidigeUri">De volledige URI van de browser.</param>
    /// <param name="gebruiker">De principal uit de <c>AuthenticationStateProvider</c>.</param>
    public static AppState Bepaal(string huidigeUri, ClaimsPrincipal gebruiker)
    {
        // De MSAL-callback moet altijd door, óók zonder (nog) geldige sessie: hij is juist de
        // route die de sessie tot stand brengt. Eerst controleren, anders stuurt de gate de
        // callback terug naar de login en ontstaat er een lus.
        if (IsAuthRoute(huidigeUri))
            return AppState.OnAuthRoute;

        if (gebruiker.Identity?.IsAuthenticated != true)
            return AppState.RedirectingToLogin;

        // Ingelogd zijn is niet genoeg: een tenant-gebruiker zonder toegewezen app-rol komt
        // door Entra heen met een geldig token. Dat is precies het geval dat laag 4 afvangt.
        foreach (var rol in ToegangsRollen)
        {
            if (gebruiker.IsInRole(rol)) return AppState.Authenticated;
        }

        return AppState.AccessDenied;
    }
}
