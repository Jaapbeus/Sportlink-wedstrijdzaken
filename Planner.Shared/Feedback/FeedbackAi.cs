using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Text;

using static Planner.Shared.Feedback.FeedbackTekst;

namespace Planner.Shared.Feedback;

/// <summary>AI-aanroepen van de feedbackwidget: volledigheid valideren en issue structureren (#1494).</summary>
internal static class FeedbackAi
{
    // ── AI: gedeeld JSON-ophaal-en-parse-blok ──────────────────────────────────

    private static async Task<JObject> RoepAiJsonAanAsync(
        IChatClient chatClient, List<ChatMessage> messages, float temperature, string logLabel, ILogger log)
    {
        var options = new ChatOptions
        {
            Temperature = temperature,
            ResponseFormat = ChatResponseFormat.Json
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await chatClient.GetResponseAsync(messages, options);
        sw.Stop();
        var json = response.Text ?? "";
        // Nooit de ruwe AI-respons loggen (#1006) — die kan ongecontroleerde, mogelijk persoonsgegevens
        // bevattende tekst bevatten. Alleen veilige technische metadata.
        log.LogDebug("{Label} AI response ontvangen: {Lengte} tekens in {DuurMs} ms", logLabel, json.Length, sw.ElapsedMilliseconds);

        return JObject.Parse(json);
    }

    // ── AI: volledigheid valideren ─────────────────────────────────────────────

    internal static async Task<ValidateResponse> ValideerVolledigheid(
        IChatClient chatClient, FeedbackRequest dto, ILogger log)
    {
        // Als de gebruiker al antwoorden heeft gegeven op aanvulvragen, accepteer direct.
        // Re-validatie leidt tot dezelfde vragen omdat het AI-model eerder gestelde vragen
        // opnieuw stelt ondanks het antwoord — de antwoorden vullen de gaten per definitie.
        if (dto.VragenAntwoorden?.Any(qa => !string.IsNullOrWhiteSpace(qa.Antwoord)) == true)
            return new ValidateResponse(true, []);

        var beschrijving = Sanitize(dto.Beschrijving, 2000);
        var paginaInfo = string.IsNullOrWhiteSpace(dto.Context?.Pagina) ? "" : $"Pagina: {dto.Context.Pagina}\n";
        var qaBlok = BouwQaBlok(dto.VragenAntwoorden);

        var systemPrompt = """
            Je beoordeelt of feedback van een clubbeheerder voldoende informatie bevat om te worden opgelost.

            Regels per type:
            - 'Fout': minimaal vereist — wat gaat er mis, en wat werd verwacht.
            - 'Verzoek': minimaal vereist — wat wil men bereiken.
            - 'Vraag': bijna altijd voldoende tenzij compleet onduidelijk.

            Geef uitsluitend JSON in dit formaat:
            { "volledig": true/false, "vragen": ["..."] }

            Als volledig: lege vragen-array.
            Als niet volledig: max 3 korte, vriendelijke aanvulvragen in begrijpelijk Nederlands.
            Nooit technisch jargon. Nooit vragen naar dingen die al beantwoord zijn.
            """;

        var userPrompt = $"""
            Type: {dto.Type}
            {paginaInfo}Beschrijving: "{beschrijving}"
            {qaBlok}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var parsed = await RoepAiJsonAanAsync(chatClient, messages, 0.1f, "Validate", log);
        var volledig = parsed["volledig"]?.Value<bool>() ?? false;
        var vragen = parsed["vragen"]?.ToObject<List<string>>() ?? [];

        return new ValidateResponse(volledig, vragen);
    }

    // ── AI: issue structureren ─────────────────────────────────────────────────

    internal static async Task<StructuredIssue> StructureerIssue(
        IChatClient chatClient, FeedbackRequest dto, ILogger log)
    {
        var beschrijving = Sanitize(dto.Beschrijving, 2000);
        var qaBlok = BouwQaBlok(dto.VragenAntwoorden);
        // #764: de (reeds geredigeerde) technische context laat het model de oorzaak concreet
        // benoemen ("POST /api/... geeft 500") in plaats van "de knop doet niets". Alleen aanwezig
        // als de melder dit niet heeft uitgezet.
        var contextBlok = dto.Telemetrie is { IsLeeg: false } t
            ? $"\nTechnische context (automatisch verzameld, al geredigeerd):\n{Sanitize(t.NaarTekst(), 1500)}\n"
            : "";

        var systemPrompt = """
            Je vertaalt gebruikersfeedback van een clubbeheerder naar een gestructureerd GitHub issue voor een developer.

            Geef uitsluitend JSON in dit formaat:
            {
              "title": "korte issue titel, max 70 tekens",
              "samenvatting": "1-2 zinnen die het probleem of verzoek beschrijven voor de developer",
              "acceptatiecriteria": ["concreet testbaar criterium", "criterium 2"]
            }

            Voor een bug:
            - Titel: beschrijft wat er mis gaat (niet 'gebruiker meldt...')
            - Samenvatting: wat de gebruiker deed, wat er fout ging, wat verwacht werd
            - Criteria: testbare verbeteringen (elk < 80 tekens, max 5 stuks)

            Voor een verzoek:
            - Titel: "Voeg X toe" of "Maak X mogelijk"
            - Samenvatting: gewenste gedrag en reden
            - Criteria: implementatiestappen als checkbox

            Schrijf technisch, voor een developer, niet voor de gebruiker.
            Is er technische context meegegeven, gebruik die dan om de oorzaak concreet te benoemen
            (route, statuscode, foutmelding). Neem nooit namen of andere persoonsgegevens over.
            """;

        var userPrompt = $"""
            Type: {dto.Type}
            Pagina: {dto.Context?.Pagina ?? "onbekend"}
            Versie: {dto.Context?.Versie ?? "?"}

            Beschrijving gebruiker: "{beschrijving}"
            {qaBlok}{contextBlok}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var parsed = await RoepAiJsonAanAsync(chatClient, messages, 0.2f, "Submit", log);
        return new StructuredIssue(
            parsed["title"]?.Value<string>() ?? StandaardTitel(dto.Type),
            parsed["samenvatting"]?.Value<string>() ?? "",
            parsed["acceptatiecriteria"]?.ToObject<List<string>>() ?? []
        );
    }

    internal static string StandaardTitel(string type) => $"[{type}] Gebruikersmelding";

    // Grenzen voor de gestructureerde velden. De samenvatting is volgens de system prompt 1-2 zinnen;
    // 500 tekens sluit aan bij de bestaande grens voor een antwoord op een aanvulvraag en laat een
    // uitgebreide samenvatting ruim toe. Acceptatiecriteria vraagt de prompt om "max 5 stuks" — dat
    // is hier de harde grens, zodat een client die er 500 stuurt wordt afgekapt in plaats van ze
    // allemaal in een publiek issue te krijgen.
    private const int MaxSamenvattingLengte = 500;
    internal const int MaxAcceptatiecriteria = 5;
    internal const int MaxAcceptatiecriteriumLengte = 120;

    /// <summary>
    /// Brengt de gestructureerde velden binnen dezelfde sanitizer en grenzen als alle andere tekst die
    /// de issuebody in gaat (#1205). Wordt op zowel AI-output als bevestigde clientwaarden toegepast,
    /// zodat het voorbeeld en de publicatie per constructie dezelfde tekst opleveren.
    /// <see cref="Sanitize"/> is idempotent, dus dubbel toepassen (hier én in
    /// <see cref="FeedbackIssueBody.Bouw"/>) verandert het resultaat niet.
    /// </summary>
    internal static StructuredIssue Normaliseer(StructuredIssue structured) => new(
        structured.Title,
        Sanitize(structured.Samenvatting, MaxSamenvattingLengte),
        [.. structured.Acceptatiecriteria
            .Take(MaxAcceptatiecriteria)
            .Select(c => Sanitize(c, MaxAcceptatiecriteriumLengte))]);

    internal sealed record StructuredIssue(string Title, string Samenvatting, List<string> Acceptatiecriteria);
    internal sealed record ValidateResponse(bool Volledig, List<string> Vragen);
}
