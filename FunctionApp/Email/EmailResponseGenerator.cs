namespace SportlinkFunction.Email;

public static class EmailResponseGenerator
{
    /// <summary>
    /// Bouwt een compleet email-antwoord op inclusief review-header (indien actief), AI-tekst en handtekening.
    /// </summary>
    public static (string onderwerp, string body) BouwAntwoordOp(
        string aiAntwoord,
        EmailClassificatie classificatie,
        InkomendEmail origineleEmail)
    {
        var onderwerp = $"Re: {origineleEmail.Onderwerp}";
        var body = "";

        // Review-mode header
        var reviewMode = Environment.GetEnvironmentVariable("EmailReviewMode");
        if (string.Equals(reviewMode, "true", StringComparison.OrdinalIgnoreCase))
        {
            body += $"=== REVIEW MODE ===\n"
                  + $"Originele afzender: {origineleEmail.Afzender}\n"
                  + $"Onderwerp: {origineleEmail.Onderwerp}\n"
                  + $"Classificatie: {classificatie.Type}\n"
                  + $"==================\n\n";
        }

        body += aiAntwoord;
        body += "\n\n" + GetHandtekening();

        return (onderwerp, body);
    }

    /// <summary>
    /// Standaard antwoord voor verzoeken die buiten scope vallen.
    /// </summary>
    public static (string onderwerp, string body) BouwBuitenScopeAntwoord(InkomendEmail origineleEmail)
    {
        var onderwerp = $"Re: {origineleEmail.Onderwerp}";
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(origineleEmail.AfzenderNaam);

        var body = "";

        var reviewMode = Environment.GetEnvironmentVariable("EmailReviewMode");
        if (string.Equals(reviewMode, "true", StringComparison.OrdinalIgnoreCase))
        {
            body += $"=== REVIEW MODE ===\n"
                  + $"Originele afzender: {origineleEmail.Afzender}\n"
                  + $"Onderwerp: {origineleEmail.Onderwerp}\n"
                  + $"Classificatie: BuitenScope\n"
                  + $"==================\n\n";
        }

        body += $"{aanhef} {voornaam},\n\n"
              + "Bedankt voor je bericht. Dit verzoek vereist handmatige afhandeling "
              + "en is ter beoordeling bij de coördinator neergelegd.\n\n"
              + GetHandtekening();

        return (onderwerp, body);
    }

    /// <summary>
    /// Tijdsgebonden aanhef op basis van het huidige uur.
    /// </summary>
    public static string GetTijdsgebondenAanhef()
    {
        var uur = DateTime.Now.Hour;
        if (uur < 12) return "Goedemorgen";
        if (uur < 18) return "Goedemiddag";
        return "Goedenavond";
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
