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
    [Inject] private FeedbackApiClient Api { get; set; } = default!;

    private enum Tab { Meldingen, Inzagelog }

    // #1872: aparte guards per clubgebonden lookup; een late response van de vorige club wordt genegeerd.
    private readonly LookupGeneratieGuard _lijstGuard = new();
    private readonly LookupGeneratieGuard _inzageGuard = new();
    private readonly LookupGeneratieGuard _detailGuard = new();

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
        _detailBezig = false;
        _filter.Offset = 0;
        _inzageOffset = 0;
        _melding = null; // #1872: melding hoort bij de vorige club
        // #1872: lijst, inzagelog en detail delen _fout/_bezig. Ook een laadronde van een inactieve tab die vóór
        // een heen-en-terug-clubwissel begon, mag daarna geen resultaat, fout of laadstatus meer toepassen.
        var clubCode = ClubSelector.SelectedClubCode;
        _lijstGuard.Start(clubCode);
        _inzageGuard.Start(clubCode);
        _detailGuard.Start(clubCode);
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
        var toegepast = await LaadClubgebondenAsync(_lijstGuard, () => Api.GetFeedbackLijstAsync(_filter), result =>
        {
            if (result.Success) _lijst = result.Data;
            else _fout = result.ErrorMessage ?? "Ophalen mislukt";
        });
        if (toegepast) _bezig = false;
    }

    private async Task LaadInzageAsync()
    {
        _bezig = true;
        _fout = null;
        var offset = _inzageOffset;
        var toegepast = await LaadClubgebondenAsync(_inzageGuard, () => Api.GetFeedbackInzagelogAsync(50, offset), result =>
        {
            if (result.Success) _inzage = result.Data;
            else _fout = result.ErrorMessage ?? "Ophalen mislukt";
        });
        if (toegepast) _bezig = false;
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
        var toegepast = await LaadClubgebondenAsync(_detailGuard, () => Api.GetFeedbackDetailAsync(id), result =>
        {
            if (result.Success) _detail = result.Data;
            else
            {
                _open = null;
                _fout = result.ErrorMessage ?? "Melding ophalen mislukt";
            }
        });
        if (toegepast) _detailBezig = false;
    }

    private async Task PubliceerAsync(Guid id)
    {
        _publicerenBezig.Add(id);
        _fout = _melding = null;
        var clubCode = ClubSelector.SelectedClubCode;
        var result = await Api.PubliceerFeedbackAsync(id);
        _publicerenBezig.Remove(id);
        if (IsClubGewisseld(clubCode)) return; // #1872: uitkomst hoort bij een andere club

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
