using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

public partial class TraceWeergave
{
    /// <summary>De te tonen trace; <c>null</c> of leeg toont niets.</summary>
    [Parameter] public BeslissingsTraceDto? Trace { get; set; }

    /// <summary>
    /// <c>true</c> voor een proefrun (e-mailtester: "zou worden verstuurd"); <c>false</c> voor een
    /// werkelijk verwerkt bericht uit het e-maillog (vaststelling in de verleden tijd).
    /// </summary>
    [Parameter] public bool Proefrun { get; set; }

    /// <summary>
    /// Toont de leeracties (alias aanmaken, verzoektype corrigeren). Alleen effectief voor beheerders: de
    /// onderliggende endpoints zijn admin-only. Zet dit in de e-mailtester en in de e-maillog-trace.
    /// </summary>
    [Parameter] public bool Corrigeerbaar { get; set; }

    /// <summary>Verwerking waar de trace bij hoort (e-maillog); <c>null</c> in de tester, die niets opslaat.</summary>
    [Parameter] public int? VerwerkingId { get; set; }

    /// <summary>Voorzet voor de samenvatting bij "Verzoektype corrigeren" (alleen de tester kent die, gesaneerd).</summary>
    [Parameter] public string? SuggestieSamenvatting { get; set; }

    /// <summary>Wordt aangeroepen nadat een alias of leermoment is opgeslagen, zodat de pagina kan aanbieden opnieuw te beoordelen.</summary>
    [Parameter] public EventCallback OnGeleerd { get; set; }

    [Inject] private IAuthService Auth { get; set; } = default!;

    private enum Actie { Geen, Koppel, Corrigeer }

    private TraceStapDto? _openStap;
    private Actie _openActie;
    private string? _melding;

    private bool KanCorrigeren => Corrigeerbaar && Auth.IsAdmin;

    private void WisselActie(TraceStapDto stap, string _)
    {
        var actie = TraceActies.ClassificatieType(stap) is not null ? Actie.Corrigeer : Actie.Koppel;
        var sluiten = _openStap == stap && _openActie == actie;
        _openStap = sluiten ? null : stap;
        _openActie = sluiten ? Actie.Geen : actie;
        _melding = null;
    }

    private void Sluit()
    {
        _openStap = null;
        _openActie = Actie.Geen;
    }

    private async Task GeleerdAsync()
    {
        _melding = _openActie == Actie.Koppel
            ? "Gekoppeld. Beoordeel hetzelfde bericht opnieuw om het effect te zien."
            : "Leermoment opgeslagen. Beoordeel hetzelfde bericht opnieuw om het effect te zien.";
        _openActie = Actie.Geen;
        await OnGeleerd.InvokeAsync();
    }

    private Task GekoppeldAsync(TeamAliasAanmaakResultaatDto _) => GeleerdAsync();

    private bool TraceIsZeker => Trace?.Oordeel?.IsZeker ?? false;

    private string ZekerheidSamenvatting => (Proefrun, TraceIsZeker) switch
    {
        (true, true) => "Zou automatisch verstuurd worden",
        (true, false) => "Zou in review gaan",
        (false, true) => "Eindoordeel: zeker",
        (false, false) => "Eindoordeel: onzeker, handmatige controle nodig"
    };

    private string ZekerheidAlertKlasse => TraceIsZeker ? "alert alert-success" : "alert alert-warning";

    private static string StapKlasse(TraceStapDto stap) => stap.Zekerheid switch
    {
        "Mislukt" => "list-group-item d-flex list-group-item-danger",
        "Onzeker" => "list-group-item d-flex list-group-item-warning",
        _ => "list-group-item d-flex"
    };

    private static string ZekerheidBadgeKlasse(TraceStapDto stap) => stap.Zekerheid switch
    {
        "Mislukt" => "bg-danger",
        "Onzeker" => "bg-warning text-dark",
        _ => "bg-success"
    };

    /// <summary>Details als "sleutel: waarde"-regel; lege waarden vallen weg.</summary>
    private static string StapDetails(TraceStapDto stap)
        => string.Join(" · ", stap.Details
            .Where(d => !string.IsNullOrWhiteSpace(d.Value))
            .Select(d => $"{d.Key}: {d.Value}"));
}
