using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Shared.Planning;

/// <summary>
/// Koppelt de wedstrijden van onze Planning aan de blokken van de Sportlink-veldplanner (#1563).
/// Sleutel is het teamlabel "Thuis - Uit" (genormaliseerd), niet het wedstrijdnummer: bij clubwedstrijden is dat
/// vaak gewoon 1 en dus niet uniek. Bij meerdere blokken met hetzelfde label wint de dichtstbijzijnde starttijd,
/// en elk blok koppelt aan hooguit één wedstrijd.
/// </summary>
public static class SportlinkVeldplannerKoppeling
{
    /// <returns>Per index in <paramref name="eigen"/> het bijbehorende Sportlink-blok; wedstrijden zonder
    /// koppeling ontbreken (de aanroeper behoudt dan de eigen berekening).</returns>
    public static IReadOnlyDictionary<int, SportlinkVeldplannerBlok> Koppel(
        IReadOnlyList<(string Label, string? Starttijd)> eigen, IReadOnlyList<SportlinkVeldplannerBlok> blokken)
    {
        var resultaat = new Dictionary<int, SportlinkVeldplannerBlok>();
        var gebruikt = new HashSet<int>();
        // Ronde 1: exact hetzelfde label. Ronde 2, alleen voor wat ronde 1 overliet: per ploeg mag de ene naam een extra
        // clubvoorvoegsel hebben ("v.v. Uit 35+2" tegenover "Uit 35+2"); categorie en teamnummer blijven exact.
        Ronde(eigen, blokken, resultaat, gebruikt, (a, b) => Normaliseer(a) == Normaliseer(b));
        Ronde(eigen, blokken, resultaat, gebruikt, ZijnZelfdeWedstrijd);
        return resultaat;
    }

    private static void Ronde(
        IReadOnlyList<(string Label, string? Starttijd)> eigen, IReadOnlyList<SportlinkVeldplannerBlok> blokken,
        Dictionary<int, SportlinkVeldplannerBlok> resultaat, HashSet<int> gebruikt, Func<string, string, bool> gelijk)
    {
        for (var i = 0; i < eigen.Count; i++)
        {
            if (resultaat.ContainsKey(i)) continue;
            var eigenMinuten = Minuten(eigen[i].Starttijd);
            int? beste = null;
            var besteAfstand = int.MaxValue;
            for (var j = 0; j < blokken.Count; j++)
            {
                if (gebruikt.Contains(j) || !gelijk(eigen[i].Label, blokken[j].Label)) continue;
                var blokMinuten = Minuten(blokken[j].StartTijd);
                var afstand = eigenMinuten.HasValue && blokMinuten.HasValue ? Math.Abs(eigenMinuten.Value - blokMinuten.Value) : 0;
                if (afstand < besteAfstand) { beste = j; besteAfstand = afstand; }
            }
            if (beste == null) continue;
            gebruikt.Add(beste.Value);
            resultaat[i] = blokken[beste.Value];
        }
    }

    /// <summary>Zijn deze twee ploegnamen dezelfde ploeg, op een extra clubvoorvoegsel na? Categorie en teamnummer blijven exact.</summary>
    internal static bool ZijnZelfdePloeg(string a, string b) => ZelfdeBehalveVoorvoegsel(Woorden(a), Woorden(b));

    /// <summary>Thuis- én uitploeg moeten overeenkomen, waarbij het ene label hooguit een extra clubvoorvoegsel heeft.</summary>
    private static bool ZijnZelfdeWedstrijd(string a, string b)
    {
        var (aThuis, aUit) = Splits(a);
        var (bThuis, bUit) = Splits(b);
        return aUit != null && bUit != null && ZelfdeBehalveVoorvoegsel(aThuis, bThuis) && ZelfdeBehalveVoorvoegsel(aUit, bUit);
    }

    /// <summary>
    /// De woorden van de kortste naam moeten het einde van de langste vormen: "v.v. Uit 35+2" past bij "Uit 35+2", maar
    /// "JO13-1" nooit bij "JO13-10". Categorie en teamnummer staan achteraan en moeten dus exact gelijk zijn; bij twijfel
    /// koppelt de regel niet en behoudt de Planning de eigen gegevens.
    /// </summary>
    private static bool ZelfdeBehalveVoorvoegsel(IReadOnlyList<string> x, IReadOnlyList<string> y)
    {
        if (x.Count == 0 || y.Count == 0) return false;
        var (kort, lang) = x.Count <= y.Count ? (x, y) : (y, x);
        for (var i = 1; i <= kort.Count; i++)
            if (kort[^i] != lang[^i]) return false;
        return true;
    }

    private static (IReadOnlyList<string> Thuis, IReadOnlyList<string>? Uit) Splits(string label)
    {
        var i = label.IndexOf(" - ", StringComparison.Ordinal);
        return i < 0 ? (Woorden(label), null) : (Woorden(label[..i]), Woorden(label[(i + 3)..]));
    }

    private static IReadOnlyList<string> Woorden(string tekst)
        => tekst.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normaliseer).Where(w => w.Length > 0).ToList();

    /// <summary>Alleen letters en cijfers, kleine letters: "Voorbeeld '46 8" en "Voorbeeld 46 8" zijn dezelfde ploeg.</summary>
    public static string Normaliseer(string? tekst)
        => string.Concat((tekst ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static int? Minuten(string? tijd)
        => TimeOnly.TryParse(tijd, out var t) ? t.Hour * 60 + t.Minute : null;
}
