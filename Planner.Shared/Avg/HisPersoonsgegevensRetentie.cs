using Microsoft.Extensions.Logging;

namespace Planner.Shared.Avg;

/// <summary>Uitkomst van één retentierun: de gebruikte grens (<c>null</c> = geen afgerond seizoen) en
/// het aantal rijen waarvan de persoonskolommen zijn leeggemaakt.</summary>
public sealed record HisRetentieResultaat(DateOnly? Grens, int MatchesGewist, int MatchDetailsGewist);

/// <summary>
/// De tier-eigen kant van de his-retentie: alleen de query's, elk over een eigen verbinding.
/// Implementaties: <c>Database.Postgres/PostgresHisRetentie</c> en
/// <c>FunctionApp/Admin/SqlServerHisRetentie</c>.
/// </summary>
public interface IHisRetentieOpslag
{
    /// <summary>Alle seizoenseinddatums uit de seizoenstabel van de tier.</summary>
    Task<IReadOnlyList<DateOnly>> LeesSeizoenseindenAsync();

    /// <summary>Maakt de persoonskolommen leeg voor wedstrijden tot en met <paramref name="grens"/>;
    /// geeft uitsluitend het aantal geraakte rijen per tabel terug.</summary>
    Task<(int Matches, int MatchDetails)> WisTotEnMetAsync(DateOnly grens);
}

/// <summary>
/// Seizoensretentie voor de persoonskolommen in <c>his.matches</c> en <c>his.matchdetails</c> (#1698,
/// eigenaarsbesluit 2026-10-12): bewaren tot het einde van het seizoen waarin de wedstrijd valt,
/// daarna leegmaken. Sportlink blijft de bron; de rij zelf (wedstrijd, teams, uitslag) blijft staan.
/// <para>
/// Tier-onafhankelijk: hier staan de regel (welke grens, welke kolommen) en de orkestratie met
/// geaggregeerde logging. Per tier blijft alleen de query: seizoenseinden lezen en een
/// <c>UPDATE ... SET kolom = NULL</c> voor wedstrijden tot en met de grens.
/// </para>
/// <para>
/// <b>Waarom een dagelijkse run en geen eenmalige per seizoen.</b> Het standaard sync-venster begint
/// een week terug, en een reset-sync haalt een heel oud seizoen opnieuw op. Beide vullen de kolommen
/// van een afgelopen seizoen weer; een dagelijkse, idempotente run maakt ze de volgende ochtend
/// opnieuw leeg.
/// </para>
/// </summary>
public static class HisPersoonsgegevensRetentie
{
    /// <summary>Persoonskolommen in <c>his.matches</c>: officials en rijders.</summary>
    public static IReadOnlyList<string> MatchesKolommen { get; } =
        ["scheidsrechters", "scheidsrechter", "rijders"];

    /// <summary>
    /// Persoonskolommen in <c>his.matchdetails</c>: rijder, vrije opmerkingen (kunnen namen bevatten),
    /// officials met hun relatiecode, en de contacttelefoon/-e-mail van accommodatie en teams (vaak
    /// een privénummer of -adres van een contactpersoon).
    /// </summary>
    public static IReadOnlyList<string> MatchDetailsKolommen { get; } =
    [
        "rijder", "opmerkingen",
        "verenigingscheidsrechtercode", "verenigingscheidsrechter",
        "overigeofficialcode", "overigeofficial", "scheidsrechters",
        "accommodatietelefoon",
        "thuisteamtelefoon", "thuisteamemail",
        "uitteamtelefoon", "uitteamemail",
    ];

    /// <summary>
    /// De laatste seizoenseinddatum die vóór <paramref name="vandaag"/> ligt, of <c>null</c> als er geen
    /// afgerond seizoen is. Wedstrijden tot en met deze datum vallen in een afgerond seizoen, mits de
    /// seizoenen aaneensluiten (zo legt de seizoensconfiguratie ze aan). Een seizoen dat vandaag
    /// eindigt, telt nog niet als afgerond.
    /// </summary>
    public static DateOnly? BepaalGrens(IEnumerable<DateOnly> seizoenseinden, DateOnly vandaag)
    {
        DateOnly? grens = null;
        foreach (var einde in seizoenseinden)
        {
            if (einde < vandaag && (grens is null || einde > grens))
                grens = einde;
        }
        return grens;
    }

    /// <summary>
    /// De stap in de dagelijkse AVG-retentierun van beide tiers (timer <c>CleanupFeedback</c>, 05:15
    /// UTC; orkestratie in <c>Planner.Endpoints/Avg/DagelijkseAvgRetentie</c>): <see cref="VoerUitAsync"/>
    /// met de datum van vandaag (om 05:15 UTC gelijk aan de Nederlandse datum), en een fout loggen en
    /// doorgooien zodat de Functions-host hem als mislukte run ziet. De tier levert alleen de query's.
    /// Geen eigen timer: dat zou het timercontract op beide tiers woordelijk herhalen
    /// (tier-duplicatie, eigenaarsbesluit 2026-10-13).
    /// </summary>
    public static async Task<HisRetentieResultaat> VoerStapUitAsync(
        IHisRetentieOpslag opslag, ILogger log, DateOnly? vandaag = null)
    {
        try
        {
            return await VoerUitAsync(
                opslag.LeesSeizoenseindenAsync, opslag.WisTotEnMetAsync,
                log, vandaag ?? DateOnly.FromDateTime(DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup his-persoonskolommen mislukt");
            throw;
        }
    }

    /// <summary>
    /// Eén run: seizoenseinden lezen, grens bepalen en, alleen als er een grens is, de wisstap van de
    /// tier aanroepen. Logt alleen datum en aantallen, nooit een waarde uit een rij (Laag 5).
    /// </summary>
    /// <param name="leesSeizoenseinden">Seizoenseinddatums uit de seizoenstabel van de tier.</param>
    /// <param name="wisTotEnMet">Maakt de persoonskolommen leeg voor wedstrijden tot en met de grens;
    /// geeft het aantal geraakte rijen per tabel terug.</param>
    public static async Task<HisRetentieResultaat> VoerUitAsync(
        Func<Task<IReadOnlyList<DateOnly>>> leesSeizoenseinden,
        Func<DateOnly, Task<(int Matches, int MatchDetails)>> wisTotEnMet,
        ILogger log,
        DateOnly vandaag)
    {
        var grens = BepaalGrens(await leesSeizoenseinden(), vandaag);
        if (grens is not DateOnly g)
        {
            log.LogInformation("His-retentie: geen afgerond seizoen gevonden, niets leeggemaakt");
            return new HisRetentieResultaat(null, 0, 0);
        }

        var (matches, matchDetails) = await wisTotEnMet(g);
        log.LogInformation(
            "His-retentie klaar: grens {Grens:yyyy-MM-dd}, persoonskolommen leeggemaakt in {Matches} his.matches- en {MatchDetails} his.matchdetails-rij(en)",
            g, matches, matchDetails);
        return new HisRetentieResultaat(g, matches, matchDetails);
    }
}
