using System.Globalization;
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
    /// Is <paramref name="datumdeel"/> (de eerste tien tekens van <c>his.matches.wedstrijddatum</c>, zoals
    /// de tier ze leest) een datum? Strikt en voor beide tiers dezelfde regel (Codex-review ronde 2 op
    /// #1698: gelijke selectie, niet raden): precies tien tekens <c>yyyy-MM-dd</c>, alleen ASCII-cijfers
    /// en koppeltekens op vaste plaatsen, en een bestaande kalenderdatum (jaar 0001–9999). Geen spaties,
    /// andere scheidingstekens, basisvorm zonder streepjes of Unicode-cijfers. Een conversie in SQL was
    /// ruimer en per tier anders (SQL Server's <c>TRY_CONVERT(date, ..., 23)</c> nam o.a. een lege tekst
    /// als 1900-01-01 en <c>2026/06/30</c> aan), daarom kiest deze functie en niet de database.
    /// </summary>
    public static bool IsGeldigDatumdeel(string? datumdeel, out DateOnly datum)
    {
        datum = default;
        if (datumdeel is not { Length: 10 }) return false;
        for (var i = 0; i < 10; i++)
        {
            var teken = datumdeel[i];
            var klopt = i is 4 or 7 ? teken == '-' : teken is >= '0' and <= '9';
            if (!klopt) return false;
        }
        return DateOnly.TryParseExact(datumdeel, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out datum);
    }

    /// <summary>
    /// Uit de datumdelen die de tier in <c>his.matches</c> aantreft de delen die een geldige datum
    /// (<see cref="IsGeldigDatumdeel"/>) tot en met <paramref name="grens"/> zijn; uniek en ordinaal
    /// gesorteerd. De tier wist daarna alleen rijen waarvan de eerste tien tekens binair gelijk zijn aan
    /// een gekozen deel.
    /// </summary>
    public static IReadOnlyList<string> SelecteerTeWissenDatumdelen(IEnumerable<string?> datumdelen, DateOnly grens)
        => datumdelen
            .Where(deel => IsGeldigDatumdeel(deel, out var datum) && datum <= grens)
            .Select(deel => deel!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// De wisstap voor <c>his.matches</c> op beide tiers: datumdelen lezen, hier kiezen
    /// (<see cref="SelecteerTeWissenDatumdelen"/>) en alleen bij een niet-lege keuze de tier laten wissen.
    /// </summary>
    /// <param name="leesDatumdelen">De verschillende eerste tien tekens van <c>wedstrijddatum</c> in rijen
    /// met nog een gevulde persoonskolom (geen andere kolom).</param>
    /// <param name="wis">Maakt de persoonskolommen leeg in rijen waarvan de eerste tien tekens binair gelijk
    /// zijn aan een van de gekozen delen; geeft het aantal geraakte rijen terug.</param>
    public static async Task<int> WisMatchesAsync(
        Func<Task<IReadOnlyList<string?>>> leesDatumdelen,
        Func<IReadOnlyList<string>, Task<int>> wis,
        DateOnly grens)
    {
        var teWissen = SelecteerTeWissenDatumdelen(await leesDatumdelen(), grens);
        return teWissen.Count == 0 ? 0 : await wis(teWissen);
    }

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
