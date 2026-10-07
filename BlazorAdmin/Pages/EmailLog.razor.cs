using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>
/// E-maillog met de beslissingstrace per regel (#1583). Voorheen alleen bereikbaar via de knop "Toon berichten en
/// traces" op Instellingen; dit is het eigen menu-item. Hergebruikt <c>EmailLogLijst</c> en dezelfde API-aanroep als
/// Instellingen. Alleen lezen: de pagina bewaart niets.
/// </summary>
public partial class EmailLog : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private string _status = "";
    private string _periode = "7d";
    private EmailLogLader _lader = default!;

    private EmailLogResponse? _log => _lader.Log;
    private bool _bezig => _lader.Bezig;
    private string? _fout => _lader.Fout;

    private string Samenvatting => _log is null
        ? ""
        : _log.Items.Count >= EmailLogFilter.MaxRegels
            ? $"{_log.Items.Count} berichten (de limiet is bereikt: kies een kortere periode of een status om verder in te zoomen)"
            : $"{_log.Items.Count} berichten";

    protected override Task OnInitializedAsync()
    {
        _lader = new EmailLogLader((vanaf, status, limiet) => Api.GetEmailLogAsync(vanaf: vanaf, status: status, limit: limiet));
        return LaadAsync();
    }

    // Een clubwissel leegt de lijst van de vorige club en laat een nog lopende aanvraag van die club vervallen.
    protected override Task OnClubChangedAsync() => LaadAsync(wisHuidige: true);

    private Task LaadAsync() => LaadAsync(wisHuidige: false);

    private async Task LaadAsync(bool wisHuidige)
    {
        var laden = _lader.LaadAsync(_status, _periode, DateTime.Today, wisHuidige);
        StateHasChanged();
        await laden;
    }
}
