using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

public partial class LeermomentFormulier
{
    /// <summary>Namen van <c>VerzoekType</c> op de server; een test bewaakt dat deze lijst bij de server aansluit.</summary>
    public static readonly IReadOnlyList<string> VerzoekTypes =
        ["BeschikbaarheidCheck", "HerplanVerzoek", "Bevestiging", "TeamContactOpvragen", "BuitenScope"];

    [Inject] private AdminApiClient Api { get; set; } = default!;

    /// <summary>Wat de pipeline ervan maakte; leeg als dat niet bekend is.</summary>
    [Parameter] public string? OrigineelType { get; set; }

    /// <summary>Voorzet voor de samenvatting (server-side gesaneerd); de beheerder redigeert hem.</summary>
    [Parameter] public string? SuggestieSamenvatting { get; set; }

    [Parameter] public int? HerkomstVerwerkingId { get; set; }
    [Parameter] public EventCallback OnOpgeslagen { get; set; }
    [Parameter] public EventCallback OnAnnuleer { get; set; }

    private readonly string _typeId = $"leer-type-{Guid.NewGuid():N}";
    private readonly string _samenvattingId = $"leer-samenvatting-{Guid.NewGuid():N}";
    private string _juistType = "";
    private string _samenvatting = "";
    private bool _bezig;
    private string? _fout;

    private bool KanOpslaan => !_bezig && _juistType.Length > 0 && !string.IsNullOrWhiteSpace(_samenvatting);

    protected override void OnInitialized() => _samenvatting = SuggestieSamenvatting ?? "";

    private async Task OpslaanAsync()
    {
        _bezig = true;
        _fout = null;
        var r = await Api.MaakLeermomentAsync(new LeermomentAanmaakDto
        {
            OrigineelVerzoekType = OrigineelType,
            JuistVerzoekType = _juistType,
            Samenvatting = _samenvatting.Trim(),
            HerkomstVerwerkingId = HerkomstVerwerkingId
        });
        _bezig = false;

        if (r.Success) await OnOpgeslagen.InvokeAsync();
        else _fout = r.ErrorMessage ?? "Opslaan mislukt";
    }

    private Task AnnuleerAsync() => OnAnnuleer.InvokeAsync();
}
