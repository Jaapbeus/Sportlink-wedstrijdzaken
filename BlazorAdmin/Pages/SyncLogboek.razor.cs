using BlazorAdmin.Models;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class SyncLogboek
{
    [Parameter] public SyncStatusDto? Status { get; set; }
    [Parameter] public bool StatusOnzeker { get; set; }

    // Beide gebouwde tiers registreren eerst deze vijf pipelinefasen. De laatste twee
    // wisselen per tier van volgorde; ook een overgeslagen fase krijgt een logregel.
    private static readonly string[] VasteFasen = ["teams", "programma", "uitslagen", "matchdetails", "reconciliatie"];
    private const int NormaleFaseAantal = 7;

    private static string FaseVoortgang(SyncJobDto job, bool logAvailable)
    {
        if (!logAvailable) return "Fasepositie tijdelijk niet beschikbaar.";
        var fasen = job.Phases?.OrderBy(fase => fase.StartedAt).ToArray() ?? [];
        var pipelineFasen = fasen.Where(fase => fase.Phase != "configuratie").ToArray();
        var pipelineNamen = pipelineFasen.Select(fase => fase.Phase).Where(naam => naam != null).Select(naam => naam!).Distinct().ToArray();
        if (fasen.Length == 0)
            return job.Status == "pending" ? "Fase 1 van 7 volgt na het ophalen uit de wachtrij." : "Nog geen fase geregistreerd (0 van 7).";

        if (pipelineNamen.Length == 0)
            return fasen.Any(fase => fase.Phase == "configuratie" && fase.Status == "failed")
                ? "Configuratie mislukt; geen pipelinefase gestart."
                : "Nog geen pipelinefase geregistreerd (0 van 7).";
        var afgerond = pipelineFasen.Count(fase => fase.Status is "succeeded" or "partial" or "failed" or "skipped");
        var voortgang = $"{afgerond} {(afgerond == 1 ? "fase" : "fasen")} afgerond";
        var volgordeBekend = pipelineNamen.Length <= NormaleFaseAantal &&
            pipelineNamen.Take(Math.Min(pipelineNamen.Length, VasteFasen.Length)).SequenceEqual(VasteFasen.Take(Math.Min(pipelineNamen.Length, VasteFasen.Length))) &&
            (pipelineNamen.Length <= 5 || pipelineNamen[5] is "planner-view" or "canonicalisatie") &&
            (pipelineNamen.Length <= 6 || (pipelineNamen[6] is "planner-view" or "canonicalisatie" && pipelineNamen[6] != pipelineNamen[5]));
        if (!volgordeBekend) return $"Fasepositie onbekend; {voortgang}.";

        return $"Fase {pipelineNamen.Length} van {NormaleFaseAantal}: {Instellingen.SyncFaseTekst(pipelineNamen[^1])} · {voortgang}.";
    }
}
