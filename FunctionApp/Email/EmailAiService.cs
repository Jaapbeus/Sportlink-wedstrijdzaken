using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace SportlinkFunction.Email;

/// <summary>
/// Service voor het classificeren van inkomende emails en het genereren van antwoorden
/// met behulp van OpenAI GPT-4o-mini.
/// </summary>
public class EmailAiService
{
    private readonly ILogger<EmailAiService> _logger;
    private readonly ChatClient _chatClient;

    private const string ClassificatieSystemPrompt = """
        Je bent een assistent voor de coördinator thuiswedstrijden van voetbalvereniging VRC Veenendaal.
        Analyseer de inkomende email en classificeer het verzoek.

        Typen verzoeken:
        - beschikbaarheid_check: iemand vraagt of een datum/tijd/veld beschikbaar is voor een oefenwedstrijd
        - herplan_verzoek: iemand wil een bestaande wedstrijd verplaatsen naar een andere datum/tijd
        - bevestiging: een antwoord op een eerder voorstel ("ja dat is goed", "akkoord", etc.)
        - buiten_scope: alles wat niet over veldbeschikbaarheid of herplannen gaat

        Geef ALTIJD een JSON response met exact dit formaat:
        {
          "type": "beschikbaarheid_check | herplan_verzoek | bevestiging | buiten_scope",
          "datum": "yyyy-MM-dd of null",
          "aanvangsTijd": "HH:mm of null",
          "teamNaam": "teamnaam of null",
          "leeftijdsCategorie": "bijv. JO11 of null",
          "tegenstander": "naam tegenstander of null",
          "samenvatting": "korte samenvatting van het verzoek",
          "namensWie": "afzender | tegenstander | onbekend"
        }

        Let op:
        - Datums in emails zijn vaak relatief ("aanstaande zaterdag") — bereken de absolute datum op basis van vandaag
        - Nederlandse tekst, informeel taalgebruik
        - Als afzender @vv-vrc.nl domein heeft: VRC-intern
        - Bij doorgestuurde berichten: bepaal namens wie het verzoek is
        - Leeftijdscategorieën: "O13", "Onder 13", "onder 13" etc. normaliseren naar "JO13". Idem voor alle leeftijden (O7→JO7, O19→JO19, etc.). Meisjes: "MO13" blijft "MO13"
        """;

    private const string AntwoordSystemPrompt = """
        Je bent de VRC Veldplanner, een geautomatiseerd systeem dat antwoordt namens de coördinator thuiswedstrijden van voetbalvereniging VRC Veenendaal.

        Schrijfstijl:
        - Kort en duidelijk, geen technische details
        - Gebruik de tijdsgebonden aanhef (Goedemorgen/Goedemiddag/Goedenavond) gevolgd door de voornaam
        - Maximaal 2-3 alternatieven noemen als de gevraagde tijd niet beschikbaar is
        - Voeg GEEN afsluiting of handtekening toe (geen "Met vriendelijke groet" etc.) — die wordt automatisch toegevoegd
        """;

    public EmailAiService(ILogger<EmailAiService> logger)
    {
        _logger = logger;

        var apiKey = Environment.GetEnvironmentVariable("OpenAiApiKey")
            ?? throw new InvalidOperationException("OpenAiApiKey environment variable is niet geconfigureerd.");

        _chatClient = new ChatClient("gpt-4o-mini", apiKey);
    }

    /// <summary>
    /// Classificeert een inkomende email met behulp van GPT-4o-mini.
    /// Retourneert een EmailClassificatie met het type verzoek en geëxtraheerde gegevens.
    /// </summary>
    public async Task<EmailClassificatie> ClassificeerEmailAsync(string body, string subject, string afzender)
    {
        _logger.LogInformation("Email classificatie gestart voor onderwerp: {Subject}", subject);

        var userPrompt = $"Vandaag is {DateTime.Now:yyyy-MM-dd} ({DateTime.Now:dddd}).\n\nVan: {afzender}\nOnderwerp: {subject}\n\n{body}";

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(ClassificatieSystemPrompt),
            new UserChatMessage(userPrompt)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0.1f,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        try
        {
            var completion = await _chatClient.CompleteChatAsync(messages, options);
            var jsonResponse = completion.Value.Content[0].Text;

            _logger.LogInformation("OpenAI classificatie response ontvangen");

            var classificatie = ParseClassificatieResponse(jsonResponse);
            return classificatie;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij het classificeren van email met onderwerp: {Subject}", subject);
            throw;
        }
    }

    /// <summary>
    /// Genereert een antwoord-email op basis van de classificatie en planner response.
    /// </summary>
    public async Task<string> GenereerAntwoordAsync(
        EmailClassificatie classificatie,
        string plannerResponseJson,
        string afzenderNaam)
    {
        _logger.LogInformation("Antwoord generatie gestart voor type: {Type}", classificatie.Type);

        var userPrompt = $"""
            Classificatie van het verzoek:
            - Type: {classificatie.Type}
            - Samenvatting: {classificatie.Samenvatting}
            - Team: {classificatie.TeamNaam ?? "onbekend"}
            - Datum: {classificatie.Datum ?? "niet opgegeven"}
            - Tijd: {classificatie.AanvangsTijd ?? "niet opgegeven"}
            - Tegenstander: {classificatie.Tegenstander ?? "onbekend"}

            Planner response:
            {plannerResponseJson}

            Naam afzender: {afzenderNaam}
            """;

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(AntwoordSystemPrompt),
            new UserChatMessage(userPrompt)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0.5f
        };

        try
        {
            var completion = await _chatClient.CompleteChatAsync(messages, options);
            var antwoord = completion.Value.Content[0].Text;

            _logger.LogInformation("Antwoord-email gegenereerd");
            return antwoord;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij het genereren van antwoord-email");
            throw;
        }
    }

    /// <summary>
    /// Parst de JSON response van OpenAI naar een EmailClassificatie object.
    /// </summary>
    private static EmailClassificatie ParseClassificatieResponse(string jsonResponse)
    {
        using var doc = JsonDocument.Parse(jsonResponse);
        var root = doc.RootElement;

        var typeString = root.GetProperty("type").GetString() ?? "buiten_scope";
        var namensWieString = root.GetProperty("namensWie").GetString() ?? "onbekend";

        return new EmailClassificatie
        {
            Type = MapVerzoekType(typeString),
            Datum = GetOptionalString(root, "datum"),
            AanvangsTijd = GetOptionalString(root, "aanvangsTijd"),
            TeamNaam = GetOptionalString(root, "teamNaam"),
            LeeftijdsCategorie = GetOptionalString(root, "leeftijdsCategorie"),
            Tegenstander = GetOptionalString(root, "tegenstander"),
            Samenvatting = root.GetProperty("samenvatting").GetString() ?? "",
            NamensWie = MapNamensWie(namensWieString)
        };
    }

    private static VerzoekType MapVerzoekType(string type) => type switch
    {
        "beschikbaarheid_check" => VerzoekType.BeschikbaarheidCheck,
        "herplan_verzoek" => VerzoekType.HerplanVerzoek,
        "bevestiging" => VerzoekType.Bevestiging,
        _ => VerzoekType.BuitenScope
    };

    private static NamensWie MapNamensWie(string namensWie) => namensWie switch
    {
        "afzender" => NamensWie.Afzender,
        "tegenstander" => NamensWie.Tegenstander,
        _ => NamensWie.Onbekend
    };

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind != JsonValueKind.Null)
        {
            var value = prop.GetString();
            return value == "null" ? null : value;
        }
        return null;
    }
}
