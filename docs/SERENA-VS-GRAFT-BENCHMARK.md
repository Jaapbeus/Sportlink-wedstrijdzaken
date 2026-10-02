# Serena vs Graft — Repository-specifiek benchmark

**Doel:** Archivering van een repository-specifieke benchmark waarom dit project Serena gebruikt, waar Graft sterker is, en hoe de metingen tot stand kwamen. Dit document richt zich op context-ophaal-workflows voor ontwikkelaars — geen productpatches, geen geïmplementeerde features.

---

## Benchmarkcontext

| Kenmerk | Waarde |
|---------|--------|
| Repository | `Jaapbeus/Sportlink-wedstrijdzaken` |
| Testrevision | `592f86fd39ad7cf27d498392ad2a3d742d8a81b0` (lokale HEAD en GitHub-clone kwamen overeen) |
| Testdatum | 2026-09-28 |
| Serena-versie | 1.7.0 met LSP/Roslyn; indexeerde 515 C#-bestanden; kon `Database/SportlinkSqlDb.sqlproj` niet laden |
| Graft-versie | 0.20.0; graph-index 519 bestanden, 6.685 nodes en 3.650 edges |

---

## Workflowvergelijking

Vijf workflows voor context-ophaal vóór een wijziging, getokeniseerd met dezelfde o200k_base-tokenizer:

| Workflow | Serena (tokens) | Graft (tokens) | Observatie |
|----------|----------------:|---------------:|-----------|
| Nieuwe helper in `TeamNaamNormalisatie` | 382 | 884 | Beide gaven normalizer-API en 19 testnamen; Graft gaf ook regels/signaturen, Serena was compacter. |
| Refactor `NormaliseerVoorVergelijking` | 4.711 | 4.511 | `rg --no-ignore` ground truth: 43 tekstmatches in 15 C#-bestanden. Graft vond alle 43/15 lexicaal. Serena gaf 40 semantische referentiecontexten in 14 bestanden; het ontbrekende bestand bevatte XML-documentatie. |
| Root-cause context voor regressie #766 | 1.252 | 408 | Serena gaf implementatie- en regressietestmethoden; Graft vond verklarende regels maar geen complete method bodies, zodat extra bronlezing nodig kan zijn. |
| Bugfix-context: verouderd async teamlookup-resultaat | 1.034 | 847 | Beide vonden `LookupGeneratieGuard`, consumer `Teambegeleiding` en drie tests; Graft was compacter, Serena gaf omringende snippets. |
| Wrapped Sportlink-endpoints over beide DB-tiers | 2.526 | 2.619 | Serena gebruikte tier-specifieke Roslyn-queries en semantische call sites. Graft vond 37 tekstmatches in 14 bestanden, inclusief comments en dubbele wrapper-definities. |
| **Totaal** | **9.905** | **9.269** | Graft produceerde ongeveer 6,4% minder antwoordtekst; voor root-cause-context was mogelijk een vervolgbronlezing nodig. |

---

## MCP Tool-schemaomvang

Gemeten serialisatie van `tools/list`:

- **Serena**: 21 tools, 22.724 tekens / circa 5.013 o200k_base tokens
- **Graft**: 6 tools, 3.348 tekens / circa 751 tokens

**Opmerking:** Dit is een schatting van schema-tekst, geen directe meting van clientprompt of facturatie. Clients kunnen definities filteren of cachen.

---

## Regressiechecks tijdens het onderzoek

Gerunt ter verificatie dat huidige gedrag onveranderd was:

- `Planner.Shared.Tests`, filter `SpatieTussenLeeftijdEnTeamnummer`: 7 geslaagd, 0 mislukt
- `BlazorAdmin.Tests`, filter `LookupGeneratieGuardTests`: 3 geslaagd, 0 mislukt
- **Setup-opmerking:** Eerste uitvoering kon niet starten wegens ontbrekende .NET 9 runtime. Met `DOTNET_ROLL_FORWARD=Major` draaiden de tests op aanwezige .NET 10 runtime en slaagden. Dit controleert huidige regressiegedragingen, niet de codeervaardigheid van Serena of Graft.

---

## Interpretatie

### Sterke punten per tool

**Serena:**
- Precieze C#-symbolnavigatie en refactorscope
- Tier-specifieke callers via Roslyn semantische analyse
- Volledige method bodies en omringende context
- Geschikt voor complexe refactoreringen waar semantiek essentieel is

**Graft:**
- Compacte repo-brede oriëntatie
- Volledige lexicale dekking van exacte identifiers
- Werkstekst-matching over meerdere bestanden
- Geschikt voor snelle context-opbouw bij breed bereik

### Graftbesparing en beperkingen

- **Outputtoken-besparing:** Graft produceerde ongeveer 6,4% minder antwoordtekst in deze vijf workflows.
- **Tool-schema:** Graft had een aanzienlijk kleiner tool-schema (751 vs 5.013 tokens), maar werkelijk gefactureerde tokens hangen af van client-side filtering en caching.
- **Geen universele winnaar:** De keuze hangt af van de taak — semantische nauwkeurigheid (Serena) vs. lexicale snelheid en omvang (Graft).

### Benchmarkgrenzen — expliciet benoemd

Dit onderzoek test **context gathering en retrieval**, niet:
- Werkelijk gebouwde features of patchkwaliteit
- Daadwerkelijke codewijzigingen of ontwikkeltijd
- Productiegebruik of lange-termijnonderhoud
- Real-time token-facturatie onder productie-belasting

De vijf workflows waren zoek-/contexttaken; geen van beide tools is gebruikt om werkelijk code te schrijven of testen uit te voeren.

---

## Praktische richtlijnen (do's en don'ts)

### Gebruik Serena voor:
- **Symbol-navigatie:** Definiëring van klassen, methoden en tier-specifieke interfaces
- **Refactorscope:** Wat moet er veranderen in welke files voor een veilige rename/extract?
- **Root-cause analyse:** Waar komt deze stack trace vandaan, wat zijn de contextuele method bodies?
- **Tier-afhankelijke logica:** SQL Server vs Postgres — wat verschilt?

### Gebruik Graft voor:
- **Letterlijke tekstzoekactie:** Alle bestanden die string X bevatten
- **Snelle repo-orientatie:** "Wat zit er al in deze repo?"
- **Duplicatie-opsporing:** Welke bestanden hebben dezelfde code-blokken?
- **Compacte kennisgraaf:** Waar is X gedefinieerd, wie gebruikt het?

### Validatiestappen (beide tools):
1. **Comments en XML-docs checken:** Serena kan XML-docs missen; valideer handmatig
2. **Alle tiers verifiëren:** Beide tools rapporteren per tier — controleer beide databasetiers apart
3. **Hits tegen repo-source valideren:** Geen "gokken" op tool-output; citeer exact wat je vindt
4. **Method-context voor bugfixes:** Serena is meer geschikt voor complete method bodies; Graft kan extra handmatige bronlezing vereisen

---

## Bronnen

- **Serena:** https://github.com/oraios/serena — MCP-toolkit voor semantic code analysis en editing
- **Graft:** https://github.com/trailhq/Graft — Context-laag voor code-agent tokens en accuraatheid

Beide repositories bevatten uitgebreide documentatie en voorbeelden.

---

## Conclusie

**Dit project gebruikt Serena** omdat de tier-afhankelijke C# semantiek en volledige method-bodies essentieel zijn voor veilige refactoring in een multi-tier architecture. **Graft kan aanvullend nuttig zijn** voor brede tekst-zoekacties en snelle orientatie, maar vereist mogelijk extra bronlezing voor diepe root-cause analyse.

De gemeten output-token besparing van ongeveer 6% is bescheiden, maar toolschema-omvang verschilt aanzienlijk. Beide tools complementeren elkaar; dit benchmark informeert de keuze per context-taak in plaats van voor één tool vast uit te gaan.
