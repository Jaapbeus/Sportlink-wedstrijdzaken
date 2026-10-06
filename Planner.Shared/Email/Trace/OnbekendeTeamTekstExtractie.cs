using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email.Trace;

/// <summary>Een teamtekst die de pipeline niet kon koppelen, klaar voor de wachtrij (#1568 deel C).</summary>
/// <param name="Genormaliseerd">Sleutel uit <see cref="TeamNaamNormalisatie"/>; dezelfde als de resolver gebruikt.</param>
/// <param name="Voorbeeld">De gesaneerde, afgekapte schrijfwijze zoals aangetroffen.</param>
public sealed record OnbekendeTeamTekstMelding(string Genormaliseerd, string Voorbeeld);

/// <summary>
/// Leidt uit een <see cref="BeslissingsTrace"/> af welke teamtekst onbekend bleef. Leest uitsluitend de
/// al gesaneerde trace (geen mailbody), dus de wachtrij bevat nooit meer dan de trace zelf.
/// </summary>
public static class OnbekendeTeamTekstExtractie
{
    public const int MaxGenormaliseerdLengte = 200;

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

            var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(tekst, clubCode);
            if (sleutel.Length == 0 || sleutel.Length > MaxGenormaliseerdLengte || !gezien.Add(sleutel)) continue;
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
