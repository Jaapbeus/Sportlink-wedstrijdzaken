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

    protected Task LaadClubgebondenBooleanAsync(
        LookupGeneratieGuard guard,
        Func<Task<bool>> ophalenAsync,
        Action<bool> toepassen) => LaadClubgebondenAsync(guard, ophalenAsync, toepassen);

    /// <summary>
    /// Generieke, bewaakte clubgebonden load (#1802/#1872): legt de club vast bij de start, en past het
    /// resultaat alleen toe als er sindsdien geen nieuwere load met dezelfde <paramref name="guard"/> is
    /// gestart én de gekozen club nog dezelfde is. Een te late response (of fout) van een vorige club
    /// wordt genegeerd. Geeft <c>true</c> terug als het resultaat is toegepast; <c>false</c> betekent
    /// dat de load verouderd is (de nieuwere load beheert dan zelf de laad-/foutstatus). Een fout van
    /// een nog actuele load wordt doorgegeven aan de aanroeper.
    /// </summary>
    protected async Task<bool> LaadClubgebondenAsync<T>(
        LookupGeneratieGuard guard,
        Func<Task<T>> ophalenAsync,
        Action<T> toepassen)
    {
        var clubCode = ClubSelector.SelectedClubCode;
        var generatie = guard.Start(clubCode);
        T waarde;
        try
        {
            waarde = await ophalenAsync();
        }
        catch when (!IsActueleClubLoad(guard, generatie, clubCode))
        {
            return false;
        }

        if (!IsActueleClubLoad(guard, generatie, clubCode)) return false;
        toepassen(waarde);
        return true;
    }

    /// <summary>
    /// #989/#1872: leest de Sportlink-extensievlag van de gekozen club via de instellingencache; geen
    /// Sportlink-kolom/-knoppen tonen als de extension uit staat. Bewaakt tegen late responses van een vorige club.
    /// </summary>
    protected Task LaadSportlinkExtensionVlagAsync(
        LookupGeneratieGuard guard, ClubInstellingenCache cache, Action<bool> toepassen) =>
        LaadClubgebondenAsync(guard, () => cache.GetSettingsAsync(),
            settings => toepassen(settings.Success && settings.Data?.SportlinkExtensionEnabled == true));

    /// <summary>
    /// #1872: true als de gekozen club niet meer <paramref name="clubCode"/> is (vastgelegd vóór een await in een
    /// actie). De uitkomst van zo'n actie (resultaat, melding, fout, vervolgactie) hoort dan bij een andere club en
    /// wordt niet toegepast; de clubwissel-reload laadt de stand van de nieuwe club zelf.
    /// </summary>
    protected bool IsClubGewisseld(string? clubCode) => ClubSelector.SelectedClubCode != clubCode;

    /// <summary>True zolang <paramref name="generatie"/> de nieuwste load is en de gekozen club nog <paramref name="clubCode"/> is.</summary>
    protected bool IsActueleClubLoad(LookupGeneratieGuard guard, int generatie, string? clubCode) =>
        guard.IsActueel(generatie) && !IsClubGewisseld(clubCode);

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
