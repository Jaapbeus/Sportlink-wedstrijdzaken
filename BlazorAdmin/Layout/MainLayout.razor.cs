using System.Reflection;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Layout;

public partial class MainLayout
{
    private List<ClubDto> _clubs = new();
    private bool _backendBezig = true;
    private readonly PaginaStartPoort _poort = new();
    private CancellationTokenSource _cts = new();

    private bool IsTestmodus =>
        string.Equals(ClubSelector.SelectedClubCode, "ALLSTARS", StringComparison.OrdinalIgnoreCase);

    protected override async Task OnInitializedAsync()
    {
        ClubSelector.OnChange += OnClubSelectorChanged;
        ClubSelector.OnClubNameChange += OnClubSelectorChanged;
        await ClubSelector.InitializeAsync(); // eigen time-out: komt altijd terug
        // Body pas renderen als de effectieve club vaststaat (#1578): met een opgeslagen club meteen,
        // zonder opgeslagen club pas als de clublijst is opgehaald en de primaire club gekozen is
        // (of het ophalen definitief faalde). Anders laadt elke pagina eerst zonder club en daarna
        // nog eens mét club.
        _poort.MarkeerOpslagGelezen(ClubSelector.SelectedClubCode);
        StateHasChanged();
        try
        {
            await LaadClubsAsync(_cts.Token);
        }
        finally
        {
            // Ook bij fout/time-out/lege lijst: pagina's tonen dan hun eigen foutafhandeling.
            _poort.MarkeerClublijstAfgerond(ClubSelector.SelectedClubCode);
        }
    }

    private void OnClubSelectorChanged() => _ = InvokeAsync(StateHasChanged);

    private async Task LaadClubsAsync(CancellationToken ct)
    {
        // Max 12 pogingen × 3 s = 36 s wachttijd bij koude start
        for (var poging = 0; poging < 12 && !ct.IsCancellationRequested; poging++)
        {
            var result = await Api.GetClubsAsync();
            if (result.Success && result.Data?.Count > 0)
            {
                _clubs = result.Data;
                if (ClubSelector.SelectedClubCode == null ||
                    !_clubs.Any(c => c.ClubCode == ClubSelector.SelectedClubCode))
                {
                    var primary = _clubs.FirstOrDefault(c => c.SyncEnabled) ?? _clubs[0];
                    await ClubSelector.SelectClubAsync(primary.ClubCode, primary.ClubName);
                }
                else
                {
                    // Naam synchroniseren voor de al-geselecteerde club (bijv. na pagina-refresh).
                    // Stil: dit mag de pagina's niet laten herladen (#1578).
                    var naam = _clubs.FirstOrDefault(c => c.ClubCode == ClubSelector.SelectedClubCode)?.ClubName;
                    ClubSelector.SynchroniseerClubNaam(naam);
                }
                _backendBezig = false;
                return;
            }

            if (poging < 11)
            {
                StateHasChanged(); // toon spinner na eerste mislukte poging
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
            }
        }
        _backendBezig = false; // stop spinner na time-out
    }

    private async Task OnClubChanged(ChangeEventArgs e)
    {
        var code = e.Value?.ToString();
        if (!string.IsNullOrWhiteSpace(code))
        {
            var naam = _clubs.FirstOrDefault(c => c.ClubCode == code)?.ClubName;
            await ClubSelector.SelectClubAsync(code, naam);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        ClubSelector.OnChange -= OnClubSelectorChanged;
        ClubSelector.OnClubNameChange -= OnClubSelectorChanged;
    }
}
