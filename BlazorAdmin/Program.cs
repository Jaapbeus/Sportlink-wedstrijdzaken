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

// Auth: lokale dev = LocalAuthService (altijd admin); productie = idem totdat MSAL volledig geconfigureerd is.
// De feitelijke routebescherming verloopt in productie via staticwebapp.config.json (SWA Entra ID).
// Zie docs/v2-admin-handleiding.md voor Entra ID app-registratie en het wisselen naar EntraAuthService.
builder.Services.AddScoped<IAuthService, LocalAuthService>();

await builder.Build().RunAsync();
