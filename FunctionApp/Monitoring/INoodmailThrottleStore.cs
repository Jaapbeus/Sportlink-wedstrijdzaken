using Planner.Endpoints.Email;

namespace SportlinkFunction.Monitoring;

/// <summary>
/// Persistente opslag voor "wanneer is deze noodmail voor het laatst verstuurd" (#831).
///
/// <para>
/// Vóór #831 stond deze staat in een <c>static</c>/<c>volatile</c> veld op de functieklasse zelf
/// (procesgeheugen). Op een Consumption-plan wordt een timer-triggered worker niet gegarandeerd warm
/// gehouden tussen twee aanroepen; bij elke cold start reset zo'n veld naar de default. Het
/// throttle-gedrag hing dus af van iets wat de code zelf niet controleert of logt: hoe vaak de host in
/// de praktijk herstart. Tijdens de 5+ dagen durende database-uitval van 25-30 augustus 2026
/// (#799/#808) leidde dat ertoe dat er geen enkele noodmail is aangekomen.
/// </para>
///
/// <para>
/// Implementaties persisteren daarom buiten het procesgeheugen. De standaardimplementatie gebruikt
/// Azure Table Storage via de bestaande <c>AzureWebJobsStorage</c>-opslagaccount — bewust niet de SQL-
/// database: de meest voorkomende aanleiding om een noodmail te versturen ís dat die database
/// onbereikbaar is, dus wegschrijven naar diezelfde database zou circulair zijn (je kunt "ik kon net
/// niet bij de database" niet betrouwbaar in die database vastleggen). De opslagaccount bestaat al
/// (vereist voor de Functions-runtime zelf) — dit voegt geen nieuwe Azure-resource toe.
/// </para>
/// </summary>
/// <remarks>
/// De leden staan sinds #1716 slice 9 in de gedeelde <see cref="INoodmailThrottlePoort"/>, zodat de
/// tier-onafhankelijke noodmail-afhandeling (<c>Planner.Endpoints/Email/EmailNoodmailMelding.cs</c>) dezelfde
/// opslag gebruikt; de DI-registratie van deze tier blijft ongewijzigd.
/// </remarks>
public interface INoodmailThrottleStore : INoodmailThrottlePoort
{
}
