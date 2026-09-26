using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>
/// Gedeelde clubwissel-lifecycle voor admin-pagina's (#1328): abonneren op
/// <see cref="ClubSelectorService.OnChange"/> in <see cref="OnInitialized"/>, de callback op de
/// UI-thread laten lopen via <c>InvokeAsync</c>, afronden met <c>StateHasChanged</c>, en het
/// abonnement weer opzeggen bij <see cref="Dispose"/>. Twaalf pagina's implementeerden dit
/// woordelijk identiek; alleen wát er bij een clubwissel herladen moet worden verschilt per
/// pagina — dat hoort in <see cref="OnClubChangedAsync"/>.
/// </summary>
/// <remarks>
/// Een pagina die overerft van deze basis voegt <c>@inherits ClubSelectorPageBase</c> toe aan de
/// <c>.razor</c>-markup (en verwijdert daar de eigen <c>@inject ClubSelectorService</c> en
/// <c>@implements IDisposable</c>, want die komen nu via de basis mee). Heeft een pagina naast de
/// clubwissel-reload nog eigen opruimwerk nodig (bijv. een <c>CancellationTokenSource</c>), dan
/// overschrijft ze <see cref="Dispose(bool)"/> en roept <c>base.Dispose(disposing)</c> aan.
/// </remarks>
public abstract class ClubSelectorPageBase : ComponentBase, IDisposable
{
    [Inject] protected ClubSelectorService ClubSelector { get; set; } = default!;

    private bool _disposed;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        ClubSelector.OnChange += HandleClubChanged;
    }

    private void HandleClubChanged() => _ = InvokeAsync(async () =>
    {
        await OnClubChangedAsync();
        StateHasChanged();
    });

    /// <summary>
    /// Pagina-specifieke reload bij clubwissel. Standaard geen actie (gebruikt door pagina's die
    /// bij een clubwissel alleen hoeven te hertekenen, zoals <c>TestData/Wedstrijden</c>).
    /// </summary>
    protected virtual Task OnClubChangedAsync() => Task.CompletedTask;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Overschrijf dit voor pagina-specifiek opruimwerk en roep altijd <c>base.Dispose(disposing)</c> aan.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) ClubSelector.OnChange -= HandleClubChanged;
        _disposed = true;
    }
}
