using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email.Trace;

/// <summary>Een teamtekst die de pipeline niet kon koppelen, klaar voor de wachtrij (#1568 deel C).</summary>
/// <param name="Genormaliseerd">Sleutel uit <see cref="TeamNaamNormalisatie"/>; dezelfde als de resolver gebruikt.</param>
/// <param name="Voorbeeld">De gesaneerde, afgekapte schrijfwijze zoals aangetroffen.</param>
public sealed record OnbekendeTeamTekstMelding(string Genormaliseerd, string Voorbeeld);

/// <summary>
/// Leidt uit de VOLLEDIGE (transiënte) <see cref="BeslissingsTrace"/> af welke teamtekst onbekend bleef.
/// Dit is de enige permanente plek voor een ruwe teamschrijfwijze (wachtrij met begrensde retentie, 90 dagen
/// niet gezien → weg), en alleen als die er structureel uitziet als een teamlabel (<see cref="ZietEruitAlsTeamlabel"/>).
/// Vrije tekst die de AI als "team" teruggaf komt hier dus niet doorheen (Codex R1-F1).
/// </summary>
public static class OnbekendeTeamTekstExtractie
{
    public const int MaxGenormaliseerdLengte = 200;
    public const int MaxTeamlabelLengte = 24;

    /// <summary>
    /// Structurele vormguard (validatie, géén normalisatie): is dit zo kort en zo gebouwd als een teamlabel
    /// ("j10-04", "JO 13/2", "Ajax 13-2")? Kort (≤ <see cref="MaxTeamlabelLengte"/>), alleen letters, cijfers,
    /// spatie, streepje, slash, punt en plus ("35+1"), hoogstens twee tokens met letters en minstens één cijfer. Een zin of
    /// naam uit de mail voldoet daar niet aan. Restrisico: een enkel woord met een cijfer ("Jan 3") past wel.
    /// </summary>
    public static bool ZietEruitAlsTeamlabel(string? tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst)) return false;
        var t = tekst.Trim();
        if (t.Length > MaxTeamlabelLengte || !t.Any(char.IsDigit)) return false;
        if (!t.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '/' or '.' or '+')) return false;
        var tokens = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length <= 3 && tokens.Count(k => k.Any(char.IsLetter)) <= 2;
    }

    public static IReadOnlyList<OnbekendeTeamTekstMelding> Uit(BeslissingsTrace trace, string clubCode)
    {
        // Is de tegenstander wél als eigen team herkend (team en tegenstander verwisseld), dan was de
        // "teamtekst" gewoon een externe tegenstander: geen onbekend eigen team.
        var eigenTeamViaTegenstander = trace.Stappen.Any(s => s.Code == TraceCodes.TegenstanderHerkenning
            && s.Details.TryGetValue("canoniekeNaam", out var naam) && !string.IsNullOrWhiteSpace(naam));
        if (eigenTeamViaTegenstander) return [];

        var gezien = new HashSet<string>(StringComparer.Ordinal);
        var lijst = new List<OnbekendeTeamTekstMelding>();
        foreach (var stap in trace.Stappen.Where(s => s.Code == TraceCodes.TeamHerkenning))
        {
            if (!stap.Details.TryGetValue("bron", out var bron) || bron is not ("Onopgelost" or "MeerdereKandidaten")) continue;
            if (!stap.Details.TryGetValue("ruweTekst", out var tekst) || string.IsNullOrWhiteSpace(tekst)) continue;
            // Een gemaskeerde waarde ("[e-mail]", "[nummer]") is geen teamtekst maar persoonsdata die de sanering wegstreepte.
            if (tekst.Contains("[e-mail]") || tekst.Contains("[nummer]")) continue;

            if (!ZietEruitAlsTeamlabel(tekst)) continue;
            var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(tekst, clubCode);
            if (sleutel.Length == 0 || sleutel.Length > MaxGenormaliseerdLengte || !ZietEruitAlsTeamlabel(sleutel)
                || !gezien.Add(sleutel)) continue;
            lijst.Add(new OnbekendeTeamTekstMelding(sleutel, TraceBuilder.Saneer(tekst)));
        }
        return lijst;
    }
}

/// <summary>
/// De wachtrij is een hulpmiddel, geen onderdeel van de verwerking: een mislukte schrijfactie mag het
/// bericht nooit laten falen (zelfde garantie als <see cref="EmailTraceOpslag"/>).
/// </summary>
public static class OnbekendeTeamTekstOpslag
{
    public static Task BewaarVeiligAsync(
        BeslissingsTrace trace, string clubCode, int verwerkingId,
        Planner.Shared.Leren.IOnbekendeTeamTekstSchrijver schrijver, ILogger log)
        => EmailTraceOpslag.BewaarVeiligAsync(async () =>
        {
            foreach (var m in OnbekendeTeamTekstExtractie.Uit(trace, clubCode))
                await schrijver.VoegToeAsync(clubCode, m.Genormaliseerd, m.Voorbeeld, verwerkingId);
        }, ex => log.LogWarning("Onbekende teamtekst bewaren mislukt voor verwerking {Id} ({Fouttype})", verwerkingId, ex.GetType().Name));
}
