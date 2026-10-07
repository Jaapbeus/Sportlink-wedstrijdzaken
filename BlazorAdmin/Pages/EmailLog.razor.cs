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
    private EmailLogResponse? _log;
    private bool _bezig;
    private string? _fout;

    private string Samenvatting => _log is null
        ? ""
        : _log.Items.Count >= EmailLogFilter.MaxRegels
            ? $"{_log.Items.Count} berichten (de limiet is bereikt: kies een kortere periode of een status om verder in te zoomen)"
            : $"{_log.Items.Count} berichten";

    protected override Task OnInitializedAsync() => LaadAsync();

    protected override Task OnClubChangedAsync() => LaadAsync();

    private async Task LaadAsync()
    {
        _bezig = true;
        _fout = null;
        StateHasChanged();

        try
        {
            var r = await Api.GetEmailLogAsync(
                vanaf: EmailLogFilter.Vanaf(_periode, DateTime.Today),
                status: EmailLogFilter.StatusParameter(_status),
                limit: EmailLogFilter.MaxRegels);
            if (r.Success) _log = r.Data;
            else
            {
                _log = null;
                _fout = r.ErrorMessage ?? "Ophalen mislukt";
            }
        }
        catch (Exception ex)
        {
            _log = null;
            _fout = ex.Message;
        }
        finally
        {
            _bezig = false;
        }
    }
}
