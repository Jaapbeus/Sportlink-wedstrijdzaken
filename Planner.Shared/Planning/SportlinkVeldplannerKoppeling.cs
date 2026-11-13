using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Shared.Planning;

/// <summary>De sleutels van een eigen regel waarmee hij aan een Sportlink-blok gekoppeld kan worden (#1915).</summary>
/// <param name="PublicMatchId">Sportlinks sleutel van de wedstrijd, als onze gegevens die (geverifieerd) kennen.</param>
/// <param name="Wedstrijdnummer">Het bondswedstrijdnummer; gelijk aan <see cref="SportlinkVeldplannerBlok.ExternalMatchId"/>.</param>
public readonly record struct EigenSleutel(string? PublicMatchId, long? Wedstrijdnummer);

/// <summary>
/// Koppelt de wedstrijden van onze Planning aan de blokken van de Sportlink-veldplanner (#1563, herschreven in #1915).
/// <para>
/// <b>Uitsluitend op sleutel, nooit op naam.</b> Een teamnaam is geen identiteit: Sportlink gebruikt in de dataservice de
/// sorteernaam van een club en in de veldplanner een kortere, en elke nieuwe afwijking gaf een dubbele regel. De koppeling
/// loopt daarom in twee rondes, beide op een ID dat Sportlink zelf uitgeeft:
/// <list type="number">
/// <item><c>PublicMatchId</c> — gelijk aan de sleutel van het blok;</item>
/// <item>voor wat overbleef: het bondswedstrijdnummer (<c>wedstrijdnummer</c> = <c>ExternalMatchId</c>), alleen als dat nummer
/// aan beide kanten precies één keer voorkomt (bij een clubwedstrijd is het vaak 1 en dus niet uniek).</item>
/// </list>
/// Elk blok koppelt aan hooguit één wedstrijd. Wat niet koppelt blijft zichtbaar als afwijking.
/// </para>
/// </summary>
public static class SportlinkVeldplannerKoppeling
{
    /// <returns>Per index in <paramref name="eigen"/> het bijbehorende Sportlink-blok; wedstrijden zonder koppeling
    /// ontbreken (de aanroeper markeert ze als afwijking).</returns>
    public static IReadOnlyDictionary<int, SportlinkVeldplannerBlok> Koppel(
        IReadOnlyList<EigenSleutel> eigen, IReadOnlyList<SportlinkVeldplannerBlok> blokken)
    {
        var resultaat = new Dictionary<int, SportlinkVeldplannerBlok>();
        var gebruikt = new HashSet<int>();

        // Ronde 1: PublicMatchId.
        var perPublicId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var j = 0; j < blokken.Count; j++)
            if (!string.IsNullOrWhiteSpace(blokken[j].PublicMatchId)) perPublicId[blokken[j].PublicMatchId!] = j;
        for (var i = 0; i < eigen.Count; i++)
        {
            var id = eigen[i].PublicMatchId;
            if (string.IsNullOrWhiteSpace(id) || !perPublicId.TryGetValue(id, out var j) || gebruikt.Contains(j)) continue;
            resultaat[i] = blokken[j];
            gebruikt.Add(j);
        }

        // Ronde 2: wedstrijdnummer = ExternalMatchId, alleen eenduidig aan beide kanten.
        var eigenPerNummer = eigen.Select((e, i) => (e.Wedstrijdnummer, Index: i))
            .Where(x => x.Wedstrijdnummer != null && !resultaat.ContainsKey(x.Index))
            .GroupBy(x => x.Wedstrijdnummer!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Index).ToList());
        var blokPerNummer = blokken.Select((b, j) => (b.ExternalMatchId, Index: j))
            .Where(x => x.ExternalMatchId != null && !gebruikt.Contains(x.Index))
            .GroupBy(x => x.ExternalMatchId!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Index).ToList());
        foreach (var (nummer, eigenIndices) in eigenPerNummer)
        {
            if (eigenIndices.Count != 1 || !blokPerNummer.TryGetValue(nummer, out var blokIndices) || blokIndices.Count != 1) continue;
            resultaat[eigenIndices[0]] = blokken[blokIndices[0]];
            gebruikt.Add(blokIndices[0]);
        }

        return resultaat;
    }

    /// <summary>Zijn deze twee ploegnamen dezelfde ploeg, op een extra clubvoorvoegsel na? Alleen voor de weergave van de tegenstander, nooit voor koppelen.</summary>
    internal static bool ZijnZelfdePloeg(string a, string b) => ZelfdeBehalveVoorvoegsel(Woorden(a), Woorden(b));

    /// <summary>
    /// De woorden van de kortste naam moeten het einde van de langste vormen: "v.v. Uit 35+2" past bij "Uit 35+2", maar
    /// "JO13-1" nooit bij "JO13-10". Categorie en teamnummer staan achteraan en moeten dus exact gelijk zijn.
    /// </summary>
    private static bool ZelfdeBehalveVoorvoegsel(IReadOnlyList<string> x, IReadOnlyList<string> y)
    {
        if (x.Count == 0 || y.Count == 0) return false;
        var (kort, lang) = x.Count <= y.Count ? (x, y) : (y, x);
        for (var i = 1; i <= kort.Count; i++)
            if (kort[^i] != lang[^i]) return false;
        return true;
    }

    private static IReadOnlyList<string> Woorden(string tekst)
        => tekst.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normaliseer).Where(w => w.Length > 0).ToList();

    /// <summary>Alleen letters en cijfers, kleine letters: "Voorbeeld '46 8" en "Voorbeeld 46 8" zijn dezelfde ploeg.</summary>
    public static string Normaliseer(string? tekst)
        => string.Concat((tekst ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
