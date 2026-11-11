using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Planner.Shared.Email;

namespace BlazorAdmin.Pages;

public partial class EmailTemplates : ClubSelectorPageBase
{
    [Inject] private EmailApiClient EmailApi { get; set; } = default!;
    [Inject] private ClubInstellingenCache InstellingenCache { get; set; } = default!;

    private List<TemplateDto> templates = new();
    private TemplateDto? editing;
    private bool isNew;
    private bool loading = true;
    private string? templatesLoadError;
    private string? voetnootLoadError;
    private readonly LookupGeneratieGuard _loadGuard = new();
    private readonly LookupGeneratieGuard _templatesLoadGuard = new();
    private readonly LookupGeneratieGuard _voetnootLoadGuard = new();
    private readonly InlineBevestiging<string> _resetConfirmation = new();
    private string? templateMessage;

    private string voetnoot = "";
    private string? voetnootMessage;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected override Task OnClubChangedAsync()
    {
        _resetConfirmation.Annuleer();
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        var clubCode = ClubSelector.SelectedClubCode;
        var generatie = _loadGuard.Start(clubCode);
        loading = true;
        await Task.WhenAll(LoadTemplatesAsync(), LoadVoetnootAsync());
        if (IsActueleLading(generatie, clubCode)) loading = false;
    }

    private async Task LoadTemplatesAsync()
    {
        var clubCode = ClubSelector.SelectedClubCode;
        var generatie = _templatesLoadGuard.Start(clubCode);
        var r = await EmailApi.GetTemplatesAsync();
        if (!_templatesLoadGuard.IsActueel(generatie) || ClubSelector.SelectedClubCode != clubCode) return;
        templates = r.Success ? r.Data ?? new() : new();
        templatesLoadError = r.Success ? null : r.ErrorMessage ?? "Templates ophalen mislukt.";
    }

    private async Task LoadVoetnootAsync()
    {
        var clubCode = ClubSelector.SelectedClubCode;
        var generatie = _voetnootLoadGuard.Start(clubCode);
        var r = await InstellingenCache.GetSettingsAsync();
        if (!_voetnootLoadGuard.IsActueel(generatie) || ClubSelector.SelectedClubCode != clubCode) return;
        if (r.Success && r.Data != null)
        {
            voetnoot = r.Data.EmailVoetnoot ?? "";
            voetnootLoadError = null;
        }
        else
            voetnootLoadError = r.ErrorMessage ?? "E-mailvoetnoot ophalen mislukt.";
    }

    private bool IsActueleLading(int generatie, string? clubCode) =>
        _loadGuard.IsActueel(generatie) && ClubSelector.SelectedClubCode == clubCode;

    private async Task OpslaanVoetnootAsync()
    {
        var dto = new SettingsUpdateDto
        {
            Velden = new Dictionary<string, string?> { ["EmailVoetnoot"] = voetnoot }
        };
        var r = await InstellingenCache.UpdateSettingsAsync(dto);
        voetnootMessage = r.Success ? "Voetnoot opgeslagen." : "Fout: " + r.ErrorMessage;
        await Task.Delay(3000);
        voetnootMessage = null;
        StateHasChanged();
    }

    private void Edit(TemplateDto t)
    {
        editing = new TemplateDto
        {
            Id = t.Id,
            TemplateKey = t.TemplateKey,
            Onderwerp = t.Onderwerp,
            BodyTemplate = t.BodyTemplate,
            Actief = t.Actief,
            ClubCode = t.ClubCode
        };
        isNew = false;
    }

    private void StartNewTemplate()
    {
        editing = new TemplateDto { Actief = true };
        isNew = true;
    }

    private void OnTemplateKeyChange(ChangeEventArgs e)
    {
        if (editing == null) return;
        var key = e.Value?.ToString() ?? "";
        editing.TemplateKey = key;
        PrefillDefaults(key);
    }

    /// <summary>Vult het formulier met de standaardtekst van de gekozen sleutel (#1780: uit de gedeelde catalogus).</summary>
    private void PrefillDefaults(string key)
    {
        var info = AntwoordSjabloonCatalogus.Zoek(key);
        if (editing == null || info == null) return;

        editing.Onderwerp = info.StandaardOnderwerp;
        editing.BodyTemplate = info.StandaardBody;
    }

    private static IEnumerable<IGrouping<string, AntwoordSjabloonInfo>> SleutelGroepen
        => AntwoordSjabloonCatalogus.Alle.GroupBy(i => i.Groep);

    /// <summary>Uitleg bij de sleutel die in het formulier staat; <c>null</c> voor een onbekende of nog niet gekozen sleutel.</summary>
    private AntwoordSjabloonInfo? GekozenSleutel => AntwoordSjabloonCatalogus.Zoek(editing?.TemplateKey);

    /// <summary>De placeholders die bij de gekozen sleutel horen, als <c>{{naam}}</c>-lijst.</summary>
    private string PlaceholderTekst
        => string.Join(", ", AntwoordSjabloonCatalogus.PlaceholdersVoor(editing?.TemplateKey).Select(p => "{{" + p + "}}"));

    private async Task OpslaanTemplateAsync()
    {
        if (editing == null || string.IsNullOrWhiteSpace(editing.TemplateKey))
        {
            templateMessage = "Template key verplicht";
            return;
        }
        var r = await EmailApi.UpdateTemplateAsync(editing.TemplateKey, editing);
        templateMessage = r.Success ? "Opgeslagen." : "Fout: " + r.ErrorMessage;
        if (r.Success)
        {
            editing = null;
            await LoadTemplatesAsync();
        }
    }

    private async Task ResetAsync(string key)
    {
        if (!_resetConfirmation.Bevestig(key)) return;
        var r = await EmailApi.ResetTemplateAsync(key);
        templateMessage = r.Success ? $"Template '{key}' gereset naar default." : "Fout: " + r.ErrorMessage;
        if (r.Success) await LoadTemplatesAsync();
    }

    private void VraagReset(string key) => _resetConfirmation.Vraag(key);

    private void AnnuleerReset() => _resetConfirmation.Annuleer();

    private static (string label, string css) GetCategorie(string key)
    {
        var groep = AntwoordSjabloonCatalogus.Zoek(key)?.Groep ?? "Overig";
        return groep switch
        {
            "Beschikbaarheid" => (groep, "bg-info text-dark"),
            "Herplanning"     => (groep, "bg-warning text-dark"),
            "Bevestiging"     => (groep, "bg-success"),
            "Teambegeleiding" => (groep, "bg-primary"),
            _                 => (groep, "bg-secondary"),
        };
    }
}
