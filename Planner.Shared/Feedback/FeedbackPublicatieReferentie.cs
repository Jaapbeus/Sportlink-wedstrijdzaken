using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Planner.Shared.Feedback;

/// <summary>
/// Unieke, niet-herleidbare publicatiereferentie van een feedbackmelding (#1803, eigenaarsbesluit 2C).
/// Ze wordt per melding één keer gemaakt, vóór de eerste GitHub-POST in de bewaarde issuetekst
/// vastgelegd en bij elke nieuwe poging hergebruikt: zo kan automatisch herstel een eerder
/// aangemaakt issue herkennen zonder dat melder-identiteit, e-mailadres of een databasesleutel
/// in het publieke issue staat. Alleen kleine letters, zodat de PII-gate en de redactie
/// (cijferreeksen, tokens) er nooit op aanslaan.
/// </summary>
public static class FeedbackPublicatieReferentie
{
    private const int Lengte = 24;
    private const string Voorvoegsel = "<!-- feedback-ref: ";
    private const string Achtervoegsel = " -->";

    private static readonly Regex Patroon = new(
        @"<!-- feedback-ref: (?<ref>[a-z]{24}) -->", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>Een nieuwe willekeurige referentie (cryptografisch random, geen afgeleide van een id).</summary>
    public static string Nieuw() =>
        string.Create(Lengte, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)('a' + RandomNumberGenerator.GetInt32(26));
        });

    /// <summary>Voegt de referentie als verborgen HTML-commentaar toe; een body die er al een heeft blijft ongewijzigd.</summary>
    public static string VoegToe(string body, string referentie) =>
        Lees(body) is not null ? body : body.TrimEnd() + "\n\n" + Voorvoegsel + referentie + Achtervoegsel + "\n";

    /// <summary>De referentie uit een issuetekst, of <c>null</c> (oudere melding of geen referentie).</summary>
    public static string? Lees(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            var m = Patroon.Match(body);
            return m.Success ? m.Groups["ref"].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}

/// <summary>Een bestaand GitHub-issue dat bij een publicatiereferentie hoort.</summary>
public sealed record FeedbackGevondenIssue(int Nummer, string Url);

/// <summary>Termijnen van het automatische herstel van een vastgelopen publicatie (#1803).</summary>
public static class FeedbackPublicatieHerstel
{
    /// <summary>
    /// Zolang een claim jonger is, loopt mogelijk nog een eerste poging; een tweede klik krijgt dan 409.
    /// Ruim boven de HTTP-timeout (15 s per aanroep) en onder de functiemaximumduur.
    /// </summary>
    public static readonly TimeSpan ClaimVerlooptNa = TimeSpan.FromMinutes(2);

    /// <summary>Marge voor klokverschil tussen database en GitHub bij het zoeken vanaf het claimmoment.</summary>
    public static readonly TimeSpan ZoekMarge = TimeSpan.FromMinutes(5);
}
