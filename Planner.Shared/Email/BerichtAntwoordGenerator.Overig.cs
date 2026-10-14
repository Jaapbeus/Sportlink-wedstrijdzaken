namespace Planner.Shared.Email;

// Vaste antwoorden van BerichtAntwoordGenerator (#1758): teambegeleiding, bevestiging, buiten scope, wedstrijd al
// ingepland, team onbekend en fout. Zie BerichtAntwoordGenerator.cs voor de poorten en de helpers.
public static partial class BerichtAntwoordGenerator
{
    // ── Teambegeleiding doorsturen ──

    public static (string onderwerp, string body) BouwTeamContactAutoReply(
        BerichtClassificatie classificatie, IAntwoordAfzender email, IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        var teamWeergave = AiVeldSanering.TeamNaamWeergave(classificatie.TeamNaam, classificatie.TeamNaamOnbevestigd);
        var teamTekst = teamWeergave != null
            ? $"de begeleiding van {teamWeergave}"
            : "de begeleiding van het opgegeven team";

        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Uw vraag over {teamTekst} is doorgestuurd. "
                   + "De begeleider neemt rechtstreeks contact met u op. "
                   + "Contactgegevens worden niet gedeeld conform AVG.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    // ── Bevestiging ──

    public static (string onderwerp, string body) BouwBevestigingAntwoord(
        IAntwoordAfzender email, BerichtClassificatie classificatie, IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + "Bedankt voor je bevestiging. Het verzoek is geregistreerd "
                   + "en wordt door de coördinator verwerkt.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    // ── Buiten scope ──

    public static (string onderwerp, string body) BouwBuitenScopeAntwoord(IAntwoordAfzender email, IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var classificatie = new BerichtClassificatie { Type = VerzoekType.BuitenScope };
        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + "Bedankt voor je bericht. Dit verzoek vereist handmatige afhandeling "
                   + "en is ter beoordeling bij de coördinator neergelegd.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    // ── Wedstrijd al ingepland ──

    public static (string onderwerp, string body) BouwWedstrijdAlIngeplandAntwoord(
        IAntwoordWedstrijd? wedstrijd,
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

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    // ── Team onbekend — vraag welk eigen team ──

    public static (string onderwerp, string body) BouwTeamOnbekendAntwoord(
        string tegenstander,
        BerichtClassificatie classificatie,
        IAntwoordAfzender email,
        IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"We kunnen de wedstrijd van {AiVeldSanering.TegenstanderOfGeneriek(tegenstander, "de opgegeven tegenstander")} niet vinden in ons programma. "
                   + "Tegen welk van onze teams zou deze wedstrijd zijn? "
                   + "Dan kunnen we de beschikbaarheid voor je controleren.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    // ── Fout ──

    public static (string onderwerp, string body) BouwFoutAntwoord(
        IAntwoordAfzender email, BerichtClassificatie classificatie, IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var inhoud = $"{aanhef} {voornaam},\n\n"
                   + "Er is een fout opgetreden bij het verwerken van je verzoek. "
                   + "De coördinator is op de hoogte gesteld en neemt zo snel mogelijk contact op.";

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }
}
