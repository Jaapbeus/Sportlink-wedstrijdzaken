using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorAdmin.Shared;

/// <summary>Generiek deelpaneel (#1362, epic #1365): sandboxed iframe-preview zonder script, HTML
/// kopiëren/downloaden en — alleen als <see cref="PdfOphalen"/> is gezet — PDF downloaden. De
/// databron zit achter delegates zodat pagina's met HTML in het geheugen en pagina's die per klik
/// ophalen hetzelfde contract gebruiken.</summary>
public partial class DeelPaneel : ComponentBase
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public string Titel { get; set; } = "";
    /// <summary>Optionele uitleg boven de knoppen.</summary>
    [Parameter] public string? Toelichting { get; set; }
    /// <summary>Basis van de bestandsnaam zonder extensie, bijv. <c>veld-optimalisatie-2026-10-03</c>.</summary>
    [Parameter] public string BestandsNaam { get; set; } = "planning";
    /// <summary>Geeft de volledige (server-gegenereerde) HTML; scripts worden hier gestript voor preview/kopie.</summary>
    [Parameter] public Func<Task<string>>? HtmlOphalen { get; set; }
    /// <summary>Geeft PDF-bytes. Null = geen PDF-knop.</summary>
    [Parameter] public Func<Task<byte[]>>? PdfOphalen { get; set; }
    /// <summary>Verandert deze waarde terwijl het paneel open staat, dan wordt de preview opnieuw opgehaald.</summary>
    [Parameter] public string? VerversSleutel { get; set; }

    private readonly SportlinkActieStatus _status = new();
    private bool _open;
    private string? _previewHtml;
    private string? _kopieerStatus;
    private string? _laatsteSleutel;

    protected override async Task OnParametersSetAsync()
    {
        if (_open && VerversSleutel != _laatsteSleutel)
            await LaadPreviewAsync();
    }

    private async Task WisselOpenAsync()
    {
        _open = !_open;
        if (_open) await LaadPreviewAsync();
    }

    private async Task LaadPreviewAsync()
    {
        _laatsteSleutel = VerversSleutel;
        var html = await HaalHtmlAsync();
        _previewHtml = DeelHtmlHelper.ZonderScript(html);
    }

    private async Task<string?> HaalHtmlAsync()
    {
        if (HtmlOphalen == null) return null;
        _status.Start();
        try
        {
            var html = await HtmlOphalen();
            _status.Wis();
            return html;
        }
        catch (Exception ex)
        {
            _status.Fout($"Ophalen mislukt: {ex.Message}");
            return null;
        }
        finally { _status.Klaar(); }
    }

    private async Task KopieerAsync()
    {
        var html = DeelHtmlHelper.ZonderScript(await HaalHtmlAsync());
        if (string.IsNullOrEmpty(html)) return;
        await JS.InvokeVoidAsync("blazorHelpers.copyToClipboard", html);
        _kopieerStatus = "Gekopieerd!";
        _ = Task.Delay(2500).ContinueWith(_ => { _kopieerStatus = null; InvokeAsync(StateHasChanged); });
    }

    private async Task DownloadHtmlAsync()
    {
        var html = await HaalHtmlAsync();
        if (string.IsNullOrEmpty(html)) return;
        await JS.InvokeVoidAsync("blazorHelpers.downloadHtml", $"{BestandsNaam}.html", html);
    }

    private async Task DownloadPdfAsync()
    {
        if (PdfOphalen == null) return;
        _status.Start();
        try
        {
            var bytes = await PdfOphalen();
            _status.Wis();
            if (bytes.Length == 0) return;
            await JS.InvokeVoidAsync("blazorHelpers.downloadBytes", $"{BestandsNaam}.pdf",
                Convert.ToBase64String(bytes), "application/pdf");
        }
        catch (Exception ex) { _status.Fout($"PDF ophalen mislukt: {ex.Message}"); }
        finally { _status.Klaar(); }
    }
}
