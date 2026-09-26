using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class Velden : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private List<VeldDto> velden = new();
    private List<VeldBeschikbaarheidDto> beschikbaarheid = new();
    private List<VeldTrainingDto> training = new();
    private List<VeldPeriodeDto> periodes = new();

    private VeldDto? editingVeld;
    private VeldBeschikbaarheidDto? editingBeschikbaarheid;
    private VeldTrainingDto? editingTraining;
    private VeldPeriodeDto? editingPeriode;

    private bool isNewVeld;
    private bool isNewBeschikbaarheid;
    private bool isNewTraining;
    private bool isNewPeriode;

    private bool loading = true;
    private string? veldMessage;
    private string? beschikbaarheidMessage;
    private string? trainingMessage;
    private string? periodeMessage;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    protected override Task OnClubChangedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        loading = true;
        var veldenResult = await Api.GetVeldenAsync();
        velden = veldenResult.Success ? veldenResult.Data ?? new() : new();
        var periodesResult = await Api.GetVeldPeriodesAsync();
        periodes = periodesResult.Success ? periodesResult.Data ?? new() : new();
        var beschikbaarheidResult = await Api.GetVeldBeschikbaarheidAsync();
        beschikbaarheid = beschikbaarheidResult.Success ? beschikbaarheidResult.Data ?? new() : new();
        var trainingResult = await Api.GetVeldTrainingAsync();
        training = trainingResult.Success ? trainingResult.Data ?? new() : new();
        loading = false;
    }

    // ── Velden ──

    private void StartNewVeld()
    {
        editingVeld = new VeldDto { VeldType = "kunstgras", Actief = true };
        isNewVeld = true;
        veldMessage = null;
    }

    private void EditVeld(VeldDto v)
    {
        editingVeld = new VeldDto
        {
            VeldNummer = v.VeldNummer, VeldNaam = v.VeldNaam, VeldType = v.VeldType,
            HeeftKunstlicht = v.HeeftKunstlicht, Actief = v.Actief
        };
        isNewVeld = false;
        veldMessage = null;
    }

    private async Task OpslaanVeldAsync()
    {
        if (editingVeld == null) return;
        var r = isNewVeld
            ? await Api.CreateVeldAsync(editingVeld)
            : await Api.UpdateVeldAsync(editingVeld.VeldNummer, editingVeld);
        veldMessage = r.Success ? "Opgeslagen." : "Fout: " + r.ErrorMessage;
        if (r.Success) { editingVeld = null; await LoadAsync(); }
    }

    // ── Veldbeschikbaarheid ──

    private void StartNewBeschikbaarheid()
    {
        editingBeschikbaarheid = new VeldBeschikbaarheidDto { DagVanWeek = 1, BeschikbaarVanaf = "18:00", BeschikbaarTot = "22:00" };
        isNewBeschikbaarheid = true;
        beschikbaarheidMessage = null;
    }

    private void EditBeschikbaarheid(VeldBeschikbaarheidDto b)
    {
        editingBeschikbaarheid = new VeldBeschikbaarheidDto
        {
            Id = b.Id, VeldNummer = b.VeldNummer, VeldNaam = b.VeldNaam, DagVanWeek = b.DagVanWeek,
            BeschikbaarVanaf = b.BeschikbaarVanaf, BeschikbaarTot = b.BeschikbaarTot,
            GebruikZonsondergang = b.GebruikZonsondergang, PeriodeId = b.PeriodeId, PeriodeNaam = b.PeriodeNaam
        };
        isNewBeschikbaarheid = false;
        beschikbaarheidMessage = null;
    }

    private void OnPeriodeGekozen(ChangeEventArgs e)
    {
        if (editingBeschikbaarheid == null) return;
        var waarde = e.Value?.ToString();
        editingBeschikbaarheid.PeriodeId = string.IsNullOrEmpty(waarde) ? null : int.Parse(waarde);
    }

    private async Task OpslaanBeschikbaarheidAsync()
    {
        if (editingBeschikbaarheid == null) return;
        var r = isNewBeschikbaarheid
            ? await Api.CreateVeldBeschikbaarheidAsync(editingBeschikbaarheid)
            : await Api.UpdateVeldBeschikbaarheidAsync(editingBeschikbaarheid.Id, editingBeschikbaarheid);
        beschikbaarheidMessage = r.Success ? "Opgeslagen." : "Fout: " + r.ErrorMessage;
        if (r.Success) { editingBeschikbaarheid = null; await LoadAsync(); }
    }

    private async Task DeleteBeschikbaarheidAsync(int id)
    {
        var r = await Api.DeleteVeldBeschikbaarheidAsync(id);
        beschikbaarheidMessage = r.Success ? "Verwijderd." : "Fout: " + r.ErrorMessage;
        await LoadAsync();
    }

    // ── Periodes ──

    private void StartNewPeriode()
    {
        var vandaag = DateTime.Today;
        editingPeriode = new VeldPeriodeDto
        {
            Naam = "", DatumVan = vandaag.ToString("yyyy-MM-dd"), DatumTot = vandaag.ToString("yyyy-MM-dd"), Actief = true
        };
        isNewPeriode = true;
        periodeMessage = null;
    }

    private void EditPeriode(VeldPeriodeDto p)
    {
        editingPeriode = new VeldPeriodeDto { Id = p.Id, Naam = p.Naam, DatumVan = p.DatumVan, DatumTot = p.DatumTot, Actief = p.Actief };
        isNewPeriode = false;
        periodeMessage = null;
    }

    private async Task OpslaanPeriodeAsync()
    {
        if (editingPeriode == null) return;
        var r = isNewPeriode
            ? await Api.CreateVeldPeriodeAsync(editingPeriode)
            : await Api.UpdateVeldPeriodeAsync(editingPeriode.Id, editingPeriode);
        periodeMessage = r.Success ? "Opgeslagen." : "Fout: " + r.ErrorMessage;
        if (r.Success) { editingPeriode = null; await LoadAsync(); }
    }

    private async Task DeletePeriodeAsync(int id)
    {
        var r = await Api.DeleteVeldPeriodeAsync(id);
        periodeMessage = r.Success ? "Verwijderd." : "Fout: " + r.ErrorMessage;
        await LoadAsync();
    }

    // ── Trainingsschema ──

    private void StartNewTraining()
    {
        editingTraining = new VeldTrainingDto { DagVanWeek = 1, VanTijd = "19:00", TotTijd = "20:30", Actief = true };
        isNewTraining = true;
        trainingMessage = null;
    }

    private void EditTraining(VeldTrainingDto t)
    {
        editingTraining = new VeldTrainingDto
        {
            Id = t.Id, VeldNummer = t.VeldNummer, VeldNaam = t.VeldNaam, DagVanWeek = t.DagVanWeek,
            VanTijd = t.VanTijd, TotTijd = t.TotTijd, Omschrijving = t.Omschrijving, Actief = t.Actief
        };
        isNewTraining = false;
        trainingMessage = null;
    }

    private async Task OpslaanTrainingAsync()
    {
        if (editingTraining == null) return;
        var r = isNewTraining
            ? await Api.CreateVeldTrainingAsync(editingTraining)
            : await Api.UpdateVeldTrainingAsync(editingTraining.Id, editingTraining);
        trainingMessage = r.Success ? "Opgeslagen." : "Fout: " + r.ErrorMessage;
        if (r.Success) { editingTraining = null; await LoadAsync(); }
    }

    private async Task DeleteTrainingAsync(int id)
    {
        var r = await Api.DeleteVeldTrainingAsync(id);
        trainingMessage = r.Success ? "Verwijderd." : "Fout: " + r.ErrorMessage;
        await LoadAsync();
    }
}
