using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Forced-choice AI-disambiguatie (#697): kiest één team uit een korte kandidatenlijst wanneer
/// de deterministische resolutie er meerdere overhoudt (bijv. "13-1" bij een club met zowel
/// JO13-1 als MO13-1).
///
/// Bewust géén vrije generatie: het model krijgt een genummerde lijst en mag alleen een index
/// teruggeven. De keuze wordt daarna nog in C# gevalideerd tegen die lijst, zodat een
/// gehallucineerd nummer nooit tot een verkeerd TeamId kan leiden.
///
/// Deze call vuurt alleen bij ambiguïteit — in de meeste gevallen is de resolutie al
/// deterministisch beslist en wordt er geen enkel token verbruikt.
/// </summary>
public sealed class TeamDisambiguationAiService(
    IChatClient chatClient,
    ILogger<TeamDisambiguationAiService> logger) : ITeamDisambiguator
{
    /// <summary>
    /// Boven dit aantal kandidaten wordt niet gedisambigueerd: dan is de e-mailtekst te vaag
    /// (bijv. alleen "JO13" bij een club met zes JO13-teams) en is terugvragen aan de afzender
    /// correcter dan een AI-gok.
    /// </summary>
    private const int MaxKandidaten = 8;

    public async Task<int?> KiesAsync(string ruweTekst, IReadOnlyList<TeamCandidate> kandidaten, CancellationToken ct = default)
    {
        if (kandidaten.Count == 0) return null;
        if (kandidaten.Count == 1) return kandidaten[0].TeamId;

        if (kandidaten.Count > MaxKandidaten)
        {
            logger.LogInformation(
                "Teamdisambiguatie overgeslagen: {Aantal} kandidaten is te vaag (max {Max})",
                kandidaten.Count, MaxKandidaten);
            return null;
        }

        var opties = BouwOptiesTekst(kandidaten);

        const string systemPrompt = """
            Je koppelt een teamaanduiding uit een e-mail aan precies één team uit een gegeven lijst.

            Geef ALTIJD JSON terug met dit formaat:
            {
              "keuze": <nummer uit de lijst, of null>,
              "reden": "korte toelichting"
            }

            Regels:
            - "keuze" MOET een nummer uit de aangeboden lijst zijn, of null. Verzin nooit een team dat niet in de lijst staat.
            - Kies alleen als de aanduiding echt naar één team verwijst. Bij twijfel tussen twee teams: null.
            - Let op onderscheidende signalen: jongens/meisjes (JO/MO), leeftijd, teamnummer, zaal/veld (ZO), G-team.
            - Een aanduiding zonder JO/MO-prefix (bijv. "13-1") geeft geen uitsluitsel over jongens of meisjes: kies dan null tenzij er andere context is.
            """;

        var userPrompt = $"Teamaanduiding uit de e-mail: \"{ruweTekst}\"\n\nMogelijke teams:\n{opties}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var options = new ChatOptions
        {
            Temperature = 0.0f,
            ResponseFormat = ChatResponseFormat.Json
        };

        try
        {
            var response = await chatClient.GetResponseAsync(messages, options, ct);
            using var doc = JsonDocument.Parse(response.Text ?? "");
            var root = doc.RootElement;

            return ValideerEnParseKeuze(root, kandidaten);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Teamdisambiguatie mislukt — doorgaan zonder keuze");
            return null;
        }
    }

    private static string BouwOptiesTekst(IReadOnlyList<TeamCandidate> kandidaten)
        => string.Join("\n", kandidaten.Select((k, i) =>
            $"{i + 1}. {k.Teamnaam}{(string.IsNullOrWhiteSpace(k.LeeftijdsCategorie) ? "" : $" (categorie {k.LeeftijdsCategorie})")}"));

    private int? ValideerEnParseKeuze(JsonElement root, IReadOnlyList<TeamCandidate> kandidaten)
    {
        if (!root.TryGetProperty("keuze", out var keuzeElement)
            || keuzeElement.ValueKind is JsonValueKind.Null
            || !keuzeElement.TryGetInt32(out var keuze))
        {
            logger.LogInformation("Teamdisambiguatie: model gaf geen keuze — terugvragen aan afzender");
            return null;
        }

        // Harde validatie: het model kan een nummer buiten de lijst teruggeven.
        if (keuze < 1 || keuze > kandidaten.Count)
        {
            logger.LogWarning(
                "Teamdisambiguatie: keuze {Keuze} valt buiten de kandidatenlijst (1-{Max}) — genegeerd",
                keuze, kandidaten.Count);
            return null;
        }

        var gekozen = kandidaten[keuze - 1];
        logger.LogInformation("Teamdisambiguatie: gekozen TeamId={TeamId} uit {Aantal} kandidaten", gekozen.TeamId, kandidaten.Count);
        return gekozen.TeamId;
    }
}
