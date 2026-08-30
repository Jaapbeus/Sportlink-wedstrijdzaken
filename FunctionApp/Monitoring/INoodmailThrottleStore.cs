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
