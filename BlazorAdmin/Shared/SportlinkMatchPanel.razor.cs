using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

/// <summary>
/// Code-behind van <c>SportlinkMatchPanel.razor</c> (#1122): laadt de Sportlink-gegevens van één
/// wedstrijd (lazy, pas als de rij is uitgeklapt) en voert de vier mutaties uit. Elke mutatie is
/// dezelfde drie regels: status starten → API aanroepen → <see cref="SportlinkActieStatus.Verwerk"/>;
/// de teksten per actie zijn het enige verschil.
/// </summary>
public partial class SportlinkMatchPanel
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    /// <summary>Onze eigen wedstrijdcode (his.matches) — de server resolvet daaruit het PublicMatchId.</summary>
    [Parameter, EditorRequired] public long WedstrijdCode { get; set; }

    private SportlinkMatchInfoDto? _info;
    private string? _laadFout;

    private readonly SportlinkActieStatus _kleedkamers = new();
    private string? _kleedkamerThuis, _kleedkamerUit, _kleedkamerOfficial;

    private readonly SportlinkActieStatus _veld = new();
    private string? _veldFieldId, _veldFieldSize;
    // #1339: dropdown-keuze op onze eigen veldnaam — vult FieldId/FieldSize hierboven met een
    // VOORSTEL (SportlinkFieldIdBuilder, Planner.Shared); de tekstvelden blijven bewerkbaar, de
    // beheerder controleert/overschrijft vóór het klikken op "Veld wijzigen".
    private int? _veldGekozenVeldNummer;
    private string? _veldGekozenSubpositie;

    private readonly SportlinkActieStatus _officials = new();
    private string? _officialScheidsrechter, _officialAr1, _officialAr2;

    private readonly SportlinkActieStatus _wijziging = new();
    private string? _wijzigingDatum, _wijzigingTijd, _wijzigingFacilityId, _wijzigingToelichting;
    private List<string>? _wijzigingValidatieMeldingen;

    private string Code => WedstrijdCode.ToString();

    protected override async Task OnInitializedAsync()
    {
        var result = await Api.GetSportlinkMatchInfoAsync(Code);
        if (result.Success)
        {
            _info = result.Data;
            // #1339: prefill met de huidige waarde in plaats van de velden leeg te laten — de
            // beheerder kan ze hierna nog steeds vrij overschrijven of via de dropdowns hieronder
            // een ander veld kiezen.
            _veldFieldId = _info?.FieldId;
            _veldFieldSize = _info?.FieldSize;

            // #1340 (VOORSTEL, DPO-vraag nog niet bevestigd — zie docs/SPORTLINK-WEB-EXTENSION.md):
            // prefill met de huidige relatiecode i.p.v. de velden leeg te laten. De server heeft ze
            // al genuld als de rol geen ScheidsrechterFeatureToegestaan heeft (SportlinkRolFeature.
            // VoegToestemmingenToe), dus hier is geen extra gate nodig — net als bij #1339's
            // FieldId/FieldSize hierboven blijven de velden vrij overschrijfbaar.
            _officialScheidsrechter = _info?.ScheidsrechterRelatieCode;
            _officialAr1 = _info?.Ar1RelatieCode;
            _officialAr2 = _info?.Ar2RelatieCode;
        }
        else _laadFout = result.ErrorMessage ?? "Onbekende fout bij ophalen Sportlink-gegevens.";
    }

    /// <summary>#1339: veld-dropdown gewijzigd — vult FieldId met het server-berekende voorstel
    /// (<c>SportlinkFieldIdBuilder.BouwVoorstelFieldId</c>) voor het gekozen veldnummer. Blijft
    /// bewerkbaar; dit is een voorstel, geen bevestigde waarde (zie SportlinkFieldIdBuilder-doc).</summary>
    private void OnVeldGekozen(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var veldNummer))
        {
            _veldGekozenVeldNummer = veldNummer;
            var optie = _info?.VeldOpties?.FirstOrDefault(v => v.VeldNummer == veldNummer);
            if (!string.IsNullOrWhiteSpace(optie?.VoorstelFieldId))
                _veldFieldId = optie.VoorstelFieldId;
        }
        else
        {
            _veldGekozenVeldNummer = null;
        }
    }

    /// <summary>#1339: subpositie-dropdown gewijzigd — vult FieldSize met het server-berekende
    /// voorstel (<c>SportlinkFieldIdBuilder.BouwVoorstelFieldSize</c>).</summary>
    private void OnSubpositieGekozen(ChangeEventArgs e)
    {
        var gekozen = e.Value?.ToString();
        _veldGekozenSubpositie = string.IsNullOrEmpty(gekozen) ? null : gekozen;
        var optie = _info?.SubpositieOpties?.FirstOrDefault(s => (s.Subpositie ?? "") == (gekozen ?? ""));
        if (optie != null) _veldFieldSize = optie.VoorstelFieldSize;
    }

    private async Task SaveDressingRoomsAsync()
    {
        _kleedkamers.Start();
        try
        {
            var r = await Api.PutSportlinkDressingRoomsAsync(Code, _kleedkamerThuis, _kleedkamerUit, _kleedkamerOfficial);
            _kleedkamers.Verwerk(r.Success, r.ErrorMessage, r.Data, "Kleedkamers toegewezen in Sportlink Club.", "Sportlink heeft de wijziging afgewezen");
        }
        finally { _kleedkamers.Klaar(); }
    }

    private async Task SaveFieldAsync()
    {
        _veld.Start();
        try
        {
            var r = await Api.PutSportlinkFieldAsync(Code, _veldFieldId, _veldFieldSize, fieldOffset: null);
            _veld.Verwerk(r.Success, r.ErrorMessage, r.Data, "Veld gewijzigd in Sportlink Club.", "Sportlink heeft de wijziging afgewezen");
        }
        finally { _veld.Klaar(); }
    }

    private async Task SaveOfficialsAsync()
    {
        _officials.Start();
        try
        {
            // Positienamen ONBEVESTIGD (#994) — zie SportlinkOfficialToewijzing.
            var officials = new (string Positie, string? PersoonId)[]
            {
                ("Referee", _officialScheidsrechter), ("AssistantReferee1", _officialAr1), ("AssistantReferee2", _officialAr2),
            }
            .Where(o => !string.IsNullOrWhiteSpace(o.PersoonId))
            .Select(o => (o.Positie, o.PersoonId!))
            .ToList();

            var r = await Api.PutSportlinkOfficialsAsync(Code, officials);
            _officials.Verwerk(r.Success, r.ErrorMessage, r.Data, "Officials toegewezen in Sportlink Club.", "Sportlink heeft de toewijzing afgewezen");
        }
        finally { _officials.Klaar(); }
    }

    private async Task SaveMatchChangeRequestAsync()
    {
        _wijzigingValidatieMeldingen = null;
        if (string.IsNullOrWhiteSpace(_wijzigingToelichting))
        {
            _wijziging.Fout("Toelichting is verplicht bij een wijzigingsverzoek.");
            return;
        }

        _wijziging.Start();
        try
        {
            var r = await Api.PutSportlinkMatchChangeRequestAsync(Code, _wijzigingDatum, _wijzigingTijd, _wijzigingFacilityId, _wijzigingToelichting);
            _wijzigingValidatieMeldingen = r.Data?.Validatie?.ValidationResultMessages;
            _wijziging.Verwerk(r.Success, r.ErrorMessage, r.Data?.Mutatie, "Validatie uitgevoerd door Sportlink Club.", "Sportlink heeft het wijzigingsverzoek afgewezen");
        }
        finally { _wijziging.Klaar(); }
    }
}
