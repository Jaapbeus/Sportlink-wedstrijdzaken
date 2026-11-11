using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>
/// Centraliseert de clubwissel-lifecycle voor admin-pagina's: abonneert op
/// <see cref="ClubSelectorService.OnChange"/> bij initialisatie, voert de callback op de UI-thread
/// uit via <c>InvokeAsync</c>, ververst de pagina met <c>StateHasChanged</c> en zegt het abonnement
/// op bij <see cref="Dispose"/>. Pagina-specifieke herlaadactie staat in
/// <see cref="OnClubChangedAsync"/>.
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

    private readonly ClubWisselTracker _clubTracker = new();
    private bool _disposed;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        _clubTracker.Markeer(ClubSelector.SelectedClubCode);
        ClubSelector.OnChange += HandleClubChanged;
    }

    // #1578: alleen herladen als de club werkelijk anders is dan die waarvoor de pagina is geladen.
    private void HandleClubChanged() => _ = InvokeAsync(async () =>
    {
        if (!_clubTracker.MoetHerladen(ClubSelector.SelectedClubCode)) return;
        await OnClubChangedAsync();
        StateHasChanged();
    });

    /// <summary>
    /// Pagina-specifieke reload bij clubwissel. Standaard geen actie (gebruikt door pagina's die
    /// bij een clubwissel alleen hoeven te hertekenen, zoals <c>TestData/Wedstrijden</c>).
    /// </summary>
    protected virtual Task OnClubChangedAsync() => Task.CompletedTask;

    protected async Task LaadClubgebondenBooleanAsync(
        LookupGeneratieGuard guard,
        Func<Task<bool>> ophalenAsync,
        Action<bool> toepassen)
    {
        var clubCode = ClubSelector.SelectedClubCode;
        var generatie = guard.Start(clubCode);
        var waarde = await ophalenAsync();
        if (guard.IsActueel(generatie) && ClubSelector.SelectedClubCode == clubCode)
            toepassen(waarde);
    }

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
