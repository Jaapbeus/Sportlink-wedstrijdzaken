using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace BlazorAdmin.Pages;

/// <summary>
/// Code-behind van <c>TeambegeleidingImport.razor</c> (#1122/regel 3). Losgekoppeld van
/// <see cref="Teambegeleiding"/> bij #1322 — CSV-import is een incidentele beheerdersactie, geen
/// dagelijks scherm, en staat daarom onder Instellingen in plaats van in het hoofdmenu.
/// </summary>
public partial class TeambegeleidingImport
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private CsvPreviewModel? _csvPreview;
    private string? _csvContent;
    private string? _bestandsnaam;
    private bool _importBezig;
    private string? _importError;
    private string? _importLeesError;
    private TeambegeldingImportResultaat? _importResultaat;

    private async Task BestandGeselecteerdAsync(InputFileChangeEventArgs e)
    {
        _csvPreview = null;
        _csvContent = null;
        _importError = null;
        _importLeesError = null;
        _importResultaat = null;

        var file = e.File;
        if (!file.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            _importLeesError = "Alleen CSV-bestanden (.csv) zijn toegestaan.";
            return;
        }

        _bestandsnaam = file.Name;

        try
        {
            using var stream = file.OpenReadStream(maxAllowedSize: 5 * 1024 * 1024);
            using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
            _csvContent = await reader.ReadToEndAsync();
        }
        catch (Exception)
        {
            _importLeesError = "Bestand kon niet worden ingelezen. Controleer of het een geldig CSV-bestand is (max. 5 MB).";
            return;
        }

        _csvPreview = MaakCsvPreview(_csvContent);
        if (_csvPreview.TotaalRijen == 0)
            _importLeesError = "CSV bevat geen gegevensrijen.";
    }

    private static CsvPreviewModel MaakCsvPreview(string csv)
    {
        var regels = csv
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.TrimEnd('\r'))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToList();

        if (regels.Count == 0)
            return new CsvPreviewModel([], [], 0);

        var headers = regels[0].Split(';').Select(h => h.Trim('"').Trim()).ToList();
        var voorbeeldRijen = regels.Skip(1).Take(5)
            .Select(r => r.Split(';').Select(v => v.Trim('"').Trim()).ToList())
            .ToList();

        return new CsvPreviewModel(headers, voorbeeldRijen, regels.Count - 1);
    }

    private async Task ImporteerAsync()
    {
        if (_csvContent == null) return;

        _importBezig = true;
        _importError = null;

        var result = await Api.ImporteerTeambegeleidingAsync(_csvContent, _bestandsnaam);
        _importBezig = false;

        if (result.Success)
        {
            _importResultaat = result.Data;
            _csvPreview = null;
            _csvContent = null;
        }
        else
        {
            _importError = result.ErrorMessage ?? "Import mislukt. Controleer het CSV-bestand en probeer opnieuw.";
        }
    }

    private void ResetImport()
    {
        _csvPreview = null;
        _csvContent = null;
        _importError = null;
        _importLeesError = null;
        _importResultaat = null;
    }

    private record CsvPreviewModel(
        List<string> Headers,
        List<List<string>> VoorbeeldRijen,
        int TotaalRijen);
}
