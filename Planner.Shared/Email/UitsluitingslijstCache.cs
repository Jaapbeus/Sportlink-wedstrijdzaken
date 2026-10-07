using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>Uitkomst van een poging om de uitsluitingslijst te verversen (#709).</summary>
public enum UitsluitingslijstStand
{
    /// <summary>Binnen de geldigheidsduur — er is niets uit de database gelezen.</summary>
    Actueel,

    /// <summary>Opnieuw uit de database gelezen; de lijst kan gewijzigd zijn.</summary>
    Ververst,

    /// <summary>Herladen mislukt, maar er is een eerdere lijst — die blijft gelden.</summary>
    VerouderdBehouden,

    /// <summary>Nooit geladen én nu niet te laden — er mag niet geclassificeerd worden.</summary>
    Ontbreekt
}

/// <summary>
/// In-memory kopie van de uitsluitingslijst met een geldigheidsduur (#709).
///
/// <para>
/// De lijst werd alleen bij een cold start en in fase 2 geladen. Fase 2 wordt niet bereikt zolang
/// elk bericht in de batch buiten scope valt, dus bleef de kopie in fase 1 verouderd: een adres dat
/// de beheerder net had uitgesloten kreeg terecht géén antwoord (de hercheck vóór de INSERT werkt
/// wel), maar de inhoud van het bericht was dan al naar de externe AI-provider gestuurd. Met een
/// geldigheidsduur wordt de lijst vóór de AI-stap vernieuwd, zonder bij elke poll de database te
/// wekken.
/// </para>
/// </summary>
public sealed class UitsluitingslijstCache
{
    /// <summary>
    /// Geldigheidsduur van de kopie. Bewust ruimer dan het poll-interval van 5 minuten: bij élke poll
    /// herladen zou de Azure SQL Serverless-database wakker houden voor batches die anders helemaal
    /// niet in de database terechtkomen. Vijftien minuten begrenst hoe lang een net uitgesloten adres
    /// nog een AI-call kan kosten, zonder die database-kosten.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    // volatile / Volatile.Read: meerdere invocaties lezen dezelfde statische instantie (#382).
    private volatile HashSet<string> _adressen = new(StringComparer.OrdinalIgnoreCase);
    private long _geladenOpTicksUtc;

    public IReadOnlySet<string> Adressen => _adressen;

    /// <summary>Is de lijst ooit met succes geladen? Zo niet, dan geldt fail-closed. (#423)</summary>
    public bool IsGeladen => Volatile.Read(ref _geladenOpTicksUtc) != 0;

    public bool IsVerouderd(DateTime nuUtc)
    {
        var ticks = Volatile.Read(ref _geladenOpTicksUtc);
        return ticks == 0 || nuUtc - new DateTime(ticks, DateTimeKind.Utc) >= Ttl;
    }

    public async Task<UitsluitingslijstStand> VerversIndienVerouderdAsync(
        Func<Task<HashSet<string>>> laadAsync, DateTime nuUtc, ILogger log)
    {
        if (!IsVerouderd(nuUtc))
            return UitsluitingslijstStand.Actueel;

        try
        {
            await HerlaadAsync(laadAsync, nuUtc);
            return UitsluitingslijstStand.Ververst;
        }
        catch (Exception ex)
        {
            if (!IsGeladen)
            {
                log.LogError(ex, "Uitsluitingslijst niet beschikbaar — AI-verwerking uitgesteld (fail-closed)");
                return UitsluitingslijstStand.Ontbreekt;
            }

            // Wél een eerdere lijst: doorgaan met die lijst is veiliger dan de verwerking stilzetten,
            // en het is precies het gedrag van vóór deze TTL.
            log.LogWarning(ex,
                "Uitsluitingslijst kon niet worden ververst — eerdere lijst met {Aantal} adressen blijft gelden",
                _adressen.Count);
            return UitsluitingslijstStand.VerouderdBehouden;
        }
    }

    /// <summary>
    /// Laadt de lijst onvoorwaardelijk opnieuw. Gebruikt door fase 2, waar de hercheck vóór de INSERT
    /// op een lijst uit déze invocatie moet gebeuren en niet op een kopie die tot de TTL oud kan zijn.
    /// </summary>
    public async Task HerlaadAsync(Func<Task<HashSet<string>>> laadAsync, DateTime nuUtc)
    {
        _adressen = await laadAsync();
        Volatile.Write(ref _geladenOpTicksUtc, nuUtc.Ticks);
    }
}
