using BlazorAdmin.Models;
using Microsoft.JSInterop;

namespace BlazorAdmin.Services;

/// <summary>
/// Uitklap- en deeplink-state voor de Sportlink-kolom in een wedstrijdenlijst (#989/#991, verplaatst
/// bij #1361 zodat zowel Planning als Veld optimalisatie dezelfde state-logica gebruiken in plaats van
/// elk hun eigen kopie). Eén instantie per pagina — geen DI-service, gewoon een <c>new()</c>-veld in de
/// code-behind. Het paneel zelf per wedstrijd is <see cref="Shared.SportlinkMatchPanel"/>.
/// </summary>
public sealed class SportlinkActieKolomState
{
    private readonly HashSet<long> _uitgeklapt = new();
    private readonly Dictionary<long, SportlinkActieStatus> _deeplinks = new();

    public bool IsUitgeklapt(long wedstrijdCode) => _uitgeklapt.Contains(wedstrijdCode);

    public void Toggle(long wedstrijdCode)
    {
        if (!_uitgeklapt.Remove(wedstrijdCode)) _uitgeklapt.Add(wedstrijdCode);
    }

    public SportlinkActieStatus Deeplink(long wedstrijdCode)
        => _deeplinks.TryGetValue(wedstrijdCode, out var s) ? s : _deeplinks[wedstrijdCode] = new SportlinkActieStatus();

    // #989: haalt alleen het PublicMatchId op (lichtgewicht endpoint) en opent meteen een nieuw tabblad.
    public async Task OpenInSportlinkAsync(long wedstrijdCode, AdminApiClient api, IJSRuntime js)
    {
        var status = Deeplink(wedstrijdCode);
        status.Start();
        try
        {
            var result = await api.GetSportlinkPublicMatchIdAsync(wedstrijdCode.ToString());
            if (!result.Success || string.IsNullOrWhiteSpace(result.Data?.PublicMatchId))
            {
                status.Fout(result.ErrorMessage ?? "PublicMatchId niet gevonden.");
                return;
            }
            var url = $"https://club.sportlink.com/competition-affairs/match-details/{Uri.EscapeDataString(result.Data.PublicMatchId)}";
            await js.InvokeVoidAsync("blazorHelpers.openInNewTab", url);
        }
        finally { status.Klaar(); }
    }
}
