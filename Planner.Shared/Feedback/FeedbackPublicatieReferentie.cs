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
/// <para>
/// De betrouwbare positie is het einde van de tekst: <see cref="VoegToe"/> plaatst er altijd een
/// nieuwe referentie en <see cref="Lees"/> neemt de laatste. Een melder die zelf een
/// <c>feedback-ref</c>-commentaar in de beschrijving zet, bepaalt de referentie dus nooit.
/// </para>
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

    /// <summary>
    /// Zet de referentie als verborgen HTML-commentaar aan het einde van de tekst. Altijd, ook als de tekst al
    /// een (mogelijk door de melder ingetypt) commentaar bevat: alleen de laatste telt (<see cref="Lees"/>).
    /// Roep dit één keer per melding aan, vóór de eerste POST.
    /// </summary>
    public static string VoegToe(string body, string referentie) =>
        body.TrimEnd() + "\n\n" + Voorvoegsel + referentie + Achtervoegsel + "\n";

    /// <summary>De laatste referentie in een issuetekst (de betrouwbare positie), of <c>null</c> (oudere melding of geen referentie).</summary>
    public static string? Lees(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            var treffers = Patroon.Matches(body);
            return treffers.Count > 0 ? treffers[^1].Groups["ref"].Value : null;
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
    /// Ruim boven de langste herstelpoging (tien lijstpagina's plus POST en 422-fallback, elk maximaal 15 s: 180 s,
    /// plus databasetijd) en onder de functiemaximumduur (10 minuten, <c>host.json</c>). Een test bewaakt die marge.
    /// </summary>
    public static readonly TimeSpan ClaimVerlooptNa = TimeSpan.FromMinutes(4);

    /// <summary>Marge voor klokverschil tussen database en GitHub bij het zoeken vanaf het begin van de publicatiepoging.</summary>
    public static readonly TimeSpan ZoekMarge = TimeSpan.FromMinutes(5);
}
