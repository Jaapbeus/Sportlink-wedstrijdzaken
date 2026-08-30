---
description: Voer de end-to-end zelftest van een database-tier uit — container, schema, demodata, browsersweep over alle pagina's en een begrensde fix-lus. Argument is de tier.
disable-model-invocation: true
argument-hint: [postgres|sqlserver] [--baseline] [--no-fix]
---

Voer de zelftest van een database-tier uit (#851). Dit is het bewijs dat een tier-omzetting
werkt — niet een afvinklijst, maar een draaiende applicatie met zichtbare demodata.

Symbolen:
- ✅ Poort geslaagd
- 🚧 Poort geblokkeerd door een bekend, genummerd defect — geen fout van deze run
- ❌ Poort gefaald

---

## Rolverdeling — lees dit eerst

| Wie | Doet wat |
|---|---|
| `scripts/dev/Test-PostgresTier.ps1` | Alles wat met een exitcode te bewijzen is: containers, schema, demodata, rijtellingen, herkomst, API, opruimen |
| **Deze skill** | De browsersweep (G7), de schrijfpaden door de GUI (G8), en de fix-lus |

**Verzin niets.** Alle routes, asserties en verwachte aantallen staan in
`scripts/dev/selftest-expectations.psd1`, en het script schrijft ze per run weg naar
`artifacts/selftest/<run>/skill-opdracht.json`. Staat een route daar niet in, dan is die route
`UNTESTED` — nooit `PASS`.

---

## FASE A — Script draaien

```powershell
./scripts/dev/Test-PostgresTier.ps1 -Tier <tier> -Mode <Baseline|Verify>
```

Lees daarna `artifacts/selftest/<run>/report.json`. Drie mogelijke uitkomsten:

| Exitcode | Betekenis | Wat jij doet |
|---|---|---|
| 0 | Alle uitgevoerde poorten geslaagd | Door naar Fase B |
| 2 | De implementatieboom van deze tier bestaat nog niet | **Stop.** Rapporteer dat, met het issuenummer uit het rapport. Dit is geen fout maar de verwachte stand |
| 1 | Een poort gefaald | Naar Fase D (fix-lus) |

> **Zonder basismeting geen oordeel.** Draai bij een `Verify`-run altijd eerst
> `-Mode Baseline` tegen de bestaande tier, of gebruik een eerdere basismeting. Zonder die
> vergelijking kun je een fout die de omzetting veroorzaakt niet onderscheiden van een fout die er
> al was — en die informatie is achteraf niet meer te reconstrueren.

---

## FASE B — Browsersweep (G7)

Vereist: de applicatie draait (`./scripts/dev/Start-Debug.ps1`) en Fase A is geslaagd.

Voor **elke** route uit `skill-opdracht.json`, in de context die daar staat (`demo` of `primair`):

1. **Zet de clubcontext.** Voor `demo`: gebruik de testmodus-knop in de zijbalk, of zet de
   clubcode in `localStorage` en herlaad. Controleer daarna dat de header de democlub toont —
   anders meet je de verkeerde dataset.
2. `mcp__playwright__browser_navigate` naar de route.
3. `mcp__playwright__browser_snapshot` — wacht tot de inhoud er is, niet alleen tot de pagina laadt.
4. Verzamel **vier onafhankelijke signalen**. Alle vier moeten goed zijn:

   | Signaal | Hoe | Eis |
   |---|---|---|
   | Console | `mcp__playwright__browser_console_messages` | nul berichten van type `error` |
   | Foutbanner | `mcp__playwright__browser_evaluate` op `document.querySelector('#blazor-error-ui')` | niet zichtbaar (`offsetParent === null`) — de div staat er áltijd, alleen zichtbaarheid telt |
   | Netwerk | `mcp__playwright__browser_network_requests` | geen enkele respons met status 400 of hoger |
   | Server | lees het applicatielog vanaf de byte-positie van vóór het navigeren | geen nieuwe exceptie |

5. **Controleer de inhoudsassertie** uit `skill-opdracht.json`. Dit is de belangrijkste stap:
   een lege pagina geeft geen foutmelding en is dus zonder deze controle groen.
6. `mcp__playwright__browser_take_screenshot` → `artifacts/selftest/<run>/pages/<route>.png`.

**Daarna de negatieve controle.** Navigeer naar de route uit `negativeControlRoute` — die bestaat
niet. Verwacht: de niet-gevonden-pagina. Krijgt die route dezelfde uitkomst als een echte pagina,
dan meet je methode niets en is **de hele browsersweep ongeldig**. Meld dat als een blokkade, niet
als één gefaalde route.

> **Waarom dit zo streng moet.** Blazor WebAssembly geeft op elke route dezelfde pagina met
> statuscode 200 terug. `Test-App.ps1` test vandaag nog twee routes die niet meer bestaan, en die
> staan al maanden op groen. Een statuscode bewijst hier niets; alleen wat er daadwerkelijk
> gerenderd is telt.

Markeer per route: `PASS`, `FAIL`, `UNTESTED` (geen demodata in de brontabel) of `OUT-OF-SCOPE`
(vereist een externe dienst).

---

## FASE C — Schrijfpaden (G8)

Een sweep over leespagina's raakt geen enkele invoegbewerking. Juist daar breken upserts,
volgnummers en parameterbinding.

Voer voor elke ronde uit `crudCases` een volledige cyclus uit, en verifieer op **drie** niveaus:

1. de aanroep slaagt;
2. de waarde is na een **harde herlaad** in de GUI zichtbaar;
3. de rij staat werkelijk in de database.

Alleen alle drie samen is groen. Alles wat je aanmaakt krijgt het voorvoegsel uit `crudPrefix`.

Doe daarnaast **één bewuste foutieve invoer** (een eindtijd vóór de begintijd). Verwacht: een
afwijzing én een ongewijzigde rij. Een omzetting die de validatie verliest maar wél schrijft, is
anders groen.

Sluit af met de controle dat de rijtellingen uit G4 exact hersteld zijn. Lekkende testdata is een
fout, geen bijzaak.

---

## FASE D — Fix-lus

Alleen als een poort rood is. **Maximaal 3 iteraties**, daarna stoppen en rapporteren.

### Wat je zelf mag aanpassen

Uitsluitend binnen de implementatieboom van de tier die je test, de bijbehorende demodata, en de
zelftest zelf. Typisch: dialectvertalingen, casing-conformiteit, typeafbeeldingen,
sleutelafleiding, de seedvertaling.

### Wat je nooit zelf aanpast — hier stop je en escaleer je

- **Security.** Een authenticatiepoort verzwakken, een certificaatcontrole uitzetten, een
  wachtwoord in een bestand zetten.
- **Een architectuurkeuze.** Een gedeelde providerabstractie of engine-detectie op runtime
  introduceren is expliciet verboden in `docs/ARCHITECTUUR-DATABASE-TIERS.md` §2.
- **De bestaande, draaiende tier.** Een wijziging daar om een nieuwe tier groen te krijgen is een
  regressie in wording.
- **Een cloudresource aanmaken of opwaarderen.** Kostenbeleid, zie CLAUDE.md.
- **Een assertie verzwakken of schrappen.** Blijkt een assertie zélf fout, dan mag je hem
  corrigeren, maar: aparte commit, expliciete motivering in het rapport, en meer dan twee van dit
  soort correcties in één run betekent automatisch escaleren. Anders schuift de test naar de code
  toe in plaats van andersom.
- **De basismeting aanpassen.** Nooit.

### De regel die de lus eerlijk houdt

**Na elke fix opnieuw vanaf een verse database.** Niet hervatten op de plek van de fout:

```powershell
./scripts/dev/Test-PostgresTier.ps1 -Teardown
./scripts/dev/Test-PostgresTier.ps1 -Tier <tier> -Mode Verify
```

Een fix kan de al gemigreerde toestand repareren terwijl het migratiepad zelf kapot blijft. Dan is
de volgende verse installatie alsnog stuk, en heeft de groene run je voorgelogen. Dit is dezelfde
reden waarom de bestaande CI het deployscript twee keer draait.

**Geen voortgang is een stop.** Levert een iteratie geen verandering op in de verzameling gefaalde
asserties, dan telt hij als mislukt en ga je direct escaleren — niet nog een keer proberen.

---

## FASE E — Rapporteren

Schrijf `artifacts/selftest/<run>/report.md`:

```
ZELFTEST <tier> — <datum>  |  commit <sha>  |  UITKOMST: <GROEN|ROOD|GEBLOKKEERD>
Basismeting: <bestand>  (vergelijking geldig / verouderd)

G0 preflight   ✅   6 asserties
G1 database    ✅   3 asserties
...
G7 sweep       ❌   11/13 routes  — 2 UNTESTED (geen demodata)

Nieuw t.o.v. de basismeting: <n>    Al aanwezig in de basismeting: <n>
Fixes: <n>    Escalaties: <n>
```

Vier categorieën bij de vergelijking, en de vierde is de belangrijkste:

| Categorie | Betekenis |
|---|---|
| Nieuw in deze tier | Blokkerend — dit veroorzaakt de omzetting |
| Al aanwezig in de basismeting | Niet blokkerend voor het oordeel, wél een issue waard |
| Hersteld | Rapporteren |
| **Verdwenen** | **Blokkerend.** Een assertie die er niet meer is, is de klassieke manier om ongemerkt groen te worden |

Meld tot slot expliciet:
- welke routes `UNTESTED` waren en waarom (dat is een gat in de demodata, geen succes);
- welke poorten 🚧 stonden en door welk issuenummer;
- wat je hebt gefixt, en wat je bewust hebt laten liggen.

---

## Altijd van toepassing

- **Nooit club-specifieke gegevens in een issue, PR of comment.** Resourcenamen, domeinen,
  tenant-identificatoren, e-mailadressen: altijd een placeholder. Zie CLAUDE.md.
- **Schermafbeeldingen kunnen persoonsgegevens bevatten.** De zelftest draait op demodata, maar
  controleer vóór je een afbeelding deelt dat er geen echte club in beeld staat. `artifacts/` staat
  daarom in `.gitignore`.
- **Externe diensten blijven uit.** De zelftest hoort geen e-mail te versturen, geen issues aan te
  maken en geen betaalde dienst aan te roepen. Zie issue #867; tot dat er is, controleer je dat
  handmatig vóór je een run start.
