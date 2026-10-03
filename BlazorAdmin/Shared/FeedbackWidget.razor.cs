using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Planner.Shared.Feedback;

namespace BlazorAdmin.Shared;

/// <summary>
/// De feedbackknop en het bijbehorende dialoogvenster. Open voor elke ingelogde rol (#764): een
/// beheerder doorloopt controleren → overzicht → voorbeeld → publiceren; een gewone gebruiker
/// controleren → overzicht → versturen (de melding wacht daarna op een beheerder, die hem publiceert).
/// </summary>
public partial class FeedbackWidget
{
    [Inject] private AdminApiClient Api { get; set; } = default!;
    [Inject] private IAuthService Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private ClientTelemetryService Telemetrie { get; set; } = default!;

    private bool _isOpen;
    private bool _bezig;
    private string? _foutmelding;
    private Stap _stap = Stap.Formulier;

    private string _type = "Fout";
    private string _beschrijving = "";
    private List<string> _vragen = [];
    private List<string> _antwoorden = [];
    private FeedbackContext? _context;

    // Technische context (#764): standaard aan, zichtbaar vóór verzending, per melding uit te zetten.
    private bool _metTelemetrie = true;
    private bool _toonContext;
    private FeedbackTelemetrie _telemetrie = new();

    private FeedbackSubmitResponse? _antwoord;

    // Het voorbeeld dat de beheerder te zien krijgt vóór publicatie (#1205). De AI-velden worden bij
    // het publiceren onveranderd teruggestuurd, zodat er exact gepubliceerd wordt wat hier stond —
    // een tweede AI-aanroep op de server zou andere tekst opleveren.
    private string _voorbeeldTitel = "";
    private string _voorbeeldBody = "";
    private FeedbackBevestiging? _bevestiging;

    private enum Stap { Formulier, Overzicht, Voorbeeld, Bevestiging }

    private bool IsAdmin => Auth.IsAdmin;

    /// <summary>Letterlijk wat er wordt meegestuurd — dezelfde bron als de server bewaart.</summary>
    private string ContextTekst => _telemetrie.IsLeeg
        ? "Er zijn geen technische gegevens beschikbaar."
        : _telemetrie.NaarTekst();

    private async Task OpenAsync()
    {
        _isOpen = true;
        _stap = Stap.Formulier;
        _beschrijving = "";
        _type = "Fout";
        _vragen = [];
        _antwoorden = [];
        _foutmelding = null;
        _voorbeeldTitel = "";
        _voorbeeldBody = "";
        _bevestiging = null;
        _antwoord = null;
        _metTelemetrie = true;
        _toonContext = false;

        // Geen JS eval() gebruiken: de productie-CSP staat alleen 'wasm-unsafe-eval' toe. Pathname komt
        // uit NavigationManager; user-agent en console-fouten via dedicated interop-helpers. (#597)
        var pagina = "/" + Nav.ToBaseRelativePath(Nav.Uri).Split('?')[0].Split('#')[0];
        var versie = typeof(FeedbackWidget).Assembly.GetName().Version?.ToString(4) ?? "?";

        _telemetrie = await Telemetrie.VerzamelAsync();
        _context = new FeedbackContext
        {
            Pagina = pagina,
            Versie = versie,
            Rol = IsAdmin ? "admin" : "gebruiker",
            Browser = _telemetrie.Browser ?? ""
        };
    }

    private void Sluit()
    {
        _isOpen = false;
        _foutmelding = null;
    }

    private void KiesType(string waarde)
    {
        _type = waarde;
        _vragen.Clear();
        _antwoorden.Clear();
    }

    private void ZetAntwoord(int index, string waarde)
    {
        while (_antwoorden.Count <= index)
            _antwoorden.Add("");
        _antwoorden[index] = waarde;
    }

    private async Task ControlerenAsync()
    {
        if (string.IsNullOrWhiteSpace(_beschrijving) || _beschrijving.Trim().Length < 10)
        {
            _foutmelding = "Vul een beschrijving in van minimaal 10 tekens.";
            return;
        }

        _bezig = true;
        _foutmelding = null;

        var result = await Api.ValidateFeedbackAsync(BouwRequest());

        _bezig = false;

        if (!result.Success)
        {
            _foutmelding = FoutTekst(result.StatusCode, result.ErrorMessage, "Controleren mislukt. Probeer het opnieuw.");
            return;
        }

        if (result.Data!.Volledig)
        {
            // Opnieuw verzamelen: een mislukte aanroep van zojuist hoort erbij.
            _telemetrie = await Telemetrie.VerzamelAsync();
            _stap = Stap.Overzicht;
            return;
        }

        var nieuweVragen = result.Data.Vragen ?? [];
        // Bewaar antwoorden op vragen die ook in de nieuwe set voorkomen.
        var nieuweAntwoorden = nieuweVragen
            .Select(v =>
            {
                var idx = _vragen.IndexOf(v);
                return idx >= 0 && idx < _antwoorden.Count ? _antwoorden[idx] : "";
            })
            .ToList();
        _vragen = nieuweVragen;
        _antwoorden = nieuweAntwoorden;
    }

    /// <summary>
    /// Beheerder: haalt het voorbeeld op en publiceert nog niets (#1205). Gewone gebruiker: bewaart de
    /// melding direct; die publiceert nooit zelf, een beheerder doet dat in het overzicht.
    /// </summary>
    private async Task VersturenAsync()
    {
        _bezig = true;
        _foutmelding = null;

        if (!IsAdmin)
        {
            await SlaOpAsync(BouwRequest());
            return;
        }

        var result = await Api.PreviewFeedbackAsync(BouwRequest());
        _bezig = false;

        if (!result.Success)
        {
            _foutmelding = FoutTekst(result.StatusCode, result.ErrorMessage, "Voorbeeld ophalen mislukt. Probeer het opnieuw.");
            return;
        }

        _voorbeeldTitel = result.Data!.Titel;
        _voorbeeldBody = result.Data.Body;
        _bevestiging = new FeedbackBevestiging
        {
            Titel = result.Data.Titel,
            Samenvatting = result.Data.Samenvatting,
            Acceptatiecriteria = result.Data.Acceptatiecriteria
        };
        _stap = Stap.Voorbeeld;
    }

    private void TerugNaarFormulier()
    {
        // De ingevulde tekst blijft staan: _beschrijving, _vragen en _antwoorden worden niet gewist.
        _stap = Stap.Formulier;
        _foutmelding = null;
        _bevestiging = null;
        _voorbeeldTitel = "";
        _voorbeeldBody = "";
    }

    /// <summary>
    /// De bewuste publicatiestap van een beheerder: stuurt de in het voorbeeld getoonde velden terug,
    /// zodat exact díe tekst openbaar wordt (#1205). De server past de PII-gate onverkort opnieuw toe.
    /// </summary>
    private async Task PublicerenAsync()
    {
        _bezig = true;
        _foutmelding = null;

        var request = BouwRequest();
        request.Bevestiging = _bevestiging;
        await SlaOpAsync(request);
    }

    private async Task SlaOpAsync(FeedbackValidateRequest request)
    {
        var result = await Api.SubmitFeedbackAsync(request);
        _bezig = false;

        if (!result.Success)
        {
            _foutmelding = FoutTekst(result.StatusCode, result.ErrorMessage, "Versturen mislukt. Probeer het opnieuw.");
            return;
        }

        _antwoord = result.Data;
        _stap = Stap.Bevestiging;
    }

    private FeedbackValidateRequest BouwRequest()
    {
        var qa = _vragen
            .Select((v, i) => new FeedbackVraagAntwoord
            {
                Vraag = v,
                Antwoord = _antwoorden.Count > i ? _antwoorden[i] : ""
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Antwoord))
            .ToList();

        return new FeedbackValidateRequest
        {
            Type = _type,
            Beschrijving = _beschrijving.Trim(),
            VragenAntwoorden = qa,
            Context = _context,
            Telemetrie = _metTelemetrie && !_telemetrie.IsLeeg ? _telemetrie : null
        };
    }

    /// <summary>Foutmeldingen in mensentaal: de limiet en een tijdelijk niet beschikbare dienst krijgen een eigen tekst.</summary>
    private static string FoutTekst(int status, string? serverFout, string standaard) => status switch
    {
        429 => "Je hebt net al een paar meldingen gestuurd. Probeer het over 10 minuten nog eens.",
        503 => "Meldingen zijn tijdelijk niet mogelijk. Laat het je beheerder weten.",
        _ => serverFout ?? standaard
    };
}
