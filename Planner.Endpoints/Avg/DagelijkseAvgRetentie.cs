using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Feedback;
using Planner.Shared.Avg;
using Planner.Shared.Feedback;

namespace Planner.Endpoints.Avg;

/// <summary>
/// De dagelijkse AVG-retentierun van beide tiers (timer <c>CleanupFeedback</c>, 05:15 UTC):
/// eerst de bewaartermijnen van de feedbackmeldingen (#764, #1476), daarna de seizoensretentie van
/// de persoonskolommen in de Sportlink-historietabellen (#1698).
/// <para>
/// <b>Waarom één timer.</b> Een eigen timer per retentiestap herhaalt op beide tiers hetzelfde
/// timercontract (klasse, <c>[Function]</c>, <c>TimerTrigger</c>, logger, aanroep) en laat de
/// tier-duplicatie stijgen; eigenaarsbesluit 2026-10-13: dat plafond stijgt niet. De tier levert
/// daarom alleen zijn eigen opslag aan deze ene run.
/// </para>
/// <para>
/// <b>Stappen zijn onafhankelijk.</b> Een fout in de ene stap (bijvoorbeeld de GitHub-statuscontrole
/// of een onbereikbare seizoenstabel) houdt de andere niet tegen; elke stap logt zijn eigen fout.
/// Daarna gooit de run de fout door (bij twee fouten als <see cref="AggregateException"/>), zodat de
/// Functions-host de run als mislukt ziet.
/// </para>
/// </summary>
public static class DagelijkseAvgRetentie
{
    public static async Task VoerUitAsync(
        Func<Task> wachtOpDatabase,
        IFeedbackStore feedbackStore,
        bool externeIntegratiesToegestaan,
        IHisRetentieOpslag hisOpslag,
        ILogger log)
    {
        var fouten = new List<Exception>();
        await VangAsync(
            () => FeedbackEndpointCore.VoerRetentieTimerAsync(wachtOpDatabase, feedbackStore, externeIntegratiesToegestaan, log),
            fouten);
        await VangAsync(() => HisPersoonsgegevensRetentie.VoerStapUitAsync(hisOpslag, log), fouten);

        if (fouten.Count == 1)
            ExceptionDispatchInfo.Capture(fouten[0]).Throw();
        if (fouten.Count > 1)
            throw new AggregateException("Dagelijkse AVG-retentie: meerdere stappen mislukt", fouten);
    }

    private static async Task VangAsync(Func<Task> stap, List<Exception> fouten)
    {
        try
        {
            await stap();
        }
        catch (Exception ex)
        {
            fouten.Add(ex);
        }
    }
}
