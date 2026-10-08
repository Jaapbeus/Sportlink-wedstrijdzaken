using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Laadt het e-maillog voor de pagina E-maillog en bewaakt dat alleen de NIEUWSTE aanvraag resultaat, fout en
/// laadstatus mag publiceren (#1583, Codex-review). Zonder deze bewaking kan een trage aanvraag A na een snellere
/// aanvraag B (andere filterkeuze of andere club) alsnog de lijst overschrijven: de keuzelijst toont dan B, de lijst
/// bevat A. Gebruikt <see cref="LookupGeneratieGuard"/>: elke aanvraag krijgt een token, een verouderd token wordt
/// genegeerd — ook voor <see cref="Bezig"/> en <see cref="Fout"/>.
/// </summary>
public sealed class EmailLogLader
{
    private readonly Func<DateTime?, string?, int, Task<ApiResult<EmailLogResponse>>> _haal;
    private readonly LookupGeneratieGuard _guard = new();

    /// <param name="haal">De API-aanroep: (vanaf, status, limiet).</param>
    public EmailLogLader(Func<DateTime?, string?, int, Task<ApiResult<EmailLogResponse>>> haal) => _haal = haal;

    public EmailLogResponse? Log { get; private set; }
    public string? Fout { get; private set; }
    public bool Bezig { get; private set; }

    /// <summary>
    /// Start een aanvraag voor de gekozen filters. <paramref name="wisHuidige"/> leegt eerst de getoonde lijst
    /// (clubwissel: de lijst van de vorige club mag niet blijven staan terwijl de nieuwe laadt).
    /// </summary>
    public async Task LaadAsync(string? status, string? periode, DateTime vandaag, bool wisHuidige = false)
    {
        var token = _guard.Start($"{status}|{periode}");
        Bezig = true;
        Fout = null;
        if (wisHuidige) Log = null;

        ApiResult<EmailLogResponse>? resultaat = null;
        string? uitzondering = null;
        try
        {
            resultaat = await _haal(
                EmailLogFilter.Vanaf(periode, vandaag), EmailLogFilter.StatusParameter(status), EmailLogFilter.MaxRegels);
        }
        catch (Exception ex)
        {
            uitzondering = ex.Message;
        }

        // Een nieuwere aanvraag is intussen gestart: dit resultaat is verouderd en raakt niets meer aan,
        // ook niet de laadstatus (die is van de nieuwere aanvraag).
        if (!_guard.IsActueel(token)) return;

        if (uitzondering is not null)
        {
            Log = null;
            Fout = uitzondering;
        }
        else if (resultaat!.Success)
        {
            Log = resultaat.Data;
        }
        else
        {
            Log = null;
            Fout = resultaat.ErrorMessage ?? "Ophalen mislukt";
        }
        Bezig = false;
    }
}
