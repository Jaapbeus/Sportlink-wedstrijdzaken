using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BlazorAdmin;
using BlazorAdmin.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// BaseUrl voor Function App calls. Leeg in productie (SWA proxy), localhost in dev.
var functionBaseUrl = builder.Configuration["FunctionBaseUrl"] ?? "";
if (string.IsNullOrWhiteSpace(functionBaseUrl))
    functionBaseUrl = builder.HostEnvironment.BaseAddress;

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(functionBaseUrl) });
builder.Services.AddScoped<AdminApiClient>();

// Auth: Production = Entra ID via MSAL + EntraAuthService (rollen uit claims).
//       Development = LocalAuthService (altijd admin, geen login — voor lokaal testen).
// SWA-routing in staticwebapp.config.json beschermt de /api/beheer/* endpoints ook server-side.
// EntraAuthService geeft de Blazor UI de juiste rolinfo voor menu-items verbergen.
if (builder.HostEnvironment.IsProduction())
{
    builder.Services.AddMsalAuthentication(options =>
    {
        builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    });
    builder.Services.AddScoped<IAuthService, EntraAuthService>();
}
else
{
    builder.Services.AddScoped<IAuthService, LocalAuthService>();
}

await builder.Build().RunAsync();
