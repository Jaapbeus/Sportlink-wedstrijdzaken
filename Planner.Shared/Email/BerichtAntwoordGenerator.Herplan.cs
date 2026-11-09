namespace Planner.Shared.Email;

// Herplan-antwoorden van BerichtAntwoordGenerator (#1758): verplaatsen, te laat, verzet zonder datum en
// herplannen naar een gewenste datum. Zie BerichtAntwoordGenerator.cs voor de poorten en de helpers.
public static partial class BerichtAntwoordGenerator
{
    // ── Herplannen ──

    public static (string onderwerp, string body) BouwHerplanAntwoord(
        IAntwoordWedstrijd? wedstrijd,
        IAntwoordHerplanOpties? herplanOpties,
        BerichtClassificatie classificatie,
        IAntwoordAfzender email,
        IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        string inhoud;

        if (wedstrijd == null)
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Er is geen wedstrijd gevonden voor {AiVeldSanering.TeamNaamWeergave(classificatie.TeamNaam, classificatie.TeamNaamOnbevestigd) ?? "het opgegeven team"} "
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
                    inhoud += FormatAlternatievenLijst(eerdere);
                }
                if (latere.Count > 0)
                {
                    inhoud += "\nLatere mogelijkheden:\n";
                    inhoud += FormatAlternatievenLijst(latere);
                }

                inhoud += "\nLaat weten welke optie de voorkeur heeft.";
            }
        }

        if (!string.IsNullOrWhiteSpace(classificatie.KnvbNotitie))
            inhoud += $"\n\nLet op: {classificatie.KnvbNotitie} Zie ook: https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/regelen-dagelijkse-praktijk/verplaatsen-van-wedstrijden";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    // ── Herplannen — te laat (verzoek binnen deadline) ──

    public static (string onderwerp, string body) BouwHerplanTeLaatAntwoord(
        IAntwoordWedstrijd? wedstrijd,
        int deadlineDagen,
        int dagenTotWedstrijd,
        BerichtClassificatie classificatie,
        IAntwoordAfzender email,
        IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        string inhoud;

        if (wedstrijd == null)
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Je herplanverzoek is helaas te laat ingediend. Volgens onze richtlijn moet een herplanverzoek "
                   + $"minimaal {deadlineDagen} dagen voor de wedstrijd worden ingediend.";
        }
        else
        {
            var datumTekst = FormatDatum(wedstrijd.Datum);
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"De wedstrijd {wedstrijd.Wedstrijd} staat gepland op {datumTekst} om {wedstrijd.AanvangsTijd} "
                   + $"op {wedstrijd.VeldNaam}. Dat is over {dagenTotWedstrijd} dag(en).\n\n"
                   + $"Volgens onze richtlijn moet een herplanverzoek minimaal {deadlineDagen} dagen voor de wedstrijd "
                   + $"worden ingediend. Omdat de wedstrijd al binnen die termijn valt, kunnen we het verzoek niet meer "
                   + $"automatisch verwerken.\n\n"
                   + $"Neem voor uitzonderingen rechtstreeks contact op met de coördinator.";
        }

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    // ── Herplannen — verzoek van tegenstander zonder concrete datum (#561/#1141) ──

    /// <summary>
    /// Bouwt het antwoord op een herplanverzoek van de tegenstander zonder concrete nieuwe datum.
    /// Zegt bewust geen nieuwe datum toe — dat wordt afgestemd met de begeleiding van ons eigen
    /// team (die in BCC staat, zie <c>EmailReplyPolicyService</c>) — maar geeft als
    /// voorzet een paar zaterdagen waarop ons team volgens het huidige programma nog vrij is.
    /// </summary>
    public static (string onderwerp, string body) BouwVerzetZonderDatumAntwoord(
        IAntwoordWedstrijd? wedstrijd,
        List<string> vrijeZaterdagen,
        BerichtClassificatie classificatie,
        IAntwoordAfzender email,
        IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        string inhoud;

        if (wedstrijd == null)
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Er is geen wedstrijd gevonden voor {AiVeldSanering.TeamNaamWeergave(classificatie.TeamNaam, classificatie.TeamNaamOnbevestigd) ?? "het opgegeven team"} "
                   + $"op {FormatDatum(classificatie.Datum)}.";
        }
        else
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Bedankt voor je verzoek om de wedstrijd {wedstrijd.Wedstrijd} op {FormatDatum(wedstrijd.Datum)} "
                   + $"om {wedstrijd.AanvangsTijd} te verzetten.\n\n"
                   + "We kunnen op dit moment nog geen nieuwe datum toezeggen: dat stemmen we eerst af met de "
                   + "begeleiding van ons eigen team. Zij zijn in de kopie (BCC) van dit bericht meegenomen, zodat "
                   + "jullie dit onderling verder kunnen afstemmen.";

            inhoud += "\n\n" + VrijeZaterdagenTekst(vrijeZaterdagen);
        }

        if (classificatie.VoegKnvbPdfBijlageToe)
            inhoud += "\n\n" + KnvbBijlageZin;

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    private const string KnvbBijlageZin = "De KNVB-speeldagenkalender is als bijlage toegevoegd.";

    /// <summary>De voorzet met de vrije zaterdagen, of de zin dat er geen speelvrije zaterdagen zijn (#561). Ook de placeholder <c>vrije_zaterdagen</c> (#1780).</summary>
    private static string VrijeZaterdagenTekst(IReadOnlyList<string> vrijeZaterdagen)
    {
        if (vrijeZaterdagen.Count == 0)
            return "Op korte termijn hebben we geen speelvrije zaterdagen kunnen vinden binnen de "
                 + "gebruikelijke termijn. Bekijk de bijgevoegde speeldagenkalender voor het volledige overzicht.";

        var tekst = "Als voorzet: volgens ons huidige programma hebben we op de volgende zaterdagen nog "
                  + "geen wedstrijd:\n";
        foreach (var datum in vrijeZaterdagen)
            tekst += $"- {FormatDatum(datum)}\n";
        return tekst;
    }

    // ── Herplannen naar gewenste datum ──

    public static (string onderwerp, string body) BouwHerplanGewensteDatumAntwoord(
        IAntwoordWedstrijd? wedstrijd,
        string? gewensteDatum,
        IAntwoordBeschikbaarheid? beschikbaarheid,
        BerichtClassificatie classificatie,
        IAntwoordAfzender email,
        IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        var gewenstDatumTekst = FormatDatum(gewensteDatum);
        string inhoud;

        if (wedstrijd == null)
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Er is geen wedstrijd gevonden voor {AiVeldSanering.TeamNaamWeergave(classificatie.TeamNaam, classificatie.TeamNaamOnbevestigd) ?? "het opgegeven team"} "
                   + $"op {FormatDatum(classificatie.Datum)}.";
        }
        else
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"De wedstrijd {wedstrijd.Wedstrijd} staat momenteel gepland op {FormatDatum(wedstrijd.Datum)} om {wedstrijd.AanvangsTijd} op {wedstrijd.VeldNaam}.\n\n"
                   + GewensteDatumMogelijkheden(gewenstDatumTekst, beschikbaarheid);
        }

        inhoud = DagdeelVenster.VoegControleZinToe(inhoud, beschikbaarheid?.GecontroleerdDagdeel);

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    /// <summary>Wat er op de gewenste datum kan: geen ruimte, de vrije vensters, de toegewezen slot of "ruimte beschikbaar". Ook de placeholder <c>mogelijkheden</c> (#1780).</summary>
    private static string GewensteDatumMogelijkheden(string gewenstDatumTekst, IAntwoordBeschikbaarheid? beschikbaarheid)
    {
        if (beschikbaarheid == null || !beschikbaarheid.Beschikbaar)
            return DagdeelVenster.GeenRuimteOpGewensteDatum(gewenstDatumTekst, beschikbaarheid?.GecontroleerdDagdeel, beschikbaarheid?.Reden);

        if (beschikbaarheid.BeschikbareVensters?.Count > 0)
        {
            var tekst = $"Op {gewenstDatumTekst} zijn de volgende mogelijkheden:\n";
            foreach (var v in FilterKunstgrasVensters(beschikbaarheid.BeschikbareVensters))
            {
                var totTekst = IsEindeDag(v.Tot) ? "einde dag" : v.Tot;
                tekst += $"- {v.VeldNaam}: beschikbaar van {v.Van} tot {totTekst}\n";
            }
            return tekst;
        }

        if (beschikbaarheid.Toewijzing is { } t)
            return $"Op {gewenstDatumTekst} is {t.VeldNaam} beschikbaar om {t.AanvangsTijd} (eindigt {t.EindTijd}).";

        return $"Op {gewenstDatumTekst} is er ruimte beschikbaar.";
    }
}
