using System.Globalization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using BlazorAdmin;
using BlazorAdmin.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Kalenderweek begint op maandag, datumnotatie NL (issue #300)
var nlCulture = new CultureInfo("nl-NL");
CultureInfo.DefaultThreadCurrentCulture = nlCulture;
CultureInfo.DefaultThreadCurrentUICulture = nlCulture;
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// FunctionBaseUrl: in ontwikkeling het lokale adres, in productie het Function App adres.
var functionBaseUrl = builder.Configuration["FunctionBaseUrl"] ?? "";
if (string.IsNullOrWhiteSpace(functionBaseUrl))
    functionBaseUrl = builder.HostEnvironment.BaseAddress;

if (builder.HostEnvironment.IsProduction())
{
    // Productie: Entra ID Bearer token via MSAL — token wordt automatisch meegestuurd
    // naar de Function App (Easy Auth valideert server-side).
    var clientId = builder.Configuration["AzureAd:ClientId"]!;
    var apiScope = $"api://{clientId}/Admin.Access";

    builder.Services.AddMsalAuthentication(options =>
    {
        builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
        options.ProviderOptions.DefaultAccessTokenScopes.Add(apiScope);
        // 'redirect' (i.p.v. default 'popup') voorkomt popup-blocker issues in InPrivate/Incognito sessies.
        options.ProviderOptions.LoginMode = "redirect";
        // Entra schrijft app-rollen in de 'roles' claim. ClaimsPrincipal.IsInRole() leest standaard
        // van ClaimTypes.Role — expliciet "roles" instellen zodat de role-gate in App.razor werkt.
        options.UserOptions.RoleClaim = "roles";
    })
    // CustomUserFactory pakt de Entra 'roles' JSON-array uit naar losse claims.
    // Zonder dit cast Blazor de array naar één claim met de JSON-string als value,
    // waardoor IsInRole("admin") faalt ook al staat de rol in het token.
    // Zie BlazorAdmin/Services/CustomUserFactory.cs voor uitleg.
    .AddAccountClaimsPrincipalFactory<CustomUserFactory>();
    builder.Services.AddScoped<IAuthService, EntraAuthService>();

    // AdminApiClient met Bearer token: AuthorizationMessageHandler voegt automatisch
    // het Entra ID access token toe aan elk request richting de Function App URL.
    var capturedFunctionBaseUrl = functionBaseUrl;
    var capturedScope = apiScope;

    // App.razor injecteert HttpClient voor de health check — registreer plain client zonder auth.
    // AdminApiClient krijgt een eigen HttpClient mét AuthorizationMessageHandler (zie hieronder).
    builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(capturedFunctionBaseUrl) });

    builder.Services.AddScoped<AdminApiClient>(sp =>
    {
        var authHandler = sp.GetRequiredService<AuthorizationMessageHandler>()
            .ConfigureHandler(
                authorizedUrls: [capturedFunctionBaseUrl],
                scopes: [capturedScope]);
        // DelegatingHandler vereist een InnerHandler (de transport-laag).
        // Zonder dit gooit HttpClient 'net_http_handler_not_assigned'.
        // In Blazor WASM mapt HttpClientHandler op BrowserHttpHandler (browser fetch API).
        authHandler.InnerHandler = new HttpClientHandler();

        // ClubCodeHeaderHandler injecteert X-Club-Code op elk request (chain boven authHandler).
        var clubHandler = new ClubCodeHeaderHandler(sp.GetRequiredService<ClubSelectorService>())
        {
            InnerHandler = authHandler
        };
        var http = new HttpClient(clubHandler) { BaseAddress = new Uri(capturedFunctionBaseUrl) };
        return new AdminApiClient(http, sp.GetRequiredService<ApiStatusService>());
    });
}
else
{
    // Ontwikkeling: geen MSAL; fake AuthenticationStateProvider zodat [Authorize] werkt.
    builder.Services.AddAuthorizationCore();
    builder.Services.AddScoped<AuthenticationStateProvider, AlwaysAuthenticatedStateProvider>();
    builder.Services.AddScoped<IAuthService, LocalAuthService>();
    builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(functionBaseUrl) });
    builder.Services.AddScoped<AdminApiClient>(sp =>
    {
        var clubHandler = new ClubCodeHeaderHandler(sp.GetRequiredService<ClubSelectorService>())
        {
            InnerHandler = new HttpClientHandler()
        };
        var http = new HttpClient(clubHandler) { BaseAddress = new Uri(functionBaseUrl) };
        return new AdminApiClient(http, sp.GetRequiredService<ApiStatusService>());
    });
}

builder.Services.AddScoped<ClubSelectorService>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<ApiStatusService>();

await builder.Build().RunAsync();
