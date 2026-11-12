using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace Planner.Endpoints.Health;

/// <summary>
/// #1911: gedeelde uitvoering van de leesproef van <c>/api/health</c> voor beide databasetiers. De tier levert alleen
/// de databasekant (de dialectspecifieke keuzequery en de productielookup); het oordeel — niet meten als de database
/// offline is, een begrensde looptijd, en een fout of timeout ⇒ <c>false</c> (degraded, falende deploy-smoke-test) —
/// staat hier eenmalig. Foutdetails gaan alleen naar het log, nooit in de publieke respons.
/// </summary>
public static class HealthLeesproef
{
    public static readonly TimeSpan StandaardTijdslimiet = TimeSpan.FromSeconds(5);

    /// <summary>
    /// De tier-onafhankelijke kern van een leesproef. Opent de verbinding, kiest met <paramref name="kiesClubSql"/> één
    /// club (bij voorkeur een met een ingevulde voorkeurstijd, want juist die loopt door de <c>time</c>-typemapping) en
    /// leest daarna de productielookup voor die club. <c>null</c> = geen rij om te lezen; een fout van de lookup wordt
    /// doorgegeven. De verbinding wordt hier vrijgegeven.
    /// </summary>
    public static async Task<bool?> LeesVoorEersteClubAsync(
        DbConnection verbinding, string kiesClubSql,
        Func<DbConnection, string, Task> leesLookup, CancellationToken ct)
    {
        await using (verbinding)
        {
            await verbinding.OpenAsync(ct);
            await using var kies = verbinding.CreateCommand();
            kies.CommandText = kiesClubSql;
            if (await kies.ExecuteScalarAsync(ct) is not string clubCode) return null;
            await leesLookup(verbinding, clubCode).WaitAsync(ct);
            return true;
        }
    }

    /// <returns><c>true</c> = leespad gelezen; <c>false</c> = fout of timeout; <c>null</c> = niet gemeten (database
    /// niet online, of de probe vond geen rij om te lezen).</returns>
    public static async Task<bool?> UitvoerenAsync(
        string databaseStatus, Func<CancellationToken, Task<bool?>> probe, ILogger log, TimeSpan? tijdslimiet = null)
    {
        if (databaseStatus != "online") return null;

        using var cts = new CancellationTokenSource(tijdslimiet ?? StandaardTijdslimiet);
        try
        {
            return await probe(cts.Token);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Health: leesproef van de planner-leespaden mislukt.");
            return false;
        }
    }
}
