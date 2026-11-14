using BlazorAdmin.Models;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class SyncLogboek
{
    [Parameter] public SyncStatusDto? Status { get; set; }

    // Beide gebouwde tiers registreren deze vijf fasen in vaste volgorde. De laatste twee
    // wisselen per tier van volgorde; ook een overgeslagen fase krijgt een logregel.
    private static readonly string[] VasteFasen = ["teams", "programma", "uitslagen", "matchdetails", "reconciliatie"];
    private const int NormaleFaseAantal = 7;

    private static string FaseVoortgang(SyncJobDto job, bool logAvailable)
    {
        if (!logAvailable) return "Fasepositie tijdelijk niet beschikbaar.";
        var fasen = job.Phases?.OrderBy(fase => fase.StartedAt).ToArray() ?? [];
        var namen = fasen.Select(fase => fase.Phase).Where(naam => naam != null).Select(naam => naam!).Distinct().ToArray();
        if (namen.Length == 0)
            return job.Status == "pending" ? "Fase 1 van 7 volgt na het ophalen uit de wachtrij." : "Nog geen fase geregistreerd (0 van 7).";

        var afgerond = fasen.Count(fase => fase.Status is "succeeded" or "partial" or "failed" or "skipped");
        var voortgang = $"{afgerond} {(afgerond == 1 ? "fase" : "fasen")} afgerond";
        var volgordeBekend = namen.Length <= NormaleFaseAantal &&
            namen.Take(Math.Min(namen.Length, VasteFasen.Length)).SequenceEqual(VasteFasen.Take(Math.Min(namen.Length, VasteFasen.Length))) &&
            (namen.Length <= 5 || namen[5] is "planner-view" or "canonicalisatie") &&
            (namen.Length <= 6 || (namen[6] is "planner-view" or "canonicalisatie" && namen[6] != namen[5]));
        if (!volgordeBekend) return $"Fasepositie onbekend; {voortgang}.";

        return $"Fase {namen.Length} van {NormaleFaseAantal}: {Instellingen.SyncFaseTekst(namen[^1])} · {voortgang}.";
    }
}
