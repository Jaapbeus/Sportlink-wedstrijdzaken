using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class EmailTemplates : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

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
        var r = await Api.GetTemplatesAsync();
        templates = r.Success ? r.Data ?? new() : new();
    }

    private async Task LoadVoetnootAsync()
    {
        var r = await Api.GetSettingsAsync();
        if (r.Success && r.Data != null)
            voetnoot = r.Data.EmailVoetnoot ?? "";
    }

    private async Task OpslaanVoetnootAsync()
    {
        var dto = new SettingsUpdateDto
        {
            Velden = new Dictionary<string, string?> { ["EmailVoetnoot"] = voetnoot }
        };
        var r = await Api.UpdateSettingsAsync(dto);
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

    private void PrefillDefaults(string key)
    {
        if (editing == null) return;
        string onderwerp = "";
        string body = "";

        switch (key)
        {
            case "beschikbaarheid_check":
                onderwerp = "Beschikbaarheid wedstrijd {{datum}} — {{team}} vs {{tegenstander}}";
                body = "Beste {{aanhef}},\n\nWij willen graag informeren of jullie beschikbaar zijn voor de wedstrijd op {{datum}} om {{aanvangstijd}}.\n\nThuisteam: {{team}}\nTegenstander: {{tegenstander}}\n\nKunt u ons zo spoedig mogelijk laten weten of de locatie en het tijdstip uitkomen?";
                break;
            case "herplan_verzoek":
                onderwerp = "Verzoek herplanning wedstrijd {{datum}} — {{team}} vs {{tegenstander}}";
                body = "Beste {{aanhef}},\n\nWij verzoeken u vriendelijk de geplande wedstrijd van {{datum}} om {{aanvangstijd}} te herplannen.\n\nThuisteam: {{team}}\nTegenstander: {{tegenstander}}\n\nZou u ons een alternatieve datum en tijd kunnen voorstellen?";
                break;
            case "bevestiging":
                onderwerp = "Bevestiging wedstrijd {{datum}} — {{team}} vs {{tegenstander}}";
                body = "Beste {{aanhef}},\n\nHierbij bevestigen wij de wedstrijd op {{datum}} om {{aanvangstijd}}.\n\nThuisteam: {{team}}\nTegenstander: {{tegenstander}}\n\nTot dan!";
                break;
            case "team_contact_opvragen":
                onderwerp = "Uw vraag over de begeleiding van {{team}}";
                body = "Beste {{voornaam}},\n\nUw vraag over de begeleiding van {{team}} is doorgestuurd. De begeleider neemt rechtstreeks contact met u op.";
                break;
            case "buiten_scope":
                onderwerp = "Uw bericht ontvangen";
                body = "Beste {{voornaam}},\n\nBedankt voor uw bericht. Uw vraag valt buiten het bereik van de automatische verwerking. Neem contact op met de club voor verdere hulp.";
                break;
        }

        if (!string.IsNullOrWhiteSpace(onderwerp)) editing.Onderwerp = onderwerp;
        if (!string.IsNullOrWhiteSpace(body)) editing.BodyTemplate = body;
    }

    private async Task OpslaanTemplateAsync()
    {
        if (editing == null || string.IsNullOrWhiteSpace(editing.TemplateKey))
        {
            templateMessage = "Template key verplicht";
            return;
        }
        var r = await Api.UpdateTemplateAsync(editing.TemplateKey, editing);
        templateMessage = r.Success ? "Opgeslagen." : "Fout: " + r.ErrorMessage;
        if (r.Success)
        {
            editing = null;
            await LoadTemplatesAsync();
        }
    }

    private async Task ResetAsync(string key)
    {
        var r = await Api.ResetTemplateAsync(key);
        templateMessage = r.Success ? $"Template '{key}' gereset naar default." : "Fout: " + r.ErrorMessage;
        await LoadTemplatesAsync();
    }

    private static (string label, string css) GetCategorie(string key) => key switch
    {
        var k when k.StartsWith("beschikbaarheid") => ("Beschikbaarheid", "bg-info text-dark"),
        var k when k.StartsWith("herplan")         => ("Herplanning", "bg-warning text-dark"),
        var k when k.StartsWith("bevestig")        => ("Bevestiging", "bg-success"),
        var k when k.StartsWith("team_contact")    => ("Teambegeleiding", "bg-primary"),
        var k when k.StartsWith("buiten_scope")    => ("Buiten scope", "bg-secondary"),
        _                                           => ("Overig", "bg-secondary"),
    };
}
