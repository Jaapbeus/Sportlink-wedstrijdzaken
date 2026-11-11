using Planner.Endpoints.Email;

namespace FunctionApp.Postgres.Monitoring;

/// <summary>
/// Persistente opslag van het tijdstip waarop een noodmail voor het laatst is verstuurd.
/// De registratie staat buiten het geheugen van de Function-worker, zodat het herhaalinterval
/// behouden blijft wanneer een worker opnieuw start. De tierimplementatie gebruikt de bestaande
/// Azure Table Storage-verbinding uit <c>AzureWebJobsStorage</c> en staat los van de bewaakte database;
/// hiervoor wordt geen apart opslagaccount aangemaakt.
/// </summary>
/// <remarks>
/// De noodmail-afhandeling gebruikt de gedeelde <see cref="INoodmailThrottlePoort"/>. Deze interface
/// verbindt die poort met de tiergebonden opslagimplementatie.
/// </remarks>
public interface INoodmailThrottleStore : INoodmailThrottlePoort
{
}
