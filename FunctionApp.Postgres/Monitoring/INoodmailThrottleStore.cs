namespace FunctionApp.Postgres.Monitoring;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Monitoring/INoodmailThrottleStore.cs</c> (#972 —
/// port van EmailProcessorFunction). Persistente opslag voor "wanneer is deze noodmail voor het
/// laatst verstuurd", zodat het throttle-gedrag niet afhangt van een <c>static</c>/<c>volatile</c>
/// veld dat bij elke cold start reset. Woordelijke kopie van het contract; alleen de onafhankelijke
/// database-uitvalmonitor (<c>DatabaseUitvalMonitorFunction</c>/<c>IDatabaseStatusReader</c>, #831)
/// is hier NIET vertaald — dat is een apart, niet-#972-gerelateerd monitoring-issue.
/// </summary>
public interface INoodmailThrottleStore
{
    /// <summary>
    /// Tijdstip (UTC) waarop voor deze sleutel voor het laatst een noodmail is geregistreerd, of
    /// <c>null</c> als er nog nooit een registratie voor deze sleutel is vastgelegd (of als een
    /// eerdere registratie inmiddels is gewist via <see cref="WisAsync"/>).
    /// </summary>
    Task<DateTime?> LaatsteKeerVerstuurdAsync(string sleutel);

    /// <summary>Registreert dat de noodmail voor deze sleutel zojuist is verstuurd.</summary>
    Task RegistreerVerstuurdAsync(string sleutel, DateTime verstuurdOpUtc);

    /// <summary>
    /// Wist de registratie voor deze sleutel (bijv. zodra de onderliggende storing hersteld is), zodat
    /// een volgende, nieuwe uitval weer als "nog niet gemeld" wordt behandeld.
    /// </summary>
    Task WisAsync(string sleutel);
}
