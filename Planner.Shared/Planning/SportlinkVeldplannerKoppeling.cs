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
        // Ronde 1: exact hetzelfde label. Ronde 2, alleen voor wat ronde 1 overliet: per ploeg mag de ene naam de
        // andere bevatten ("v.v. Uit 35+2" tegenover "Uit 35+2").
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

    /// <summary>Thuis- én uitploeg moeten gelijk zijn of de ene de andere bevatten.</summary>
    private static bool ZijnZelfdeWedstrijd(string a, string b)
    {
        var (aThuis, aUit) = Splits(a);
        var (bThuis, bUit) = Splits(b);
        return aUit != null && bUit != null && Bevat(aThuis, bThuis) && Bevat(aUit, bUit);
    }

    private static bool Bevat(string x, string y) => x.Length > 0 && y.Length > 0 && (x.Contains(y) || y.Contains(x));

    private static (string Thuis, string? Uit) Splits(string label)
    {
        var i = label.IndexOf(" - ", StringComparison.Ordinal);
        return i < 0 ? (Normaliseer(label), null) : (Normaliseer(label[..i]), Normaliseer(label[(i + 3)..]));
    }

    /// <summary>Alleen letters en cijfers, kleine letters: "Voorbeeld '46 8" en "Voorbeeld 46 8" zijn dezelfde ploeg.</summary>
    public static string Normaliseer(string? tekst)
        => string.Concat((tekst ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static int? Minuten(string? tijd)
        => TimeOnly.TryParse(tijd, out var t) ? t.Hour * 60 + t.Minute : null;
}
