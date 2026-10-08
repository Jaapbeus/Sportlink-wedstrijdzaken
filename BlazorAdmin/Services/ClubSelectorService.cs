using Microsoft.JSInterop;
using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Bewaart de geselecteerde club-code en club-naam van de beheerder.
/// Slaat de keuze op in localStorage zodat hij na een pagina-refresh bewaard blijft.
/// </summary>
public class ClubSelectorService
{
    private readonly IJSRuntime _js;
    private string? _clubCode;
    private string? _clubName;
    private const string StorageKey = "selectedClubCode";

    /// <summary>Gaat uitsluitend af als de <b>clubcode</b> verandert (#1578). Pagina's herladen hier hun
    /// gegevens op; een naamsynchronisatie of een wijziging van de menuvlag mag dat nooit veroorzaken.</summary>
    public event Action? OnChange;

    /// <summary>Gaat af als alleen de weergavenaam van de al gekozen club wijzigt (#1578). Bedoeld voor
    /// onderdelen die de naam tonen (menu, bovenbalk); pagina's hoeven hier niet op te herladen.</summary>
    public event Action? OnClubNameChange;

    /// <summary>Gaat af als de menuvlag <see cref="SportlinkExtensionEnabled"/> wijzigt (#1578).
    /// Aparte gebeurtenis zodat pagina's die de vlag niet gebruiken er niet op herladen.</summary>
    public event Action? OnSportlinkExtensionChange;

    public ClubSelectorService(IJSRuntime js)
    {
        _js = js;
    }

    public string? SelectedClubCode => _clubCode;
    public string? SelectedClubName => _clubName;

    /// <summary>#1122: staat de Sportlink Web Extension aan voor de geselecteerde club? Gevuld door
    /// NavMenu (bij laden en clubwissel) en bijgewerkt door de extensie-instellingenpagina na
    /// opslaan, zodat de menu-items Wijzigingsverzoeken/Oefenwedstrijd direct meebewegen zonder
    /// dat elke pagina zelf de instellingen ophaalt.</summary>
    public bool SportlinkExtensionEnabled { get; private set; }

    public void ZetSportlinkExtensionEnabled(bool enabled)
    {
        if (SportlinkExtensionEnabled == enabled) return;
        SportlinkExtensionEnabled = enabled;
        OnSportlinkExtensionChange?.Invoke();
    }

    /// <summary>Standaardtijd die het lezen van localStorage mag duren voordat we doorgaan zonder opgeslagen club.</summary>
    public static readonly TimeSpan StandaardOpslagTimeout = TimeSpan.FromSeconds(5);

    public async Task InitializeAsync(TimeSpan? timeout = null)
    {
        try
        {
            // Een nooit voltooiende interop mag de layout niet eeuwig vasthouden (#1578).
            using var cts = new CancellationTokenSource(timeout ?? StandaardOpslagTimeout);
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", cts.Token, new object?[] { StorageKey });
            if (!string.IsNullOrWhiteSpace(stored) && !string.Equals(_clubCode, stored, StringComparison.Ordinal))
            {
                _clubCode = stored;
                // Een pagina die al met een andere (lege) club is geladen moet dit nog oppikken.
                OnChange?.Invoke();
            }
        }
        catch
        {
            // localStorage niet beschikbaar (pre-render) — stilzwijgend negeren
        }
    }

    /// <summary>
    /// Selecteert een club. ClubName wordt opgeslagen zodat NavMenu altijd de juiste naam toont.
    /// <see cref="OnChange"/> gaat alleen af als de clubcode echt verandert (#1578); bij dezelfde club
    /// wordt hooguit de naam stil bijgewerkt via <see cref="SynchroniseerClubNaam"/>.
    /// </summary>
    public async Task SelectClubAsync(string clubCode, string? clubName = null)
    {
        if (string.Equals(_clubCode, clubCode, StringComparison.Ordinal))
        {
            SynchroniseerClubNaam(clubName);
            return;
        }

        _clubCode = clubCode;
        _clubName = clubName;
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, clubCode);
        }
        catch { }
        OnChange?.Invoke();
    }

    /// <summary>
    /// Werkt de weergavenaam van de al gekozen club bij zonder <see cref="OnChange"/> te vuren (#1578).
    /// Een lege naam wordt genegeerd; een ongewijzigde naam doet niets.
    /// </summary>
    public void SynchroniseerClubNaam(string? clubName)
    {
        if (string.IsNullOrWhiteSpace(clubName) || string.Equals(_clubName, clubName, StringComparison.Ordinal)) return;
        _clubName = clubName;
        OnClubNameChange?.Invoke();
    }
}
