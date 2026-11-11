using Planner.Endpoints.Email;

namespace SportlinkFunction.Monitoring;

/// <summary>
/// Persistente opslag van het tijdstip waarop een noodmail voor het laatst is verstuurd.
///
/// <para>
/// De registratie staat buiten het geheugen van de Function-worker, zodat het herhaalinterval
/// behouden blijft wanneer een worker opnieuw start.
/// </para>
///
/// <para>
/// De tierimplementatie gebruikt de bestaande Azure Table Storage-verbinding uit
/// <c>AzureWebJobsStorage</c>. De throttle staat bewust los van de bewaakte SQL-database, die bij een
/// storing onbereikbaar kan zijn; hiervoor wordt geen apart opslagaccount aangemaakt.
/// </para>
/// </summary>
/// <remarks>
/// De noodmail-afhandeling gebruikt de gedeelde <see cref="INoodmailThrottlePoort"/>. Deze interface
/// verbindt die poort met de tiergebonden opslagimplementatie.
/// </remarks>
public interface INoodmailThrottleStore : INoodmailThrottlePoort
{
}
