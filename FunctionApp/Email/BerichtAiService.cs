using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace SportlinkFunction.Email;

/// <summary>
/// Service voor het classificeren van inkomende emails en het genereren van antwoorden
/// met behulp van OpenAI GPT-4o-mini.
/// </summary>
public class BerichtAiService
{
    private readonly ILogger<BerichtAiService> _logger;
    private readonly ChatClient _chatClient;

    // KNVB-verplaatsingsregels voor seizoen 2025/'26 — wordt door AI gebruikt om overtreding te signaleren
    // Bron: https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/regelen-dagelijkse-praktijk/verplaatsen-van-wedstrijden
    private const string KnvbRegelsContext = """
        ## KNVB-verplaatsingsregels seizoen 2025/'26

        ### Aanvangstijdwijzigingen
        - Tot 8 dagen voor wedstrijd: aanpasbaar via Sportlink Club (geen KNVB-goedkeuring nodig)
        - Na 8 dagen voor wedstrijd: onderling overleg + KNVB-goedkeuring vereist
        - Let op (strenger): standaardteams mannen, vrouwen topklasse t/m 1e klasse, landelijke jeugddivisies

        ### Datumverplaatsing — algemeen
        - Aanvraag uiterlijk 3 dagen voor wedstrijdaanvang (via Sportlink Club)
        - Beide clubs moeten instemmen; KNVB kan afwijzen

        ### Categorie A (strenge regels)
        Geldt voor: mannen senioren standaard, vrouwen top/hoofd/1e klasse, landelijke jeugddivisies + hoofdklasse
        - Mannen/vrouwen senioren: GEEN verplaatsing na 1 mei 2026
        - Vrouwen 2e klasse: GEEN verplaatsing na 1 mei 2026
        - Jeugd divisies + hoofdklasse najaar: deadline 13 december 2025
        - Jeugd divisies + hoofdklasse voorjaar: deadline 16 mei 2026
        - Laatste 2 wedstrijddagen van de competitie: GEEN verplaatsing

        ### Categorie B (flexibeler — onderling overleg)
        Geldt voor: pupillen, junioren regionaal, senioren 3e klasse en lager, vrouwen 3e klasse
        - Senioren 21 sep–31 dec: verplaatst uiterlijk 31 januari 2026
        - Senioren 1 jan–1 jun: verplaatst uiterlijk 21 juni 2026
        - Vrouwen 3e klasse: uiterlijk 9 mei 2026; geen verplaatsing na 1 mei
        - Pupillen (O7–O12): voor volgende fase schriftelijk vastleggen

        ### Snipperdagen (alleen Categorie B)
        - Max 1 per team per seizoen
        - Aanvraag uiterlijk dinsdag 23:59 van de voorafgaande week
        - Periode: seizoenstart t/m eerste volledige weekend maart 2026
        - NIET voor: beker, O7–O12, MO13–MO20

        ### Bekerwedstrijden
        - Onderling overleg + KNVB-goedkeuring; moet voor de bekerronde plaatsvinden
        """;

    private static string BouwClassificatieSystemPrompt()
    {
        var clubNaam = SystemUtilities.AppSettings.GetSetting("clubName") ?? "de thuisclub";

        return $$"""
            Je bent een assistent voor de coördinator thuiswedstrijden van {{clubNaam}}.
            Analyseer de inkomende email en classificeer het verzoek.

            Typen verzoeken:
            - beschikbaarheid_check: iemand vraagt of een datum/tijd/veld beschikbaar is (bijv. voor een oefenwedstrijd of veldreservering). Ook als er MEERDERE datums worden gevraagd voor hetzelfde team.
            - herplan_verzoek: iemand wil een bestaande wedstrijd verplaatsen naar een andere datum/tijd
            - bevestiging: een antwoord op een eerder voorstel ("ja dat is goed", "akkoord", etc.)
            - buiten_scope: alles wat niet over veldbeschikbaarheid of herplannen gaat, OF als de email over meerdere VERSCHILLENDE teams gaat en het onduidelijk is welke wedstrijd bedoeld wordt

            Geef ALTIJD een JSON response met exact dit formaat:
            {
              "type": "beschikbaarheid_check | herplan_verzoek | bevestiging | buiten_scope",
              "datum": "yyyy-MM-dd of null (eerste/primaire datum)",
              "datums": ["yyyy-MM-dd", ...] of null (ALLE gevraagde datums als er meerdere zijn),
              "aanvangsTijd": "HH:mm of null",
              "gewensteDatum": "yyyy-MM-dd of null",
              "teamNaam": "teamnaam of null",
              "leeftijdsCategorie": "bijv. JO11 of null",
              "tegenstander": "naam tegenstander of null",
              "samenvatting": "korte samenvatting van het verzoek",
              "namensWie": "afzender | tegenstander | onbekend",
              "knvbNotitie": "korte notitie als het verzoek mogelijk een KNVB-regel overtreedt, anders null"
            }

            KRITIEKE REGELS:
            - Het ONDERWERP van de email bevat vaak de meest betrouwbare datum en teamnamen. Gebruik datums uit het onderwerp als eerste bron.
            - "datum" = de eerste/primaire datum. Bij herplan_verzoek is dit de HUIDIGE wedstrijddatum, NIET de gewenste nieuwe datum.
            - "datums" = array met ALLE gevraagde datums als er meerdere zijn (bijv. "30 mei en 6 juni" → ["2026-05-30", "2026-06-06"]). Vul dit veld ALTIJD als er meerdere datums worden genoemd.
            - "gewensteDatum" = de datum waarnaar men wil verplaatsen (alleen bij herplan_verzoek). Kan null zijn als niet genoemd.
            - Datums in emails zijn vaak relatief ("aanstaande zaterdag") — bereken de absolute datum op basis van vandaag
            - Nederlandse tekst, informeel taalgebruik
            - Emails van interne clubcontacten worden doorgestuurd of komen van een club-emailadres
            - Bij doorgestuurde berichten: bepaal namens wie het verzoek is
            - Leeftijdscategorieën: "O13", "Onder 13", "onder 13" etc. normaliseren naar "JO13". Idem voor alle leeftijden (O7→JO7, O19→JO19, etc.). Meisjes: "MO13" blijft "MO13"
            - Meerdere datums voor hetzelfde team = beschikbaarheid_check (NIET buiten_scope)
            - Alleen buiten_scope als het verzoek echt niet over veldbeschikbaarheid of herplannen gaat, of als er meerdere VERSCHILLENDE teams worden genoemd zonder duidelijk verband
            - 'doordeweeks' betekent maandag t/m donderdag (vrijdag is GEEN doordeweekse dag). Bij 'volgende week doordeweeks': vul 'datums' met ALLE VIER weekdagen (ma/di/wo/do) van de volgende kalenderweek. Voorbeeld: vandaag zondag 18 mei → 'volgende week doordeweeks' → datums: ["2026-05-19","2026-05-20","2026-05-21","2026-05-22"]

            KNVB-regelcheck (voor herplan_verzoek):
            Vul "knvbNotitie" in als op basis van datum en teamtype een KNVB-regel waarschijnlijk van toepassing is.
            Wees kort (1-2 zinnen). Voorbeeld: "Senioren mogen na 1 mei 2026 geen wedstrijden meer verplaatsen (KNVB Cat A)."
            Laat null als datum ruim voor eventuele deadlines valt of het teamtype niet duidelijk is.

            {{KnvbRegelsContext}}
            """;
    }


    public BerichtAiService(ILogger<BerichtAiService> logger)
    {
        _logger = logger;

        var apiKey = Environment.GetEnvironmentVariable("OpenAiApiKey")
            ?? throw new InvalidOperationException("OpenAiApiKey environment variable is niet geconfigureerd.");

        _chatClient = new ChatClient("gpt-4o-mini", apiKey);
    }

    /// <summary>
    /// Classificeert een inkomend bericht met behulp van GPT-4o-mini.
    /// Retourneert een BerichtClassificatie met het type verzoek en geëxtraheerde gegevens.
    /// </summary>
    public async Task<BerichtClassificatie> ClassificeerBerichtAsync(string body, string subject, string afzender)
    {
        _logger.LogInformation("Bericht classificatie gestart voor onderwerp: {Subject}", subject);

        var userPrompt = $"Vandaag is {DateTime.Now:yyyy-MM-dd} ({DateTime.Now:dddd}).\n\nVan: {afzender}\nOnderwerp: {subject}\n\n{body}";

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(BouwClassificatieSystemPrompt()),
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
            _logger.LogError(ex, "Fout bij het classificeren van bericht met onderwerp: {Subject}", subject);
            throw;
        }
    }

    private static BerichtClassificatie ParseClassificatieResponse(string jsonResponse)
    {
        using var doc = JsonDocument.Parse(jsonResponse);
        var root = doc.RootElement;

        var typeString = root.GetProperty("type").GetString() ?? "buiten_scope";
        var namensWieString = root.GetProperty("namensWie").GetString() ?? "onbekend";

        return new BerichtClassificatie
        {
            Type = MapVerzoekType(typeString),
            Datum = GetOptionalString(root, "datum"),
            AanvangsTijd = GetOptionalString(root, "aanvangsTijd"),
            GewensteDatum = GetOptionalString(root, "gewensteDatum"),
            Datums = GetOptionalStringArray(root, "datums"),
            TeamNaam = GetOptionalString(root, "teamNaam"),
            LeeftijdsCategorie = GetOptionalString(root, "leeftijdsCategorie"),
            Tegenstander = GetOptionalString(root, "tegenstander"),
            Samenvatting = root.GetProperty("samenvatting").GetString() ?? "",
            NamensWie = MapNamensWie(namensWieString),
            KnvbNotitie = GetOptionalString(root, "knvbNotitie")
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

    private static List<string>? GetOptionalStringArray(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Array)
        {
            var result = new List<string>();
            foreach (var item in prop.EnumerateArray())
            {
                var val = item.GetString();
                if (!string.IsNullOrEmpty(val) && val != "null")
                    result.Add(val);
            }
            return result.Count > 0 ? result : null;
        }
        return null;
    }
}
