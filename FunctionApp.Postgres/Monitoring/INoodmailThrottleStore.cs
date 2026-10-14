using Planner.Endpoints.Email;

namespace FunctionApp.Postgres.Monitoring;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Monitoring/INoodmailThrottleStore.cs</c> (#972 —
/// port van EmailProcessorFunction). Persistente opslag voor "wanneer is deze noodmail voor het
/// laatst verstuurd", zodat het throttle-gedrag niet afhangt van een <c>static</c>/<c>volatile</c>
/// veld dat bij elke cold start reset. Woordelijke kopie van het contract; alleen de onafhankelijke
/// database-uitvalmonitor (<c>DatabaseUitvalMonitorFunction</c>/<c>IDatabaseStatusReader</c>, #831)
/// is hier NIET vertaald — dat is een apart, niet-#972-gerelateerd monitoring-issue.
/// </summary>
/// <remarks>
/// De leden staan sinds #1716 slice 9 in de gedeelde <see cref="INoodmailThrottlePoort"/>, zodat de
/// tier-onafhankelijke noodmail-afhandeling (<c>Planner.Endpoints/Email/EmailNoodmailMelding.cs</c>) dezelfde
/// opslag gebruikt; de DI-registratie van deze tier blijft ongewijzigd.
/// </remarks>
public interface INoodmailThrottleStore : INoodmailThrottlePoort
{
}
