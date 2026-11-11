using System.Text.RegularExpressions;

namespace Planner.Shared.Email;

/// <summary>
/// Datumherkenning in de tekst van een inkomend bericht: de expliciete datum in onderwerp of eigen
/// tekst, de dagnaamcorrectie van de modeldatum, de "doordeweeks"-uitbreiding en het afknippen van
/// de citaat-/ondertekeningsstaart. Pure tekstlogica zonder database, Graph of AI; verhuisd uit
/// beide <c>BerichtPipeline.cs</c>-bestanden, waar ze woordelijk twee keer stonden (#1716, eerste
/// slice van de e-mailkern naar <c>Planner.Shared</c>).
/// </summary>
public static class BerichtDatumHeuristiek
{
    /// <summary>Apply a date correction only to the date role used by this request type.</summary>
    public static void ValideerClassificatieDatum(BerichtClassificatie classificatie, string emailBody, string onderwerp)
    {
        // A reschedule has two date roles. A subject date can be administrative or desired;
        // neither is evidence that the model's current match date is wrong. Missing dates
        // still use the existing explicit-text fallback without changing GewensteDatum.
        if (classificatie.Type == VerzoekType.HerplanVerzoek && !string.IsNullOrEmpty(classificatie.Datum))
            return;

        var vorigeDatum = classificatie.Datum;
        var nieuweDatum = CorrigeerDagDatum(vorigeDatum, emailBody, onderwerp);
        if (classificatie.Datums is { Count: 1 } datums && datums[0] == vorigeDatum && nieuweDatum is not null)
            datums[0] = nieuweDatum;
        classificatie.Datum = nieuweDatum;
    }

    private static readonly (string Naam, DayOfWeek Dag)[] DagNamen =
    {
        ("maandag", DayOfWeek.Monday), ("dinsdag", DayOfWeek.Tuesday),
        ("woensdag", DayOfWeek.Wednesday), ("donderdag", DayOfWeek.Thursday),
        ("vrijdag", DayOfWeek.Friday), ("zaterdag", DayOfWeek.Saturday),
        ("zondag", DayOfWeek.Sunday),
    };

    private static readonly string[] CitaatMarkers =
    {
        @"\bvan\s*:",
        @"\bverzonden\s*:",
        @"\bfrom\s*:",
        @"\bsent\s*:",
        @"-{2,}\s*original",
        @"\bop\b[^\n]{0,120}?\bschreef\b",
    };

    /// <summary>
    /// De datum (<c>jjjj-MM-dd</c>) die bij dit bericht hoort, gegeven de datum die het model vond.
    /// Prioriteit: expliciete datum in het onderwerp &gt; expliciete datum in de eigen tekst (alleen als
    /// het model geen datum had) &gt; modeldatum met dagnaamcorrectie. In een reply-thread wint de
    /// modeldatum boven de (oude) datum in het onderwerp. Geeft <paramref name="modelDatum"/>
    /// ongewijzigd terug als er niets te corrigeren valt.
    /// </summary>
    public static string? CorrigeerDagDatum(string? modelDatum, string emailBody, string onderwerp)
    {
        // De citaat-/ondertekeningsstaart bevat datums en dagnamen van eerdere berichten
        // ("Verzonden: dinsdag 26 mei 2026"); die horen niet bij dit verzoek.
        var eigenTekst = StripCitaatEnOndertekening(emailBody);

        var onderwerpDatum = ExtractExpliciteDatum(onderwerp);
        // Bij een reply/forward staat in het onderwerp de datum van de oorspronkelijke vraag.
        // Heeft het model uit de nieuwe tekst een datum gehaald, dan is die actueler.
        var onderwerpMagWinnen = string.IsNullOrEmpty(modelDatum) || !BerichtTekstHeuristiek.HeeftReplyPrefix(onderwerp);
        if (onderwerpDatum.HasValue && onderwerpMagWinnen)
            return onderwerpDatum.Value.ToString("yyyy-MM-dd");

        var bodyDatum = ExtractExpliciteDatum(eigenTekst);
        if (bodyDatum.HasValue && string.IsNullOrEmpty(modelDatum))
            return bodyDatum.Value.ToString("yyyy-MM-dd");

        if (string.IsNullOrEmpty(modelDatum)) return modelDatum;
        if (!ModelDatum.TryParse(modelDatum, out var datum)) return modelDatum;

        // Alleen corrigeren bij precies één dagnaam. Bij meerdere ("zaterdag of zondag?", of een
        // ondertekening met "kantine open vrijdag") is niet vast te stellen welke dag bij het
        // verzoek hoort; dan is de modeldatum ongewijzigd laten beter dan gokken.
        var tekst = (onderwerp + " " + eigenTekst).ToLowerInvariant();
        var gevondenDagen = DagNamen
            .Where(d => tekst.Contains(d.Naam))
            .Select(d => d.Dag)
            .Distinct()
            .ToList();
        if (gevondenDagen.Count != 1) return modelDatum;

        var doelDag = gevondenDagen[0];
        if (datum.DayOfWeek == doelDag) return modelDatum;

        // A lone weekday elsewhere in the mail can describe a meeting, not this match.
        // Correct only when the weekday participates in a direct scheduling request.
        var dagNaam = DagNamen.Single(d => d.Dag == doelDag).Naam;
        if (!HeeftExplicietDagnaamVerzoek(tekst, dagNaam)) return modelDatum;

        for (int offset = 1; offset <= 7; offset++)
        {
            if (datum.AddDays(-offset).DayOfWeek == doelDag)
                return datum.AddDays(-offset).ToString("yyyy-MM-dd");
            if (datum.AddDays(offset).DayOfWeek == doelDag)
                return datum.AddDays(offset).ToString("yyyy-MM-dd");
        }
        return modelDatum;
    }

    private static bool HeeftExplicietDagnaamVerzoek(string tekst, string dagNaam)
    {
        var dag = Regex.Escape(dagNaam);
        return Regex.IsMatch(tekst, $@"\b(?:kunnen\s+(?:we|jullie)|zouden\s+we|kan\s+het|graag|ja)\s+{dag}\b") ||
               Regex.IsMatch(tekst, $@"\b{dag}\s+(?:spelen|inhalen|verzetten|verplaatsen|beschikbaar)\b");
    }

    /// <summary>
    /// Als de berichttekst 'doordeweeks' bevat, vervang de modeldatums door de exacte
    /// maandag t/m donderdag van de week die het model afleidde. Vrijdag is nooit doordeweeks.
    /// Een concreet gevraagde datum blijft altijd staan en datums in het verleden vallen af.
    /// </summary>
    public static List<string> ExpandDoordeweeksDatums(List<string> aiDatums, string onderwerp, string body)
    {
        var eigenBody = StripCitaatEnOndertekening(body);
        var tekst = (onderwerp + " " + eigenBody).ToLowerInvariant();
        if (!tekst.Contains("doordeweeks"))
            return aiDatums;

        // "doordeweeks, bijvoorbeeld woensdag 13 mei" is een concreet verzoek — dat mag niet door
        // vier andere dagen worden vervangen.
        if (ExtractExpliciteDatum(onderwerp).HasValue || ExtractExpliciteDatum(eigenBody).HasValue)
            return aiDatums;

        // Leid de weekmaandag af: óf uit de eerste modeldatum, óf uit "volgende week".
        DateOnly weekStart;
        if (aiDatums.Count > 0 && ModelDatum.TryParse(aiDatums[0], out var firstDate))
        {
            int daysFromMonday = ((int)firstDate.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            weekStart = firstDate.AddDays(-daysFromMonday);
        }
        else
        {
            var today = NederlandseBedrijfsKlok.Vandaag();
            int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
            if (daysUntilMonday == 0) daysUntilMonday = 7;
            weekStart = today.AddDays(daysUntilMonday);
        }

        // Maandag t/m donderdag (4 dagen), zonder de dagen die al voorbij zijn — anders krijgt de
        // afzender voor elke verstreken dag een "datum moet in de toekomst zijn"-antwoord.
        var vandaag = NederlandseBedrijfsKlok.Vandaag();
        return Enumerable.Range(0, 4)
            .Select(i => weekStart.AddDays(i))
            .Where(d => d > vandaag)
            .Select(d => d.ToString("yyyy-MM-dd"))
            .ToList();
    }

    /// <summary>
    /// Knipt de tekst af bij de eerste citaat- of doorstuurkop, zodat alleen de eigen tekst van de
    /// afzender wordt geanalyseerd. Blijft er niets over (het bericht is volledig een citaat), dan
    /// is de originele tekst het enige beschikbare materiaal en wordt die teruggegeven.
    /// </summary>
    public static string StripCitaatEnOndertekening(string tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst)) return tekst ?? "";

        var eerstePositie = -1;
        foreach (var marker in CitaatMarkers)
        {
            var match = Regex.Match(tekst, marker, RegexOptions.IgnoreCase);
            if (match.Success && (eerstePositie < 0 || match.Index < eerstePositie))
                eerstePositie = match.Index;
        }

        if (eerstePositie < 0) return tekst;

        var eigenTekst = tekst[..eerstePositie];
        return string.IsNullOrWhiteSpace(eigenTekst) ? tekst : eigenTekst;
    }

    private static DateOnly? ExtractExpliciteDatum(string tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst)) return null;

        var numericMatch = Regex.Match(tekst, @"(\d{1,2})-(\d{1,2})-(\d{4})");
        if (numericMatch.Success)
        {
            if (int.TryParse(numericMatch.Groups[1].Value, out var dag) &&
                int.TryParse(numericMatch.Groups[2].Value, out var maand) &&
                int.TryParse(numericMatch.Groups[3].Value, out var jaar))
            {
                try { return new DateOnly(jaar, maand, dag); } catch { }
            }
        }

        // Slash-notatie ("14/02/2026", "-19/1"): net zo vaak waargenomen als de streepjesvorm
        // (#722-analyse), maar dd-mm zónder jaar via '/' wordt bewust NIET los ondersteund omdat
        // die vorm ambigu is met teamnotatie ("13/1"); mét expliciet jaartal is dat onderscheid er wel.
        var slashMatchMetJaar = Regex.Match(tekst, @"(?<![\d/])(\d{1,2})/(\d{1,2})/(\d{4})(?!\d)");
        if (slashMatchMetJaar.Success)
        {
            if (int.TryParse(slashMatchMetJaar.Groups[1].Value, out var dag) &&
                int.TryParse(slashMatchMetJaar.Groups[2].Value, out var maand) &&
                int.TryParse(slashMatchMetJaar.Groups[3].Value, out var jaar))
            {
                try { return new DateOnly(jaar, maand, dag); } catch { }
            }
        }

        return ExtractMaandnaamDatum(tekst);
    }

    private static DateOnly? ExtractMaandnaamDatum(string tekst)
    {
        var maandNamen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["januari"] = 1, ["februari"] = 2, ["maart"] = 3, ["april"] = 4,
            ["mei"] = 5, ["juni"] = 6, ["juli"] = 7, ["augustus"] = 8,
            ["september"] = 9, ["oktober"] = 10, ["november"] = 11, ["december"] = 12,
            // Afgekorte vormen (#722-analyse: 117 waargenomen buiten citaat-/ondertekeningstekst,
            // bijv. "22 aug", "24 mrt.") — "mei" is al gelijk aan de volledige vorm.
            ["jan"] = 1, ["feb"] = 2, ["mrt"] = 3, ["apr"] = 4,
            ["jun"] = 6, ["jul"] = 7, ["aug"] = 8,
            ["sep"] = 9, ["sept"] = 9, ["okt"] = 10, ["nov"] = 11, ["dec"] = 12
        };

        var tekstLower = tekst.ToLowerInvariant();
        foreach (var (naam, maandNr) in maandNamen)
        {
            var maandMatch = Regex.Match(tekstLower, $@"(\d{{1,2}})\s+{naam}\b\.?(?:\s+(\d{{4}}))?");
            if (maandMatch.Success && int.TryParse(maandMatch.Groups[1].Value, out var d))
            {
                if (maandMatch.Groups[2].Success && int.TryParse(maandMatch.Groups[2].Value, out var expliciteJaar))
                {
                    try { return new DateOnly(expliciteJaar, maandNr, d); } catch { }
                }
                else
                {
                    var kandidaat = BerichtTekstHeuristiek.EerstvolgendVoorkomen(d, maandNr);
                    if (kandidaat.HasValue) return kandidaat;
                }
            }
        }

        return null;
    }
}
