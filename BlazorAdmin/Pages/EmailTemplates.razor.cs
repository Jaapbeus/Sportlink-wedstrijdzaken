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
    private string? templateMessage;

    private string voetnoot = "";
    private string? voetnootMessage;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected override Task OnClubChangedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        loading = true;
        await Task.WhenAll(LoadTemplatesAsync(), LoadVoetnootAsync());
        loading = false;
    }

    private async Task LoadTemplatesAsync()
    {
        var r = await EmailApi.GetTemplatesAsync();
        templates = r.Success ? r.Data ?? new() : new();
    }

    private async Task LoadVoetnootAsync()
    {
        var r = await InstellingenCache.GetSettingsAsync();
        if (r.Success && r.Data != null)
            voetnoot = r.Data.EmailVoetnoot ?? "";
    }

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
        var r = await EmailApi.ResetTemplateAsync(key);
        templateMessage = r.Success ? $"Template '{key}' gereset naar default." : "Fout: " + r.ErrorMessage;
        await LoadTemplatesAsync();
    }

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
