using System.Text.RegularExpressions;

namespace Planner.Shared.Email;

/// <summary>
/// Pure, tier-onafhankelijke tekstheuristieken van de e-mailpipeline. Verhuisd uit beide
/// <c>BerichtPipeline.cs</c>-bestanden (#1568): ze stonden daar woordelijk twee keer.
/// </summary>
public static class BerichtTekstHeuristiek
{
    /// <summary>True als het onderwerp met een antwoord-/doorstuurvoorvoegsel begint (Re:, Fw:, Aw:).</summary>
    public static bool HeeftReplyPrefix(string? onderwerp)
        => !string.IsNullOrWhiteSpace(onderwerp)
           && Regex.IsMatch(onderwerp, @"^\s*(?:(?:re|fw|fwd|aw)\s*:\s*)+", RegexOptions.IgnoreCase);

    /// <summary>
    /// Een maandnaam zonder jaartal betekent "het eerstvolgende voorkomen". Zonder deze regel
    /// levert "10 januari" in een mail van half december een datum van elf maanden terug op,
    /// waarop de afzender automatisch "datum moet in de toekomst zijn" terugkrijgt.
    /// Een datum die nog maar kort geleden is, blijft in het huidige jaar: dat is vaker een
    /// verwijzing naar het recente verleden dan naar volgend jaar.
    /// </summary>
    public static DateOnly? EerstvolgendVoorkomen(int dag, int maand)
    {
        const int verledenTolerantieDagen = 30;
        var ondergrens = DateOnly.FromDateTime(DateTime.Today).AddDays(-verledenTolerantieDagen);

        for (int jaarOffset = 0; jaarOffset <= 1; jaarOffset++)
        {
            DateOnly kandidaat;
            try { kandidaat = new DateOnly(DateTime.Today.Year + jaarOffset, maand, dag); }
            catch { continue; }
            if (kandidaat >= ondergrens) return kandidaat;
        }
        return null;
    }

    /// <summary>"vervroegen" of "verlaten" als de tekst eenduidig één richting noemt, anders <c>null</c>.</summary>
    public static string? DetecteerRichting(string? onderwerp, string? body)
    {
        var tekst = ((onderwerp ?? "") + " " + (body ?? "")).ToLowerInvariant();
        bool vervroegen = tekst.Contains("vervroeg") || tekst.Contains("eerder")
                       || tekst.Contains("naar voren");
        bool verlaten = tekst.Contains("verlaat") || tekst.Contains("verlat")
                     || tekst.Contains(" later") || tekst.Contains("naar achter");
        if (vervroegen && !verlaten) return "vervroegen";
        if (verlaten && !vervroegen) return "verlaten";
        return null;
    }
}
