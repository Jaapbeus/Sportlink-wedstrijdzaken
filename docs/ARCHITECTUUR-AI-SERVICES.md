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
// OpenAI (huidig) — zoals werkelijk geregistreerd in FunctionApp.Postgres/Program.cs
// en FunctionApp/Program.cs
builder.Services.AddSingleton<IChatClient>(
    new ChatClient(aiModelName, new System.ClientModel.ApiKeyCredential(openAiApiKey))
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

### Migratiestatus — afgerond

> **Status: afgerond** (geverifieerd 19-09-2026). Alle productie-AI-aanroepen lopen via
> `IChatClient`. De enige plek in de codebase die nog een provider-klasse aanraakt is de
> DI-registratie zelf — precies zoals de grondregel voorschrijft.

| Component | Tier | Status |
|-----------|------|--------|
| `BerichtAiService` | beide (`FunctionApp.Postgres/Email/`, `FunctionApp/Email/`) | `IChatClient` via constructor |
| `Planner.Shared/Feedback/FeedbackCore` | gedeeld | `IChatClient` als methodeparameter |
| Provider-registratie | per tier één regel in `Program.cs` | `new ChatClient(aiModelName, …).AsIChatClient()` |
| Toekomstige AI-services | — | Altijd `IChatClient` vanaf aanmaak |

*(Tot #1268 stond hier ook `TeamDisambiguationAiService` — forced-choice teamdisambiguatie op de
SQL Server-tier. Die functionaliteit bestond alleen op die ene tier; de eigenaar koos het
deterministische gedrag van de Postgres-tier als norm voor beide, en de klasse is verwijderd. Zie
`docs/ARCHITECTUUR-TEAMRESOLUTIE.md` voor het volledige verhaal.)*

Een provider-wissel is daarmee inderdaad wat de grondregel belooft: **één regel per tier.**

**OpenAI SDK mag niet worden uitgebreid.** Nieuwe AI-aanroepen gaan via `IChatClient`.
Deze regel blijft normatief, ook nu de migratie klaar is: hij bewaakt dat er geen nieuwe
provider-afhankelijkheid binnensluipt.

---

## Datumregel: altijd dynamisch injecteren in de system prompt

### Waarom

Taalmodellen hebben geen betrouwbare kennis van de huidige datum. Zonder injectie
berekent het model relatieve datums ("aanstaande zaterdag") incorrect — of helemaal niet.

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

> **Status: geïmplementeerd (#604), op beide tiers.** `FunctionApp.Postgres/Program.cs` (de tier die
> in productie draait, #1060) **en** `FunctionApp/Program.cs` lezen de modelnaam uit de app setting
> `AiModelName` bij de `IChatClient`-registratie; ontbreekt die, dan valt hij terug op `gpt-4o-mini`.
> De fallback is toegestaan omdat het puur een provider-model-identifier is — geen club-specifieke
> waarde. De naam komt bewust **niet** uit de instellingentabel (`public.appsettings` op Postgres,
> `dbo.AppSettings` op SQL Server): de DI-registratie loopt bij host-start, vóór de eerste
> databaseverbinding.
>
> Zet de waarde lokaal in het `local.settings.json` van de tier waarop je werkt — standaard
> `FunctionApp.Postgres/local.settings.json`, voor de SQL Server-tier `FunctionApp/local.settings.json`
> (zie de bijbehorende template) — en in productie als Azure Function Application Setting.

---

## Jaarlijkse onderhoudsplicht: KNVB-regels

De `KnvbRegelsContext` constante in `BerichtAiService.cs` bevat KNVB-verplaatsingsregels
voor één specifiek seizoen (huidig: 2026/'27 — actueel op 19-09-2026). Deze datums zijn
jaarlijks verouderd. De constante staat **per tier** in een eigen kopie:
`FunctionApp.Postgres/Email/BerichtAiService.cs` en `FunctionApp/Email/BerichtAiService.cs`.
Werk beide bij.

**Verplichting:** bij elke nieuwe seizoensstart (augustus/september):
1. Controleer KNVB-website op gewijzigde verplaatsingsregels
2. Update `KnvbRegelsContext` met nieuwe deadlines én de seizoensdata uit de speeldagenkalender
3. Update de seizoensvermelding (`## KNVB-verplaatsingsregels seizoen 20XX/'YY`) **en de twee
   begeleidende velden die sinds #608 naast de constante staan**: `KnvbRegelsSeizoen`
   (nu `"2026/'27"`) en `KnvbRegelsGeldigTot` (nu 20 juni 2027). Voorbij die datum waarschuwt de
   code uit zichzelf — zowel de beheerder via het log als het model via een extra promptregel —
   dat de deadlines verlopen zijn en er geen `knvbNotitie` meer op gebaseerd mag worden.
4. Archiveer de nieuwe speeldagenkalender-PDF's in `docs/knvb-speeldagenkalenders/<seizoen>/`,
   plaats de PDF's die de app zelf meestuurt in `FunctionApp/Content/KnvbKalenders/<seizoen>/`
   (die map is gedeeld: `FunctionApp.Postgres.csproj` linkt hem, beide csproj's sluiten hem in),
   en seed de kalenderdagen **per tier**:
   - Postgres (productie): een nieuwe migratie onder `Database.Postgres/migrations/` die
     `public.knvbkalenderdag` vult — zie `019_knvbkalenderdag.sql` als model
   - SQL Server: `dbo.KnvbKalenderDag` via `Database/Script.PostDeployment1.sql`
5. Voeg CHANGELOG-entry toe onder `### Changed`

**Bronnen:**
- [KNVB verplaatsen van wedstrijden](https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/regelen-dagelijkse-praktijk/verplaatsen-van-wedstrijden)
- [KNVB speeldagenkalenders](https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/seizoensplanning/speeldagenkalenders)

**GitHub-herinnering:** maak elk jaar in augustus een issue aan met label `chore` en title
`"KNVB-regels bijwerken voor seizoen 20XX/'YY"`.

---

## Checklist bij elke AI-aanroep (codereview)

```
□ Gebruikt de service IChatClient (niet ChatClient of AnthropicClient direct)?
□ Is de huidige datum dynamisch geïnjecteerd in de system prompt?
□ Bevatten few-shot voorbeelden GEEN hardcoded absolute datums (bijv. "2026-05-19")?
□ Is de modelnaam configureerbaar (niet hardcoded)?
□ Zijn KNVB-datums (bij de classificatiedienst) hetzelfde seizoen als het huidige?
□ Is de wijziging op beide built-tiers doorgevoerd (FunctionApp.Postgres/ én FunctionApp/)?
□ Is AVG-compliance bewaard (geen persoonsgegevens in logs — zie AVG #210)?
```

---

*Laatste verificatie: v3.5.3.1 — 2026-09-19 (migratiestatus, modelnaam-registratie en
KNVB-onderhoudsrecept getoetst tegen beide tiers).*
