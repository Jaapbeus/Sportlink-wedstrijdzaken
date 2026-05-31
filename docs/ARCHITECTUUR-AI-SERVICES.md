# Architectuur — AI-services

Dit document definieert de architectuurregels voor alle AI-integraties in dit project.
Het is leidend bij elke toevoeging of wijziging die een LLM aanroept.

---

## Grondregel: provider-agnostisch vanaf dag één

Dit project integreert AI voor e-mailclassificatie en antwoordgeneratie. De provider
(OpenAI, Azure OpenAI, Anthropic Claude) is een implementatiedetail — de applicatiecode
mag daar niet van afhangen.

**Reden:** Providerkeuze verandert op basis van prijs, beschikbaarheid, AVG-compliance
(EU-hosting), of betere modellen. Een provider-wissel mag nooit meer zijn dan het
aanpassen van één DI-registratie en één configuratiewaarde.

---

## Abstractielaag: `IChatClient` (Microsoft.Extensions.AI)

### De interface

```csharp
// NuGet: Microsoft.Extensions.AI
using Microsoft.Extensions.AI;

// Productie-code gebruikt altijd IChatClient, nooit provider-specifieke klassen
public class BerichtAiService(IChatClient chatClient, ILogger<BerichtAiService> logger)
{
    // ...
}
```

### Provider-registratie via DI (één plek, één wissel)

```csharp
// OpenAI (huidig)
builder.Services.AddSingleton<IChatClient>(
    new OpenAIClient(apiKey)
        .GetChatClient("gpt-4o-mini")
        .AsIChatClient());

// Azure OpenAI (druppel-in vervanging)
builder.Services.AddSingleton<IChatClient>(
    new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
        .GetChatClient(deploymentName)
        .AsIChatClient());

// Anthropic Claude (officieel via NuGet: Anthropic)
builder.Services.AddSingleton<IChatClient>(
    new AnthropicClient { ApiKey = anthropicKey }
        .AsIChatClient("claude-sonnet-4-6"));
```

### Migratiestatus

| Component | Status | Actie |
|-----------|--------|-------|
| `BerichtAiService` | Directe OpenAI SDK | Migreren naar `IChatClient` bij volgende feature-branch |
| Toekomstige AI-services | — | Altijd `IChatClient` vanaf aanmaak |

**OpenAI SDK mag niet worden uitgebreid.** Nieuwe AI-aanroepen gaan via `IChatClient`.
Bestaande code wordt gemigreerd zodra er een reden is om de provider te aan te raken.

---

## Datumregel: altijd dynamisch injecteren in de system prompt

### Waarom

Taalmodellen hebben geen betrouwbare kennis van de huidige datum. Zonder injectie
berekent het model relatieve datums ("aanstaande zaterdag") incorrect — of niet at all.

**Bronnen die dit bevestigen:**
- Anthropic: claude.ai injecteert `{{currentDateTime}}` in elke system prompt
  ([system-prompts release notes](https://platform.claude.com/docs/en/release-notes/system-prompts))
- Microsoft: "Contextual information such as the current date" is explicitly een aanbevolen
  supporting-content categorie
  ([Azure AI Prompt Engineering](https://learn.microsoft.com/en-us/azure/ai-services/openai/concepts/prompt-engineering))
- OpenAI: datum-injectie is de community-standaard voor tijdgevoelige toepassingen

### De regel

> **Elke system prompt die tijdgevoelige instructies bevat — datumberekening, KNVB-deadlines,
> "aanstaande", "volgende week" — MOET de huidige datum als EERSTE instructie bevatten.**

```csharp
// Verplicht patroon voor tijdgevoelige system prompts
private static string BouwSystemPrompt(DateTime today)
{
    return $"""
        Vandaag is {today:dddd d MMMM yyyy}.

        [Verdere instructies...]
        """;
}

// Aanroep: geef DateTime door, genereer nooit intern
var systemPrompt = BouwSystemPrompt(DateTime.Now);
```

**Waarom `DateTime.Now` en niet `UtcNow`:** e-mailclassificatie werkt met Nederlandse
datums in lokale context (NL-timezone). Gebruik UTC alleen als de context dat vereist.

### Datumformaat in system prompts

Conform Microsoft's token-efficiëntie onderzoek: schrijf de maand uit.

| Formaat | Tokens | Gebruik |
|---------|--------|---------|
| `01/15/2025` | Meer | Nooit in prompts |
| `2025-01-15` | Neutraal | Alleen in gestructureerde output-instructies |
| `15 januari 2025` | Minder | In instructies en voorbeelden |
| `woensdag 15 januari 2025` | Weinig extra | Aanbevolen voor maximale context |

---

## Few-shot voorbeelden: geen absolute datums

### Het probleem

> "When adding few examples to a system prompt for function calling, the model uses
> the information from these examples as factual data."
> — OpenAI Developer Community

Een voorbeeld met `"2026-05-19"` in een system prompt kan het model laten denken
dat het 19 mei 2026 is, ook als de user-prompt een andere datum injecteert.

### De regel

> **Few-shot voorbeelden in system prompts bevatten NOOIT absolute datums.
> Gebruik dynamisch berekende datums (uit de `today`-parameter) of generieke beschrijvingen.**

```csharp
// GOED: dynamisch berekend vanuit today-parameter
var volgendeWeekMa = today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7);
if (volgendeWeekMa == today) volgendeWeekMa = today.AddDays(7);
var voorbeeld = $"als vandaag {today:dddd d MMMM} is → 'volgende week doordeweeks': " +
    $"[{volgendeWeekMa:yyyy-MM-dd}, {volgendeWeekMa.AddDays(1):yyyy-MM-dd}, ...]";

// FOUT: hardcoded jaar → veroudert zonder waarschuwing
// "vandaag zondag 18 mei → datums: [\"2026-05-19\",\"2026-05-20\",...]"

// GOED: generieke beschrijving zonder jaar
// "bijv. \"30 mei en 6 juni\" → twee datums in yyyy-MM-dd formaat met het lopende jaar"

// FOUT: hardcoded jaar in formaat-uitleg
// "\"30 mei en 6 juni\" → [\"2026-05-30\", \"2026-06-06\"]"
```

---

## Modelnaam: configureerbaar, niet hardcoded

```csharp
// FOUT: hardcoded model en provider
_chatClient = new ChatClient("gpt-4o-mini", apiKey);

// GOED: model komt uit configuratie
var modelName = Environment.GetEnvironmentVariable("AiModelName") ?? "gpt-4o-mini";
// provider-registratie via DI (zie boven)
```

Configureer via GitHub Variable `AI_MODEL_NAME` en Azure Function Application Settings.
Dit maakt model-upgrades (gpt-4o-mini → gpt-4.1-mini, etc.) zonder deployment mogelijk.

---

## Jaarlijkse onderhoudsplicht: KNVB-regels

De `KnvbRegelsContext` constante in `BerichtAiService.cs` bevat KNVB-verplaatsingsregels
voor één specifiek seizoen (bijv. 2025/'26). Deze datums zijn jaarlijks verouderd.

**Verplichting:** bij elke nieuwe seizoensstart (augustus/september):
1. Controleer KNVB-website op gewijzigde verplaatsingsregels
2. Update `KnvbRegelsContext` met nieuwe deadlines
3. Update de seizoensvermelding (`## KNVB-verplaatsingsregels seizoen 20XX/'YY`)
4. Voeg CHANGELOG-entry toe onder `### Changed`

**Bron:** [KNVB verplaatsen van wedstrijden](https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/regelen-dagelijkse-praktijk/verplaatsen-van-wedstrijden)

**GitHub-herinnering:** maak elk jaar in augustus een issue aan met label `chore` en title
`"KNVB-regels bijwerken voor seizoen 20XX/'YY"`.

---

## Checklist bij elke AI-aanroep (codereview)

```
□ Gebruikt de service IChatClient (niet ChatClient of AnthropicClient direct)?
□ Is de huidige datum dynamisch geïnjecteerd in de system prompt?
□ Bevatten few-shot voorbeelden GEEN hardcoded absolute datums (bijv. "2026-05-19")?
□ Is de modelnaam configureerbaar (niet hardcoded)?
□ Zijn KNVB-datums (bij klassieficatiedienst) hetzelfde seizoen als het huidige?
□ Is AVG-compliance bewaard (geen persoonsgegevens in logs — zie AVG #210)?
```

---

*Laatste verificatie: v2.7.0.1 — 2026-05-31*
