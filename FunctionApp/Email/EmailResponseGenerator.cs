using System.Globalization;
using SportlinkFunction.Planner;

namespace SportlinkFunction.Email;

public static class EmailResponseGenerator
{
    private static readonly CultureInfo NL = new("nl-NL");

    // ── Beschikbaarheid ──

    public static (string onderwerp, string body) BouwBeschikbaarheidAntwoord(
        CheckAvailabilityResponse response,
        EmailClassificatie classificatie,
        InkomendEmail email)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        var datumTekst = FormatDatum(classificatie.Datum);

        string inhoud;

        if (response.Beschikbaar && response.Toewijzing != null)
        {
            var t = response.Toewijzing;
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst} is {t.VeldNaam} beschikbaar om {t.AanvangsTijd}. "
                   + $"De wedstrijd eindigt om {t.EindTijd}.";

            if (response.Waarschuwingen.Count > 0)
                inhoud += "\n\nLet op: " + string.Join(" ", response.Waarschuwingen);
        }
        else if (response.BeschikbareVensters?.Count > 0)
        {
            // Open vraag — toon beschikbare vensters
            var vensters = FilterKunstgrasVensters(response.BeschikbareVensters);
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst} zijn de volgende mogelijkheden:\n";
            foreach (var v in vensters)
            {
                var totTekst = IsEindeDag(v.Tot) ? "einde dag" : v.Tot;
                inhoud += $"- {v.VeldNaam}: beschikbaar van {v.Van} tot {totTekst}";
                if (!string.IsNullOrEmpty(v.Opmerking))
                    inhoud += $" ({v.Opmerking})";
                inhoud += "\n";
            }
            inhoud += "\nGeef een voorkeurstijd door, dan plannen we het in.";
        }
        else if (!response.Beschikbaar && response.Alternatieven.Count > 0)
        {
            var alternatieven = FilterAlternatieven(response.Alternatieven);
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst}";
            if (!string.IsNullOrEmpty(classificatie.AanvangsTijd))
                inhoud += $" om {classificatie.AanvangsTijd}";
            inhoud += " is helaas geen ruimte.";

            if (alternatieven.Count > 0)
            {
                inhoud += " Alternatieven:\n";
                foreach (var alt in alternatieven.Take(3))
                    inhoud += $"- {alt.VeldNaam} om {alt.AanvangsTijd} (eindigt {alt.EindTijd})\n";
            }

            if (response.Waarschuwingen.Count > 0)
                inhoud += "\nLet op: " + string.Join(" ", response.Waarschuwingen);
        }
        else
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst} is helaas geen veld beschikbaar.";
            if (!string.IsNullOrEmpty(response.Reden))
                inhoud += $" {response.Reden}";
        }

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email);
    }

    // ── Herplannen ──

    public static (string onderwerp, string body) BouwHerplanAntwoord(
        ZoekWedstrijdResponse? wedstrijd,
        HerplanCheckResponse? herplanOpties,
        EmailClassificatie classificatie,
        InkomendEmail email)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        string inhoud;

        if (wedstrijd == null)
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Er is geen wedstrijd gevonden voor {classificatie.TeamNaam ?? "het opgegeven team"} "
                   + $"op {FormatDatum(classificatie.Datum)}. "
                   + "Controleer de teamnaam en datum en probeer het opnieuw.";
        }
        else if (herplanOpties == null || herplanOpties.Alternatieven.Count == 0)
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"De wedstrijd {wedstrijd.Wedstrijd} op {FormatDatum(wedstrijd.Datum)} om {wedstrijd.AanvangsTijd} "
                   + $"op {wedstrijd.VeldNaam} kan helaas niet verplaatst worden. "
                   + "Er zijn geen alternatieven beschikbaar.";
        }
        else
        {
            // Filter: verwijder huidige slot en slots < 30 min verschil
            var huidigeAanvang = TimeOnly.TryParse(wedstrijd.AanvangsTijd, out var ha) ? ha : TimeOnly.MinValue;
            var zinvolleAlternatieven = herplanOpties.Alternatieven
                .Where(a => TimeOnly.TryParse(a.AanvangsTijd, out var at)
                    && Math.Abs((at.ToTimeSpan() - huidigeAanvang.ToTimeSpan()).TotalMinutes) >= 30)
                .ToList();

            var eerdere = zinvolleAlternatieven
                .Where(a => TimeOnly.Parse(a.AanvangsTijd) < huidigeAanvang)
                .ToList();
            eerdere = FilterAlternatieven(eerdere);

            var latere = zinvolleAlternatieven
                .Where(a => TimeOnly.Parse(a.AanvangsTijd) > huidigeAanvang)
                .ToList();
            latere = FilterAlternatieven(latere);

            if (eerdere.Count == 0 && latere.Count == 0)
            {
                inhoud = $"{aanhef} {voornaam},\n\n"
                       + $"De wedstrijd {wedstrijd.Wedstrijd} staat gepland op {FormatDatum(wedstrijd.Datum)} "
                       + $"om {wedstrijd.AanvangsTijd} op {wedstrijd.VeldNaam}. "
                       + "Het is een volle wedstrijddag en er zijn helaas geen zinvolle alternatieven beschikbaar.";
            }
            else
            {
                inhoud = $"{aanhef} {voornaam},\n\n"
                       + $"De wedstrijd {wedstrijd.Wedstrijd} staat gepland op {FormatDatum(wedstrijd.Datum)} "
                       + $"om {wedstrijd.AanvangsTijd} op {wedstrijd.VeldNaam}.\n";

                if (eerdere.Count > 0)
                {
                    inhoud += "\nEerdere mogelijkheden:\n";
                    foreach (var alt in eerdere.Take(3))
                        inhoud += $"- {alt.VeldNaam} om {alt.AanvangsTijd} (eindigt {alt.EindTijd})\n";
                }
                if (latere.Count > 0)
                {
                    inhoud += "\nLatere mogelijkheden:\n";
                    foreach (var alt in latere.Take(3))
                        inhoud += $"- {alt.VeldNaam} om {alt.AanvangsTijd} (eindigt {alt.EindTijd})\n";
                }

                inhoud += "\nLaat weten welke optie de voorkeur heeft.";
            }
        }

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email);
    }

    // ── Bevestiging ──

    public static (string onderwerp, string body) BouwBevestigingAntwoord(
        InkomendEmail email, EmailClassificatie classificatie)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + "Bedankt voor je bevestiging. Het verzoek is geregistreerd "
                   + "en wordt door de coördinator verwerkt.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email);
    }

    // ── Buiten scope ──

    public static (string onderwerp, string body) BouwBuitenScopeAntwoord(InkomendEmail email)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var classificatie = new EmailClassificatie { Type = VerzoekType.BuitenScope };
        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + "Bedankt voor je bericht. Dit verzoek vereist handmatige afhandeling "
                   + "en is ter beoordeling bij de coördinator neergelegd.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email);
    }

    // ── Fout ──

    public static (string onderwerp, string body) BouwFoutAntwoord(
        InkomendEmail email, EmailClassificatie classificatie)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + "Er is een fout opgetreden bij het verwerken van je verzoek. "
                   + "De coördinator is op de hoogte gesteld en neemt zo snel mogelijk contact op.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email);
    }

    // ── Helpers ──

    public static string GetTijdsgebondenAanhef()
    {
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var uur = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone).Hour;
        if (uur < 12) return "Goedemorgen";
        if (uur < 18) return "Goedemiddag";
        return "Goedenavond";
    }

    /// <summary>
    /// Filter veld 5 (grasveld) uit alternatieven als er 3+ kunstgrasvelden beschikbaar zijn.
    /// </summary>
    private static List<SlotToewijzing> FilterAlternatieven(List<SlotToewijzing> alternatieven)
    {
        var kunstgrasSlots = alternatieven.Where(a => a.VeldNummer >= 1 && a.VeldNummer <= 4).ToList();
        var kunstgrasVelden = kunstgrasSlots.Select(a => a.VeldNummer).Distinct().Count();

        // Als 3+ kunstgrasvelden beschikbaar zijn, laat grasvelden weg
        if (kunstgrasVelden >= 3)
            return kunstgrasSlots;

        return alternatieven;
    }

    /// <summary>
    /// Filter veld 5 uit beschikbare vensters als er 3+ kunstgrasvelden beschikbaar zijn.
    /// </summary>
    private static List<BeschikbaarVenster> FilterKunstgrasVensters(List<BeschikbaarVenster> vensters)
    {
        var kunstgrasVensters = vensters.Where(v => v.VeldNummer >= 1 && v.VeldNummer <= 4).ToList();
        if (kunstgrasVensters.Count >= 3)
            return kunstgrasVensters;
        return vensters;
    }

    /// <summary>
    /// Eindtijd >= 21:00 betekent effectief "einde dag" (sluittijd sportpark).
    /// </summary>
    private static bool IsEindeDag(string tot)
    {
        return TimeOnly.TryParse(tot, out var tijd) && tijd >= new TimeOnly(21, 0);
    }

    private static string FormatDatum(string? datumString)
    {
        if (DateOnly.TryParse(datumString, out var datum))
            return $"{datum.ToString("dddd d MMMM yyyy", NL)}";
        return datumString ?? "de opgegeven datum";
    }

    private static (string onderwerp, string body) WrapMetReviewEnHandtekening(
        string inhoud, EmailClassificatie classificatie, InkomendEmail email)
    {
        var onderwerp = $"Re: {email.Onderwerp}";
        var body = "";

        var reviewMode = Environment.GetEnvironmentVariable("EmailReviewMode");
        if (string.Equals(reviewMode, "true", StringComparison.OrdinalIgnoreCase))
        {
            body += $"=== REVIEW MODE ===\n"
                  + $"Originele afzender: {email.Afzender}\n"
                  + $"Onderwerp: {email.Onderwerp}\n"
                  + $"Classificatie: {classificatie.Type}\n"
                  + $"==================\n\n";
        }

        body += inhoud;
        body += "\n\n" + GetHandtekening();

        return (onderwerp, body);
    }

    private static string GetHandtekening()
    {
        var afzenderNaam = SystemUtilities.AppSettings.GetSetting("plannerAfzenderNaam") ?? "VRC Veldplanner";
        var coordinatorNaam = SystemUtilities.AppSettings.GetSetting("coordinatorNaam");
        var coordinatorFunctie = SystemUtilities.AppSettings.GetSetting("coordinatorFunctie") ?? "Coördinator thuiswedstrijden";

        var handtekening = $"Met vriendelijke groet,\n\n{afzenderNaam}";
        if (!string.IsNullOrEmpty(coordinatorNaam))
            handtekening += $"\nGeautomatiseerd antwoord namens {coordinatorNaam}";
        handtekening += $"\n{coordinatorFunctie}";

        return handtekening;
    }

    private static string ExtractVoornaam(string volledigeNaam)
    {
        if (string.IsNullOrWhiteSpace(volledigeNaam))
            return "";
        var delen = volledigeNaam.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return delen[0];
    }
}
