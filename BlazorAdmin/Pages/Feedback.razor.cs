using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace BlazorAdmin.Pages;

/// <summary>
/// Feedbackoverzicht voor beheerders (#764, #1478): alle meldingen met melder, status en
/// issue-verwijzing, een publicatieknop voor meldingen van gewone gebruikers, en het inzagelog.
/// Het openen van de lijst of een melding wordt door de server vastgelegd (inzagelog) — de pagina
/// zelf bewaart niets. Geen export: dat zou een kopie buiten bewaartermijn en inzagelog om maken.
/// </summary>
public partial class Feedback : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private enum Tab { Meldingen, Inzagelog }

    private Tab _tab = Tab.Meldingen;
    private readonly FeedbackFilterDto _filter = new();
    private DateOnly? _vanaf;
    private DateOnly? _tot;
    private FeedbackLijstDto? _lijst;
    private FeedbackInzagelogDto? _inzage;
    private int _inzageOffset;
    private bool _bezig;
    private string? _fout;
    private string? _melding;

    private Guid? _open;
    private FeedbackDetailDto? _detail;
    private bool _detailBezig;
    private readonly HashSet<Guid> _publicerenBezig = [];

    protected override Task OnInitializedAsync() => LaadAsync();

    protected override Task OnClubChangedAsync()
    {
        _open = null;
        _detail = null;
        _filter.Offset = 0;
        _inzageOffset = 0;
        return LaadAsync();
    }

    private Task LaadAsync() => _tab == Tab.Meldingen ? LaadLijstAsync() : LaadInzageAsync();

    private async Task KiesTabAsync(Tab tab)
    {
        _tab = tab;
        _fout = _melding = null;
        await LaadAsync();
    }

    private async Task LaadLijstAsync()
    {
        _bezig = true;
        _fout = null;
        _filter.Vanaf = _vanaf?.ToString("yyyy-MM-dd");
        _filter.Tot = _tot?.ToString("yyyy-MM-dd");
        var result = await Api.GetFeedbackLijstAsync(_filter);
        _bezig = false;

        if (result.Success) _lijst = result.Data;
        else _fout = result.ErrorMessage ?? "Ophalen mislukt";
    }

    private async Task LaadInzageAsync()
    {
        _bezig = true;
        _fout = null;
        var result = await Api.GetFeedbackInzagelogAsync(50, _inzageOffset);
        _bezig = false;

        if (result.Success) _inzage = result.Data;
        else _fout = result.ErrorMessage ?? "Ophalen mislukt";
    }

    private Task ZoekAsync()
    {
        _filter.Offset = 0;
        _open = null;
        return LaadLijstAsync();
    }

    private async Task OnZoekToets(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await ZoekAsync();
    }

    private Task WisFilterAsync()
    {
        _filter.Type = _filter.Status = _filter.Zoek = null;
        _vanaf = _tot = null;
        return ZoekAsync();
    }

    private Task VorigeAsync()
    {
        _filter.Offset = Math.Max(0, _filter.Offset - _filter.Limit);
        return LaadLijstAsync();
    }

    private Task VolgendeAsync()
    {
        _filter.Offset += _filter.Limit;
        return LaadLijstAsync();
    }

    private Task VorigeInzageAsync()
    {
        _inzageOffset = Math.Max(0, _inzageOffset - 50);
        return LaadInzageAsync();
    }

    private Task VolgendeInzageAsync()
    {
        _inzageOffset += 50;
        return LaadInzageAsync();
    }

    /// <summary>Detail wordt lui geladen bij uitklappen, zodat de lijstquery geen lange tekst en telemetrie meesleept.</summary>
    private async Task ToggleDetailAsync(Guid id)
    {
        if (_open == id)
        {
            _open = null;
            _detail = null;
            return;
        }

        _open = id;
        _detail = null;
        _detailBezig = true;
        var result = await Api.GetFeedbackDetailAsync(id);
        _detailBezig = false;

        if (result.Success) _detail = result.Data;
        else
        {
            _open = null;
            _fout = result.ErrorMessage ?? "Melding ophalen mislukt";
        }
    }

    private async Task PubliceerAsync(Guid id)
    {
        _publicerenBezig.Add(id);
        _fout = _melding = null;
        var result = await Api.PubliceerFeedbackAsync(id);
        _publicerenBezig.Remove(id);

        if (result.Success)
        {
            _melding = $"Melding gepubliceerd als issue #{result.Data!.IssueNummer}.";
            _open = null;
            _detail = null;
            await LaadLijstAsync();
        }
        else
        {
            _fout = result.ErrorMessage ?? "Publiceren mislukt";
        }
    }
}
