using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

/// <summary>
/// Bewerk-/aanmaakformulier voor één speeltijdcategorie. Eén definitie, zodat de pagina het
/// zowel direct onder de bewerkte regel als bovenaan de tabel (nieuwe categorie) kan tonen (#1543).
/// Het formulier hoort bij precies één <see cref="SpeeltijdBewerkSessie"/> (#1552).
/// </summary>
public partial class SpeeltijdFormulier
{
    private static int _volgnummer;

    /// <summary>Uniek per formulier-instantie, zodat label-for/input-id nooit botsen (#1554).</summary>
    private readonly string _idPrefix = $"speeltijd-{Interlocked.Increment(ref _volgnummer)}";

    [Parameter, EditorRequired] public SpeeltijdBewerkSessie Sessie { get; set; } = default!;
    [Parameter] public EventCallback OnOpslaan { get; set; }
    [Parameter] public EventCallback OnAnnuleer { get; set; }

    private string Id(string veld) => $"{_idPrefix}-{veld}";

    private void ZetVoorkeurTijd(string? v) =>
        Sessie.Model.StandaardVoorkeurTijd = string.IsNullOrWhiteSpace(v) ? null : v;
}
