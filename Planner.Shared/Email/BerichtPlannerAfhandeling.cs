using Newtonsoft.Json;

namespace Planner.Shared.Email;

/// <summary>Dispatches only after team resolution; all tier I/O remains behind unevaluated branch delegates.</summary>
public static class BerichtPlannerAfhandeling
{
    public static Task<string> VerwerkAsync(
        BerichtClassificatie classificatie, Func<Task<string>> beschikbaarheid,
        Func<Task<string>> herplan, Func<Task<string>> teamContact)
    {
        if (classificatie.TeamHandmatigBeoordelen)
            return Task.FromResult(TeamHandmatigeAfhandeling.PlannerResponse(classificatie));

        return classificatie.Type switch
        {
            VerzoekType.BeschikbaarheidCheck => beschikbaarheid(),
            VerzoekType.HerplanVerzoek => herplan(),
            VerzoekType.TeamContactOpvragen => teamContact(),
            VerzoekType.Bevestiging => Task.FromResult(JsonConvert.SerializeObject(new
            {
                status = "Bevestiging ontvangen",
                opmerking = "Bevestigingen vereisen handmatige afhandeling door de coördinator"
            })),
            _ => Task.FromResult(JsonConvert.SerializeObject(new { status = "Niet verwerkt" }))
        };
    }
}
