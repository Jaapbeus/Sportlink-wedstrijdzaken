using Planner.Shared;
using System.Globalization;
using SportlinkFunction.Planner;

namespace SportlinkFunction.Email;

public static class BerichtResponseGenerator
{
    private static readonly CultureInfo NL = new("nl-NL");

    // ── Beschikbaarheid ──

    public static (string onderwerp, string body) BouwBeschikbaarheidAntwoord(
        CheckAvailabilityResponse response,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        var vensters = response.BeschikbareVensters != null
            ? FilterKunstgrasVensters(response.BeschikbareVensters)
            : new List<BeschikbaarVenster>();
        var isDiverseMogelijkheden = vensters.Count >= 4;
        var datumTekst = FormatDatum(classificatie.Datum);

        string inhoud;

        if (response.Beschikbaar && response.Toewijzing != null)
        {
            var t = response.Toewijzing;
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst} is {t.VeldNaam} beschikbaar om {t.AanvangsTijd}. "
                   + $"De wedstrijd eindigt om {t.EindTijd}.";

            if (isDiverseMogelijkheden)
                inhoud += " Er zijn op deze dag nog diverse andere mogelijkheden.";

            if (response.Waarschuwingen.Count > 0)
                inhoud += "\n\nLet op: " + string.Join(" ", response.Waarschuwingen);
        }
        else if (!response.Beschikbaar && vensters.Count > 0 && !string.IsNullOrEmpty(classificatie.AanvangsTijd))
        {
            // Voorkeurstijd niet beschikbaar — toon vensters als alternatieven
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst} om {classificatie.AanvangsTijd} is helaas geen ruimte. Beschikbare mogelijkheden:\n";
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
        else if (response.BeschikbareVensters?.Count > 0)
        {
            // Open vraag zonder voorkeurstijd — toon beschikbare vensters
            vensters = FilterKunstgrasVensters(response.BeschikbareVensters);
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
        else if (response.TeamConflict != null)
        {
            // Team heeft al een wedstrijd op deze datum — dit is de primaire reden.
            // Veld-beschikbaarheid is niet gecheckt (early return in PlannerService),
            // dus "geen veld beschikbaar" is hier feitelijk onjuist en misleidend.
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"{response.Reden}\n\n"
                   + $"Hierdoor kan op {datumTekst} geen oefenwedstrijd worden ingepland.";
        }
        else
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst} is helaas geen veld beschikbaar.";
            if (!string.IsNullOrEmpty(response.Reden))
                inhoud += $" {response.Reden}";
        }

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Beschikbaarheid (meerdere datums) ──

    public static (string onderwerp, string body) BouwMultiDatumBeschikbaarheidAntwoord(
        List<(string datum, CheckAvailabilityResponse response)> resultaten,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var inhoud = $"{aanhef} {voornaam},\n\n";

        foreach (var (datum, response) in resultaten)
        {
            inhoud += BouwDatumSectie(datum, response, classificatie) + "\n";
        }

        inhoud += BouwMultiDatumAfsluitzin(clubSettings);

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    /// <summary>
    /// Afsluitzin van een multi-datum beschikbaarheidsantwoord, met de coördinator bij naam (#670).
    ///
    /// <para>
    /// Leest de instelling via dezelfde weg als <see cref="GetHandtekening"/>: uit de meegegeven
    /// club-snapshot wanneer die er is, en anders uit de globale cache. Dat is de regel uit #677 —
    /// een dry-run voor de democlub mag niet terugvallen op de gegevens van de productieclub.
    /// </para>
    ///
    /// <para>
    /// Ontbreekt de naam, dan blijft de zin clubneutraal en compleet: geen halve zin, en bewust geen
    /// fallbacknaam in de code (architectuurregel "geen club-specifieke strings").
    /// </para>
    /// </summary>
    private static string BouwMultiDatumAfsluitzin(ClubAppSettingsSnapshot? clubSettings)
    {
        var coordinatorNaam = clubSettings != null
            ? clubSettings.CoordinatorNaam
            : SystemUtilities.AppSettings.GetSetting("coordinatorNaam");

        return string.IsNullOrWhiteSpace(coordinatorNaam)
            ? "Laat weten welke optie(s) de voorkeur hebben, dan gaan we samen plannen en definitief opnemen in de planning."
            : $"Laat weten welke optie(s) de voorkeur hebben, dan gaan we samen met {coordinatorNaam} plannen en definitief opnemen in de planning.";
    }

    /// <summary>
    /// Bouwt de sectie voor één datum in een (multi-datum) beschikbaarheidantwoord.
    /// Toont vensters (van-tot) i.p.v. vaste starttijden, en "diverse mogelijkheden" bij een ruime planning.
    /// </summary>
    private static string BouwDatumSectie(
        string datum, CheckAvailabilityResponse response, BerichtClassificatie classificatie)
    {
        var datumTekst = FormatDatum(datum);
        var vensters = response.BeschikbareVensters != null
            ? FilterKunstgrasVensters(response.BeschikbareVensters)
            : new List<BeschikbaarVenster>();
        var isDiverseMogelijkheden = vensters.Count >= 4;

        if (response.Beschikbaar && response.Toewijzing != null)
        {
            var t = response.Toewijzing;
            var sectie = $"**{datumTekst}:** {t.VeldNaam} is beschikbaar om {t.AanvangsTijd} (eindigt {t.EindTijd}).";
            if (response.Waarschuwingen.Count > 0)
                sectie += " Let op: " + string.Join(" ", response.Waarschuwingen);
            if (isDiverseMogelijkheden)
                sectie += " Er zijn op deze dag nog diverse andere mogelijkheden.";
            return sectie + "\n";
        }

        if (!response.Beschikbaar && vensters.Count > 0)
        {
            // Voorkeurstijd niet beschikbaar — toon vensters als alternatieven
            var sectie = $"**{datumTekst}:**";
            if (!string.IsNullOrEmpty(classificatie.AanvangsTijd))
                sectie += $" Om {classificatie.AanvangsTijd}";
            sectie += " is helaas geen ruimte. Beschikbare mogelijkheden:\n";
            foreach (var v in vensters)
            {
                var totTekst = IsEindeDag(v.Tot) ? "einde dag" : v.Tot;
                sectie += $"- {v.VeldNaam}: beschikbaar van {v.Van} tot {totTekst}";
                if (!string.IsNullOrEmpty(v.Opmerking))
                    sectie += $" ({v.Opmerking})";
                sectie += "\n";
            }
            return sectie;
        }

        if (!response.Beschikbaar && response.Alternatieven.Count > 0)
        {
            var alternatieven = FilterAlternatieven(response.Alternatieven);
            var sectie = $"**{datumTekst}:**";
            if (!string.IsNullOrEmpty(classificatie.AanvangsTijd))
                sectie += $" Om {classificatie.AanvangsTijd}";
            sectie += " is helaas geen ruimte.";
            if (alternatieven.Count > 0)
            {
                sectie += " Alternatieven:\n";
                foreach (var alt in alternatieven.Take(3))
                    sectie += $"- {alt.VeldNaam} om {alt.AanvangsTijd} (eindigt {alt.EindTijd})\n";
            }
            return sectie;
        }

        if (response.TeamConflict != null)
            return $"**{datumTekst}:** {response.Reden} Hierdoor kan op deze dag geen oefenwedstrijd worden ingepland.\n";

        var fallback = $"**{datumTekst}:** Helaas geen veld beschikbaar.";
        if (!string.IsNullOrEmpty(response.Reden))
            fallback += $" {response.Reden}";
        return fallback + "\n";
    }

    // ── Herplannen ──

    public static (string onderwerp, string body) BouwHerplanAntwoord(
        ZoekWedstrijdResponse? wedstrijd,
        HerplanCheckResponse? herplanOpties,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
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

        if (!string.IsNullOrWhiteSpace(classificatie.KnvbNotitie))
            inhoud += $"\n\nLet op: {classificatie.KnvbNotitie} Zie ook: https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/regelen-dagelijkse-praktijk/verplaatsen-van-wedstrijden";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Herplannen — te laat (verzoek binnen deadline) ──

    public static (string onderwerp, string body) BouwHerplanTeLaatAntwoord(
        ZoekWedstrijdResponse? wedstrijd,
        int deadlineDagen,
        int dagenTotWedstrijd,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
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

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Herplannen — verzoek van tegenstander zonder concrete datum (#561) ──

    /// <summary>
    /// Bouwt het antwoord op een herplanverzoek van de tegenstander zonder concrete nieuwe datum.
    /// Zegt bewust geen nieuwe datum toe — dat wordt afgestemd met de begeleiding van ons eigen
    /// team (die in BCC staat, zie <see cref="Email.EmailReplyPolicyService"/>) — maar geeft als
    /// voorzet een paar zaterdagen waarop ons team volgens het huidige programma nog vrij is.
    /// </summary>
    public static (string onderwerp, string body) BouwVerzetZonderDatumAntwoord(
        ZoekWedstrijdResponse? wedstrijd,
        List<string> vrijeZaterdagen,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        string inhoud;

        if (wedstrijd == null)
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Er is geen wedstrijd gevonden voor {classificatie.TeamNaam ?? "het opgegeven team"} "
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

            if (vrijeZaterdagen.Count > 0)
            {
                inhoud += "\n\nAls voorzet: volgens ons huidige programma hebben we op de volgende zaterdagen nog "
                        + "geen wedstrijd:\n";
                foreach (var datum in vrijeZaterdagen)
                    inhoud += $"- {FormatDatum(datum)}\n";
            }
            else
            {
                inhoud += "\n\nOp korte termijn hebben we geen speelvrije zaterdagen kunnen vinden binnen de "
                        + "gebruikelijke termijn. Bekijk de bijgevoegde speeldagenkalender voor het volledige overzicht.";
            }
        }

        if (classificatie.VoegKnvbPdfBijlageToe)
        {
            inhoud += "\n\nDe KNVB-speeldagenkalender is als bijlage toegevoegd.";
        }

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Herplannen naar gewenste datum ──

    public static (string onderwerp, string body) BouwHerplanGewensteDatumAntwoord(
        ZoekWedstrijdResponse? wedstrijd,
        string? gewensteDatum,
        CheckAvailabilityResponse? beschikbaarheid,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        var gewenstDatumTekst = FormatDatum(gewensteDatum);
        string inhoud;

        if (wedstrijd == null)
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Er is geen wedstrijd gevonden voor {classificatie.TeamNaam ?? "het opgegeven team"} "
                   + $"op {FormatDatum(classificatie.Datum)}.";
        }
        else if (beschikbaarheid == null || !beschikbaarheid.Beschikbaar)
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"De wedstrijd {wedstrijd.Wedstrijd} staat momenteel gepland op {FormatDatum(wedstrijd.Datum)} om {wedstrijd.AanvangsTijd} op {wedstrijd.VeldNaam}.\n\n"
                   + $"Helaas is er op {gewenstDatumTekst} geen ruimte beschikbaar.";
            if (!string.IsNullOrEmpty(beschikbaarheid?.Reden))
                inhoud += $" {beschikbaarheid.Reden}";
        }
        else if (beschikbaarheid.BeschikbareVensters?.Count > 0)
        {
            var vensters = FilterKunstgrasVensters(beschikbaarheid.BeschikbareVensters);
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"De wedstrijd {wedstrijd.Wedstrijd} staat momenteel gepland op {FormatDatum(wedstrijd.Datum)} om {wedstrijd.AanvangsTijd} op {wedstrijd.VeldNaam}.\n\n"
                   + $"Op {gewenstDatumTekst} zijn de volgende mogelijkheden:\n";
            foreach (var v in vensters)
            {
                var totTekst = IsEindeDag(v.Tot) ? "einde dag" : v.Tot;
                inhoud += $"- {v.VeldNaam}: beschikbaar van {v.Van} tot {totTekst}\n";
            }
        }
        else if (beschikbaarheid.Toewijzing != null)
        {
            var t = beschikbaarheid.Toewijzing;
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"De wedstrijd {wedstrijd.Wedstrijd} staat momenteel gepland op {FormatDatum(wedstrijd.Datum)} om {wedstrijd.AanvangsTijd} op {wedstrijd.VeldNaam}.\n\n"
                   + $"Op {gewenstDatumTekst} is {t.VeldNaam} beschikbaar om {t.AanvangsTijd} (eindigt {t.EindTijd}).";
        }
        else
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"De wedstrijd {wedstrijd.Wedstrijd} staat momenteel gepland op {FormatDatum(wedstrijd.Datum)} om {wedstrijd.AanvangsTijd} op {wedstrijd.VeldNaam}.\n\n"
                   + $"Op {gewenstDatumTekst} is er ruimte beschikbaar.";
        }

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Teambegeleiding doorsturen ──

    public static (string onderwerp, string body) BouwTeamContactAutoReply(
        BerichtClassificatie classificatie, InkomendBericht email, ClubAppSettingsSnapshot? clubSettings = null)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        var teamTekst = !string.IsNullOrWhiteSpace(classificatie.TeamNaam)
            ? $"de begeleiding van {classificatie.TeamNaam}"
            : "de begeleiding van het opgegeven team";

        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Uw vraag over {teamTekst} is doorgestuurd. "
                   + "De begeleider neemt rechtstreeks contact met u op. "
                   + "Contactgegevens worden niet gedeeld conform AVG.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Bevestiging ──

    public static (string onderwerp, string body) BouwBevestigingAntwoord(
        InkomendBericht email, BerichtClassificatie classificatie, ClubAppSettingsSnapshot? clubSettings = null)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + "Bedankt voor je bevestiging. Het verzoek is geregistreerd "
                   + "en wordt door de coördinator verwerkt.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Buiten scope ──

    public static (string onderwerp, string body) BouwBuitenScopeAntwoord(InkomendBericht email, ClubAppSettingsSnapshot? clubSettings = null)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var classificatie = new BerichtClassificatie { Type = VerzoekType.BuitenScope };
        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + "Bedankt voor je bericht. Dit verzoek vereist handmatige afhandeling "
                   + "en is ter beoordeling bij de coördinator neergelegd.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Wedstrijd al ingepland ──

    public static (string onderwerp, string body) BouwWedstrijdAlIngeplandAntwoord(
        ZoekWedstrijdResponse? wedstrijd,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        string inhoud;

        if (wedstrijd == null)
        {
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + "Er is een fout opgetreden bij het ophalen van de wedstrijdgegevens. "
                   + "De coördinator neemt zo snel mogelijk contact op.";
        }
        else
        {
            var datumTekst = FormatDatum(wedstrijd.Datum);
            var veldTekst = !string.IsNullOrWhiteSpace(wedstrijd.VeldNaam)
                ? $" op {wedstrijd.VeldNaam}"
                : "";
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"De wedstrijd {wedstrijd.Wedstrijd} staat al ingepland op {datumTekst} "
                   + $"om {wedstrijd.AanvangsTijd}{veldTekst}.";
        }

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Team onbekend — vraag welk eigen team ──

    public static (string onderwerp, string body) BouwTeamOnbekendAntwoord(
        string tegenstander,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"We kunnen de wedstrijd van {tegenstander} niet vinden in ons programma. "
                   + "Tegen welk van onze teams zou deze wedstrijd zijn? "
                   + "Dan kunnen we de beschikbaarheid voor je controleren.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Fout ──

    public static (string onderwerp, string body) BouwFoutAntwoord(
        InkomendBericht email, BerichtClassificatie classificatie, ClubAppSettingsSnapshot? clubSettings = null)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + "Er is een fout opgetreden bij het verwerken van je verzoek. "
                   + "De coördinator is op de hoogte gesteld en neemt zo snel mogelijk contact op.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, clubSettings);
    }

    // ── Template-driven antwoord (v2 — EmailTemplateService overload) ──

    /// <summary>
    /// Past een EmailTemplate toe op de classificatie. Placeholders: {{voornaam}}, {{aanhef}},
    /// {{datum}}, {{team}}, {{tegenstander}}, {{aanvangstijd}}.
    /// Valt terug op de standaard handtekening + review-wrapper.
    /// Niet-destructief: bestaande Bouw* methoden blijven beschikbaar als fallback.
    /// </summary>
    public static (string onderwerp, string body) BouwAangepasteAntwoord(
        EmailTemplate template,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null,
        IDictionary<string, string>? extraPlaceholders = null)
    {
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["voornaam"] = ExtractVoornaam(email.AfzenderNaam),
            ["aanhef"] = GetTijdsgebondenAanhef(),
            ["datum"] = FormatDatum(classificatie.Datum),
            ["team"] = classificatie.TeamNaam ?? "",
            ["tegenstander"] = classificatie.Tegenstander ?? "",
            ["aanvangstijd"] = classificatie.AanvangsTijd ?? "",
        };

        if (extraPlaceholders != null)
        {
            foreach (var (k, v) in extraPlaceholders)
                placeholders[k] = v;
        }

        var onderwerpInvulling = EmailTemplateService.ApplyPlaceholders(template.Onderwerp, placeholders);
        var bodyInvulling = EmailTemplateService.ApplyPlaceholders(template.Body, placeholders);

        var onderwerp = !string.IsNullOrWhiteSpace(onderwerpInvulling)
            ? onderwerpInvulling
            : $"Re: {email.Onderwerp}";

        // Wrap met review-mode prefix + handtekening, hergebruik de bestaande logica
        var reviewMode = Environment.GetEnvironmentVariable("EmailReviewMode");
        var body = "";
        if (string.Equals(reviewMode, "true", StringComparison.OrdinalIgnoreCase))
        {
            body += $"=== REVIEW MODE ===\n"
                  + $"Originele afzender: {email.Afzender}\n"
                  + $"Onderwerp: {email.Onderwerp}\n"
                  + $"Classificatie: {classificatie.Type}\n"
                  + $"Template: {template.Key}\n"
                  + $"==================\n\n";
        }

        body += bodyInvulling;
        body += "\n\n" + GetHandtekening(clubSettings);
        return (onderwerp, body);
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
    /// Beperkt de alternatieven tot kunstgras zodra er drie of meer kunstgrasvelden beschikbaar zijn:
    /// op zo'n ruime dag hoeft het antwoord geen natuurgras aan te bieden.
    ///
    /// <para>Het veldtype komt uit <c>dbo.Velden</c> (#705). Eerder werd het uit het veldnummer geraden
    /// (1-4 = kunstgras, 5 = gras); bij een club met natuurgras op de lage nummers gooide dat juist
    /// de échte kunstgrasvelden weg en verzweeg het antwoord beschikbare velden.</para>
    ///
    /// <para><b>Alleen aantoonbaar natuurgras verdwijnt (#707).</b> De classificatie loopt via
    /// <see cref="VeldTypeClassificatie"/> — de enige kunstgras-definitie in de codebase. Een veldtype
    /// dat noch als kunstgras noch als natuurgras herkenbaar is (leeg, of bijv. "hybride") geldt als
    /// onbekend: het telt niet mee voor de drempel én wordt nooit weggefilterd. Zo'n slot is
    /// aantoonbaar beschikbaar, en dat weglaten is schadelijker dan een optie te veel aanbieden.</para>
    /// </summary>
    private static List<SlotToewijzing> FilterAlternatieven(List<SlotToewijzing> alternatieven)
    {
        var kunstgrasVelden = alternatieven
            .Where(a => VeldTypeClassificatie.IsKunstgras(a.VeldType))
            .Select(a => a.VeldNummer).Distinct().Count();
        if (kunstgrasVelden < 3)
            return alternatieven;

        // Bewust IsNatuurgras en niet !IsKunstgras: onbekend is geen natuurgras en blijft staan.
        return alternatieven
            .Where(a => !VeldTypeClassificatie.IsNatuurgras(a.VeldType))
            .ToList();
    }

    /// <summary>
    /// Laat natuurgrasvensters weg zodra er drie of meer kunstgrasvensters zijn, behalve als een
    /// natuurgrasvenster het enige aanbod in zijn tijdsblok is — dan is het juist de toegevoegde
    /// waarde. Veldtype uit <c>dbo.Velden</c>, niet uit het veldnummer (#705), geclassificeerd via
    /// <see cref="VeldTypeClassificatie"/> (#707); vensters met een onbekend veldtype blijven altijd staan.
    /// </summary>
    private static List<BeschikbaarVenster> FilterKunstgrasVensters(List<BeschikbaarVenster> vensters)
    {
        var kunstgrasVensters = vensters.Where(v => VeldTypeClassificatie.IsKunstgras(v.VeldType)).ToList();
        if (kunstgrasVensters.Count < 3)
            return vensters;

        var overigeVensters = vensters
            .Where(v => !VeldTypeClassificatie.IsKunstgras(v.VeldType))
            .Where(v => !VeldTypeClassificatie.IsNatuurgras(v.VeldType) || !OverlaptMetVenster(v, kunstgrasVensters))
            .ToList();

        return kunstgrasVensters.Concat(overigeVensters)
            .OrderBy(v => TimeOnly.TryParse(v.Van, out var t) ? t : TimeOnly.MinValue)
            .ToList();
    }

    private static bool OverlaptMetVenster(BeschikbaarVenster venster, List<BeschikbaarVenster> andere)
    {
        var van = TimeOnly.TryParse(venster.Van, out var v) ? v : TimeOnly.MinValue;
        var tot = TimeOnly.TryParse(venster.Tot, out var t) ? t : TimeOnly.MaxValue;
        return andere.Any(a =>
        {
            var aVan = TimeOnly.TryParse(a.Van, out var av) ? av : TimeOnly.MinValue;
            var aTot = TimeOnly.TryParse(a.Tot, out var at) ? at : TimeOnly.MaxValue;
            return aVan < tot && aTot > van;
        });
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
        string inhoud, BerichtClassificatie classificatie, InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
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
        body += "\n\n" + GetHandtekening(clubSettings);

        return (onderwerp, body);
    }

    /// <summary>
    /// Bouwt de auto-reply handtekening. <paramref name="clubSettings"/> (#677) laat het dry-run
    /// pad (EmailTestFunction) de instellingen van de in de GUI geselecteerde club gebruiken —
    /// bijv. AllStars FC — in plaats van de proces-globale AppSettings-cache, die altijd de
    /// primaire (echte) club van deze deployment bevat. Is <paramref name="clubSettings"/>
    /// <c>null</c> (de echte e-mailpipeline, zonder clubswitcher), dan is het gedrag exact
    /// hetzelfde als vóór #677: lezen uit de globale cache.
    ///
    /// Belangrijk: als een override actief is, valt een ontbrekend veld NIET terug op de globale
    /// cache — dat zou immers de echte productieclub weer laten lekken in een AllStars-dry-run.
    /// </summary>
    private static string GetHandtekening(ClubAppSettingsSnapshot? clubSettings = null)
    {
        var voetnoot = clubSettings != null
            ? clubSettings.EmailVoetnoot
            : SystemUtilities.AppSettings.GetSetting("emailVoetnoot");
        if (!string.IsNullOrWhiteSpace(voetnoot))
            return voetnoot;

        // Fallback: auto-opgebouwde handtekening uit losse instellingen
        var afzenderNaam = clubSettings != null
            ? clubSettings.PlannerAfzenderNaam
                ?? throw new InvalidOperationException("Vereiste instelling 'PlannerAfzenderNaam' ontbreekt in dbo.AppSettings voor de opgegeven club")
            : SystemUtilities.AppSettings.GetSetting("plannerAfzenderNaam")
                ?? throw new InvalidOperationException("Vereiste instelling 'plannerAfzenderNaam' ontbreekt in dbo.AppSettings");
        var coordinatorNaam = clubSettings != null
            ? clubSettings.CoordinatorNaam
            : SystemUtilities.AppSettings.GetSetting("coordinatorNaam");
        var coordinatorFunctie = clubSettings != null
            ? clubSettings.CoordinatorFunctie
            : SystemUtilities.AppSettings.GetSetting("coordinatorFunctie");

        var handtekening = $"Met vriendelijke groet,\n\n{afzenderNaam}";
        if (!string.IsNullOrEmpty(coordinatorNaam))
            handtekening += $"\nGeautomatiseerd antwoord namens {coordinatorNaam}";
        if (!string.IsNullOrEmpty(coordinatorFunctie))
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
