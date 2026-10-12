using System.Text.Json;
using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Gedeelde, per club gecachete leesroute voor de club-instellingen (<c>GET api/beheer/settings</c>) en de
/// menu-toegang van de ingelogde gebruiker (<c>GET api/planner/menu-toegang</c>) — #1599/#1647.
/// <para>
/// Vóór deze cache haalden <c>NavMenu</c> (voor de extensievlag) en de pagina's Planning, Sportlink Web
/// Extension, enz. elk dezelfde instellingen op: twee identieke aanroepen per paginaopening. Nu delen ze één
/// aanroep: gelijktijdige lezers wachten op dezelfde lopende aanroep, een volgende lezer van dezelfde club
/// krijgt het bewaarde antwoord. De cache vervalt bij een clubwissel (andere clubcode), na opslaan via
/// <see cref="UpdateSettingsAsync"/>, en na <see cref="MaxLeeftijd"/> als vangnet voor wijzigingen van een ander.
/// Mislukte aanroepen worden nooit bewaard. Lezers krijgen een kopie, zodat een pagina die haar formulier op
/// het resultaat bewerkt de gedeelde stand niet vervuilt.
/// </para>
/// </summary>
public sealed class ClubInstellingenCache : IDisposable
{
    /// <summary>Hoe lang een bewaard antwoord geldig blijft als niets het eerder ongeldig maakt.</summary>
    public static readonly TimeSpan MaxLeeftijd = TimeSpan.FromMinutes(5);

    /// <summary>Venster waarin een pagina die bewust "vers" leest nog het antwoord deelt van de aanroep die het menu
    /// net deed — anders zou de eerste paginaopening alsnog twee aanroepen kosten.</summary>
    public static readonly TimeSpan VersVenster = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InstellingenApiClient _api;
    private readonly ClubSelectorService _club;
    private readonly Bewaard<AppSettingsDto> _settings;
    private readonly Bewaard<MenuToegangDto> _menuToegang;

    public ClubInstellingenCache(InstellingenApiClient api, ClubSelectorService club, TimeProvider? tijd = null)
    {
        _api = api;
        _club = club;
        var klok = tijd ?? TimeProvider.System;
        _settings = new Bewaard<AppSettingsDto>(club, klok, _api.GetSettingsAsync);
        _menuToegang = new Bewaard<MenuToegangDto>(club, klok, _api.GetMenuToegangAsync);
        _club.OnChange += Wis;
    }

    /// <summary>De instellingen van de gekozen club. <paramref name="vers"/>: sla een bewaard antwoord over (voor
    /// de instellingenpagina zelf), behalve als het net is opgehaald (<see cref="VersVenster"/>).</summary>
    public Task<ApiResult<AppSettingsDto>> GetSettingsAsync(bool vers = false) =>
        _settings.GetAsync(Kopieer, vers ? VersVenster : MaxLeeftijd);

    /// <summary>Slaat instellingen op en maakt de bewaarde stand bij succes ongeldig, zodat elke volgende lezer
    /// de nieuwe waarden ziet. Pagina's roepen dit aan in plaats van <c>UpdateSettingsAsync</c> op de client.</summary>
    public async Task<ApiResult<SettingsUpdateResultDto>> UpdateSettingsAsync(SettingsUpdateDto dto)
    {
        var resultaat = await _api.UpdateSettingsAsync(dto);
        if (resultaat.Success)
        {
            _settings.Wis();
            _menuToegang.Wis(); // bevat de extensievlag
        }
        return resultaat;
    }

    /// <summary>Menu-sleutels die de ingelogde (niet-admin) gebruiker volgens "Rechten per rol" mag zien.</summary>
    public Task<ApiResult<MenuToegangDto>> GetMenuToegangAsync() => _menuToegang.GetAsync(Kopieer, MaxLeeftijd);

    /// <summary>Maakt alles ongeldig (bijvoorbeeld na wijziging van de rechten per rol).</summary>
    public void Wis()
    {
        _settings.Wis();
        _menuToegang.Wis();
    }

    public void Dispose() => _club.OnChange -= Wis;

    private static T Kopieer<T>(T bron) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(bron, Json), Json)!;

    /// <summary>Eén bewaard antwoord, gekoppeld aan de clubcode waarvoor het is opgehaald.</summary>
    private sealed class Bewaard<T> where T : class
    {
        private readonly ClubSelectorService _club;
        private readonly TimeProvider _tijd;
        private readonly Func<Task<ApiResult<T>>> _laad;
        private readonly object _slot = new();
        private Task<ApiResult<T>>? _taak;
        private string? _voorClub;
        private DateTimeOffset _gestart;

        public Bewaard(ClubSelectorService club, TimeProvider tijd, Func<Task<ApiResult<T>>> laad)
        {
            _club = club;
            _tijd = tijd;
            _laad = laad;
        }

        public void Wis()
        {
            lock (_slot)
            {
                _taak = null;
            }
        }

        public async Task<ApiResult<T>> GetAsync(Func<T, T> kopieer, TimeSpan maxLeeftijd)
        {
            var resultaat = await Huidige(maxLeeftijd);
            return resultaat.Success && resultaat.Data is not null
                ? ApiResult<T>.Ok(kopieer(resultaat.Data), resultaat.StatusCode)
                : resultaat;
        }

        private Task<ApiResult<T>> Huidige(TimeSpan maxLeeftijd)
        {
            var club = _club.SelectedClubCode;
            var nu = _tijd.GetUtcNow();
            lock (_slot)
            {
                // Een afgeronde mislukte aanroep wordt nooit hergebruikt: de eerstvolgende lezer probeert het opnieuw.
                var bruikbaar = _taak is not null
                    && !(_taak.IsCompleted && (!_taak.IsCompletedSuccessfully || !_taak.Result.Success))
                    && string.Equals(_voorClub, club, StringComparison.Ordinal)
                    && nu - _gestart <= maxLeeftijd;
                if (!bruikbaar)
                {
                    _voorClub = club;
                    _gestart = nu;
                    _taak = _laad();
                }
                return _taak!;
            }
        }
    }
}
