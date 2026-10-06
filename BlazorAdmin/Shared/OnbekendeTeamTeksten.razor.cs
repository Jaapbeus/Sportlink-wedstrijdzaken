using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

public partial class OnbekendeTeamTeksten
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    /// <summary>Wordt aangeroepen na het koppelen, zodat de aliaslijst op de pagina ververst kan worden.</summary>
    [Parameter] public EventCallback OnGekoppeld { get; set; }

    private static readonly (string Waarde, string Label)[] Filters =
    [
        ("open", "Open"), ("genegeerd", "Genegeerd"), ("afgehandeld", "Afgehandeld"), ("", "Alles")
    ];

    private List<OnbekendeTeamTekstDto> _items = new();
    private string _filter = "open";
    private int _open;
    private bool _bezig;
    private string? _fout;
    private string? _melding;
    private int? _koppelId;

    protected override Task OnInitializedAsync() => LaadAsync(_filter);

    private async Task LaadAsync(string status)
    {
        _filter = status;
        _bezig = true;
        _fout = null;
        _koppelId = null;
        var r = await Api.GetOnbekendeTeamTekstenAsync(status);
        if (r.Success && r.Data is not null)
        {
            _items = r.Data.Items;
            _open = r.Data.Open;
        }
        else _fout = r.ErrorMessage ?? "Ophalen mislukt";
        _bezig = false;
    }

    private void WisselKoppelen(int id) => _koppelId = _koppelId == id ? null : id;

    private void SluitKoppelen() => _koppelId = null;

    private async Task GekoppeldAsync(TeamAliasAanmaakResultaatDto resultaat)
    {
        _melding = $"'{resultaat.RuweTekst}' is gekoppeld aan {resultaat.Teamnaam}.";
        await LaadAsync(_filter);
        await OnGekoppeld.InvokeAsync();
    }

    private async Task ZetStatusAsync(int id, string status)
    {
        _melding = null;
        var r = await Api.ZetOnbekendeTeamTekstStatusAsync(id, status);
        if (r.Success) await LaadAsync(_filter);
        else _fout = r.ErrorMessage ?? "Actie mislukt";
    }

    private static string StatusKlasse(string status) => status switch
    {
        "afgehandeld" => "bg-success",
        "genegeerd" => "bg-secondary",
        _ => "bg-warning text-dark"
    };
}
