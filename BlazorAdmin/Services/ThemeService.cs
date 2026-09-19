using BlazorAdmin.Models;
using Microsoft.JSInterop;

namespace BlazorAdmin.Services;

/// <summary>
/// Laadt het club-thema vanuit de API en past CSS-variabelen + favicon toe via JSInterop.
/// v2 — #325/#339, licht/donker toegevoegd in #1256 (epic #1249).
/// </summary>
public class ThemeService
{
    private readonly AdminApiClient _api;
    private readonly IJSRuntime _js;

    public string? LogoUrl    { get; private set; }
    public string? FaviconUrl { get; private set; }

    public event Action? OnThemeChanged;

    /// <summary>Wordt gemeld zodra de licht/donker-modus wijzigt, zodat de schakelaar meeloopt.</summary>
    public event Action<string>? OnModeChanged;

    public ThemeService(AdminApiClient api, IJSRuntime js)
    {
        _api = api;
        _js = js;
    }

    public async Task LoadAndApplyAsync()
    {
        var result = await _api.GetThemeAsync();
        if (result.Success && result.Data != null)
            await ApplyAsync(result.Data);
    }

    public async Task ApplyAsync(ThemeDto theme)
    {
        LogoUrl    = theme.LogoUrl;
        FaviconUrl = theme.FaviconUrl;

        try
        {
            await _js.InvokeVoidAsync("themeHelper.applyMode", LichtPalet(theme), DonkerPalet(theme));

            if (!string.IsNullOrWhiteSpace(theme.FaviconUrl))
                await _js.InvokeVoidAsync("themeHelper.setFavicon", theme.FaviconUrl);
        }
        catch
        {
            // JSInterop kan falen als WASM nog niet volledig geladen is — stilzwijgend negeren
        }

        OnThemeChanged?.Invoke();
    }

    /// <summary>
    /// De modus zoals die nu in het DOM staat. Gezet door de IIFE in <c>theme.js</c>, vóórdat
    /// Blazor boot — dus lezen, niet zelf bepalen.
    /// </summary>
    public async Task<string> GetModeAsync()
    {
        try
        {
            return await _js.InvokeAsync<string>("themeHelper.getMode");
        }
        catch
        {
            return "light";
        }
    }

    public async Task SetModeAsync(string mode)
    {
        if (mode is not ("light" or "dark")) return;

        try
        {
            await _js.InvokeVoidAsync("themeHelper.setMode", mode);
        }
        catch
        {
            // Zie ApplyAsync — JSInterop vóór volledige WASM-start.
        }

        OnModeChanged?.Invoke(mode);
    }

    /// <summary>
    /// Het lichte palet van de club, of — zolang die er nog geen heeft ingesteld (#1254) — de vier
    /// platte kleuren. Zonder die terugval zou elke bestaande club zijn thema kwijtraken zodra
    /// deze versie live gaat, want die clubs hebben alleen de platte kolommen gevuld.
    /// </summary>
    private static Dictionary<string, string> LichtPalet(ThemeDto theme)
    {
        if (theme.LightColors is { Count: > 0 }) return new Dictionary<string, string>(theme.LightColors);

        return new Dictionary<string, string>
        {
            ["primary"]       = theme.Primary,
            ["secondary"]     = theme.Secondary,
            ["accent"]        = theme.Accent,
            ["textOnPrimary"] = theme.TextOnPrimary
        };
    }

    /// <summary>
    /// Het donkere palet van de club. Leeg is hier wél een geldige uitkomst: app.css heeft eigen
    /// neutrale donkerwaarden (#1255), dus een club zonder eigen donkere set krijgt die te zien in
    /// plaats van niets.
    /// </summary>
    private static Dictionary<string, string> DonkerPalet(ThemeDto theme) =>
        theme.DarkColors is { Count: > 0 }
            ? new Dictionary<string, string>(theme.DarkColors)
            : new Dictionary<string, string>();
}
