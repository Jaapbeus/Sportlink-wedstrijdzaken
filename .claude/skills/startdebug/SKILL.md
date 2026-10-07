---
name: startdebug
description: "Start alle lokale debug-services op de laatste develop-branch — Azurite, FunctionApp (:7094) en BlazorAdmin (:5242). Werkt develop eerst bij naar origin/develop, past openstaande migraties toe en zet live Sportlink-verkeer voor de primaire club klaar (nooit ALLSTARS). Argumenten: \"swa\" voor de SWA emulator (:4280), \"offline\" om zonder Sportlink-verkeer te starten."
disable-model-invocation: true
argument-hint: "[swa] [offline]"
---

> **Gezamenlijke agentregels zijn leidend (CLAUDE.md/AGENTS.md).** Deze skill geldt voor Codex
> en Claude Code. Werk uitsluitend aan de toegewezen taak in de eigen geverifieerde worktree;
> claim of wijzig geen taak, branch, worktree of services van een andere actieve sessie.
> Behoud `source:` als herkomst; registreer implementer, reviewer, fase en sessie afzonderlijk.
> Vóór merge: wederzijdse review van de huidige head-SHA, relevante checks én afzonderlijke
> eigenaarsautorisatie. Deze skill omzeilt die grenzen niet.
> Start/stop/clean/migraties vereisen vooraf exclusief runtime-eigenaarschap. De huidige
> debugscriptset is gedeeld: onbekende of andere runtime-eigenaar betekent geen mutaties.
> Poorten vrij betekent niet dat databases/testdata vrij zijn. Leg reservering en vrijgave vast
> in de taak/sessie-overdracht; laat services intact als ze aan een andere sessie behoren.

Start de lokale debug-omgeving. Scripts staan in `scripts/dev/`.

> ⚠️ **KRITIEKE REGEL — altijd van toepassing:**
> Roep **NOOIT** `dotnet build BlazorAdmin` aan terwijl de Blazor dev server al draait of ná het starten.
> BlazorAdmin genereert content-hash fingerprints per compilatie — twee compilatiepassen = twee sets fingerprints
> = 404 op framework-JS = "An unhandled error has occurred. Reload" in de browser.
> **Enige veilige patroon:** `Stop-Debug.ps1 -Clean` → `Start-Debug.ps1`.

> 🖥️ **Cross-platform (#800, #1286).** Deze skill draait op Windows én macOS onder PowerShell 7.
> Drie regels bij het aanpassen ervan:
> 1. **Poortdetectie uitsluitend via `Test-PortListening`** uit `scripts/dev/DevServices.psm1`.
>    Nooit `Get-NetTCPConnection` — die zit in de module `NetTCPIP` en bestaat alleen op Windows.
>    De .NET BCL is op macOS ook geen optie: `GetActiveTcpListeners()` ziet daar alleen listeners
>    van het eigen proces (#1171), dus die aanroep geeft `$false` voor precies de services die
>    hier gedetecteerd moeten worden.
> 2. **Nooit `Stop-Process -Name`.** Dat sloopt élk `dotnet`/`node`-proces op de machine, en
>    `dotnet watch` herstart zijn kindproces meteen — poort 5242 is dan direct weer bezet.
>    Gebruik `Stop-Debug.ps1`, dat procesbomen stopt via het PID-bestand.
> 3. **Geen backslash in een padliteral.** Op Unix is `\` een geldig teken ín een bestandsnaam,
>    geen scheidingsteken. Forward slashes werken op beide platforms.

Alle commando's hieronder draaien in PowerShell 7 (`pwsh` op macOS, `powershell`/`pwsh` op Windows).

## Stap 0 — Draai altijd de laatste `develop` (verplicht, vóór Stap 1)

> **Waarom (#1466).** De skill start de services vanuit de huidige werkmap. Is dat de main-checkout,
> dan draait de GUI op de productieversie — of erger, op een achterlopende main (voorbeeld: GUI
> toonde v3.8.0.0 terwijl `origin/develop` al op v3.9.6.1 stond, 32 commits verder) — terwijl je
> bijna altijd de nieuwste integratiestand wilt testen. `develop` is de integratiebranch voor lokaal
> testen (zie CLAUDE.md, "Branch-strategie"); dáár hoort de debug-omgeving op te draaien. **Elke
> volgende stap (1 t/m 7) voer je uit vanuit de develop-worktree die je hier bepaalt** — niet vanuit
> de map waarin de sessie toevallig startte.

> **Sinds #1574 doet `Start-Debug.ps1` zelf 0a, 0b en 0d** (develop-worktree zoeken, fast-forward
> naar `origin/develop`, daar starten), ook bij een aanroep vanuit een andere map. `-HuidigeWerkmap`
> schakelt dat uit. Stap 0a/0b hieronder blijven nodig voor 0c (migraties, vóór het starten) en als
> je de worktree-keuze wilt controleren.

**Vóór 0a:** reserveer de gedeelde runtime én de develop-acceptatieworktree; controleer
dat geen andere sessie daar schrijft of services beheert. Zonder reservering geen fetch/merge,
migratie, clean of start in die omgeving. `/startdebug` reserveert niet automatisch andermans omgeving.

**0a — Vind de worktree die `develop` uitgecheckt heeft.** `develop` kan maar in één worktree tegelijk
staan; die is het doel van alle volgende stappen. Draai vanuit de repo-root:

```powershell
$developPad = $null; $pad = $null
foreach ($line in (git worktree list --porcelain)) {
    if     ($line -like 'worktree *')              { $pad = $line.Substring(9) }
    elseif ($line -eq 'branch refs/heads/develop') { $developPad = $pad; break }
}
if (-not $developPad) {
    Write-Host "Geen worktree op 'develop' gevonden. Maak er eenmalig één buiten de repo-boom:" -ForegroundColor Yellow
    Write-Host "  git worktree add ../Sportlink-wedstrijdzaken-develop develop" -ForegroundColor Yellow
    Write-Host "en draai daarna deze skill opnieuw." -ForegroundColor Yellow
} else {
    Write-Host "develop-worktree: $developPad" -ForegroundColor Green
}
```

**0b — Werk develop bij naar `origin/develop` (fast-forward only).** Nooit forceren: heeft de lokale
develop eigen commits (diverged), dan faalt de fast-forward bewust — stop dan en meld dat aan de
gebruiker in plaats van te mergen of te resetten.

```powershell
git -C $developPad fetch origin develop
git -C $developPad merge --ff-only origin/develop
if ($LASTEXITCODE -ne 0) {
    throw "FF-only mislukt — develop is lokaal afgeweken. Geen migraties/services starten."
}
```

**0c — Pas openstaande Postgres-migraties toe VÓÓR het starten.** Een bijgewerkte develop brengt soms
nieuwe migraties mee; zonder toepassen blijft `/api/health` op `degraded` staan met een niet-lege
`pendingMigrations`. Doe dit nu, **niet** terwijl de services draaien: een `dotnet run` naast
`func start` + `dotnet watch` heeft de functiehost al eens laten omvallen (SIGKILL door
resource-druk). De runner is idempotent. De connection string komt uit `local.settings.json` —
**nooit echoën**.

```powershell
Push-Location $developPad
try {
    $ls = Get-Content FunctionApp.Postgres/local.settings.json -Raw | ConvertFrom-Json
    $env:POSTGRES_CONNECTION_STRING = $ls.Values.POSTGRES_CONNECTION_STRING
    if ($env:POSTGRES_CONNECTION_STRING) { dotnet run --project Database.Postgres.Cli }
    else { Write-Host "Geen POSTGRES_CONNECTION_STRING in local.settings.json" -ForegroundColor Yellow }
} finally { Pop-Location }
```

**0d — Stap de sessie de develop-worktree in** (`Set-Location $developPad`, of werk met expliciete
paden). Alle `./scripts/dev/...`-aanroepen en healthchecks hieronder gaan vanaf hier over díe
worktree. Rapporteer in de samenvatting (Stap 7) welke branch én welk versienummer draaien, zodat
meteen zichtbaar is dat het de laatste develop is.

## Stap 1 — Controleer lopende services

```powershell
Import-Module ./scripts/dev/DevServices.psm1 -Force
$p = Get-DebugPorts
foreach ($naam in 'Azurite','FunctionApp','BlazorAdmin','Swa') {
    $luistert = Test-PortListening -Port $p[$naam]
    $vlag = if ($luistert) { 'BEZET' } else { 'vrij ' }
    Write-Host ("  {0,-12} :{1,-6} {2}" -f $naam, $p[$naam], $vlag)
}
```

Rapporteer welke poorten al bezet zijn.
- Als Azurite, FunctionApp én BlazorAdmin al luisteren → meld dit en vraag of ze opnieuw gestart moeten worden.
- Als de services al draaien en de gebruiker wil doorgaan → sla Stap 2 over en ga direct naar Stap 3.

> Ziet deze stap op macOS alles als "vrij" terwijl er wél services draaien, en waarschuwt
> `Test-PortListening` over een ontbrekende `lsof`? Dan is de poortdetectie blind en betekent
> "vrij" niets — los dat eerst op, want Start-Debug zou dan een tweede Azurite starten die niet
> kan binden (#1171).

## Stap 2 — Clean start

`Start-Debug.ps1 -Clean` doet het stoppen, het cleanen van de stale fingerprints en het starten
in één pass — op beide platforms. Voer het daarom **niet** met de hand voor in losse stappen.

Bepaal op basis van `$ARGUMENTS`:
- Standaard: `./scripts/dev/Start-Debug.ps1 -Clean` (als agent met `-Bewaak`, zie hieronder; draait vanzelf op de laatste develop, #1574, met live Sportlink en de GO-controle, #1576)
- Argument bevat "huidig": voeg `-HuidigeWerkmap` toe (bewust niet op develop, bijv. een feature-branch)
- Argument bevat "swa": voeg `-Swa` toe
- Argument bevat "offline": voeg `-Offline` toe (lokaal dan geen Sportlink-verkeer, zie Stap 3b)

> **Waarom `-SportlinkLive` standaard is (#1466).** De eigenaar test op localhost de Sportlink Web
> Extension tegen de échte Sportlink Club van de eigen club, inclusief schrijfacties zoals
> **Wedstrijd aanmaken** en verwijderen (#1440). Zonder deze schakelaar staat lokaal elk extern
> verkeer dicht (EgressGuard, #857) en registreert de host geen Sportlink-client (ontbrekende
> hostsleutel, #1411). De schakelaar maakt dit veilig en herhaalbaar; zie Stap 3b voor wat hij
> wel en niet doet.

> Het script gebruikt intern `dotnet watch run` (of `dotnet run` bij `-NoWatch`) voor BlazorAdmin.
> Dit is de ENIGE geautoriseerde manier om BlazorAdmin te starten — het doet build+serve in één pass.
> Roep `dotnet build BlazorAdmin` NIET afzonderlijk aan vóór of na het starten.

Platformverschil in de output, geen fout:
- **Windows** — elke service krijgt een eigen console-venster.
- **macOS/Linux** — `Start-Process` opent daar nooit een venster en `-WindowStyle` is er een no-op.
  De output loopt naar logbestanden in `sportlink-debug-logs` onder de tijdelijke map van de
  gebruiker; het script meldt het pad. Gebruik `-Tail` voor één samengevoegde logstroom in de
  huidige terminal.

> **Binnen een Claude Code-sessie (#1576).** Start-Debug start de services sinds #1576 in een eigen
> sessie, maar dat bleek niet genoeg: Azurite verdween binnen twee seconden nadat het startscript was
> geëindigd (de FunctionApp en BlazorAdmin bleven staan), en daarna gaf elk scherm "Failed to fetch".
> Zolang het script zelf blijft draaien blijft Azurite staan. **Start het daarom als agent altijd met
> `-Bewaak` in een eigen achtergrondaanroep (`run_in_background`)**: het script blijft dan draaien en
> herstart een weggevallen service automatisch.
> **Een controle in dezelfde aanroep als de start bewijst niets.** Draai `Test-DebugGo.ps1` (Stap 3c)
> altijd in een **volgende** aanroep, en start nooit handmatig losse services als reparatie: een
> losse `func start` omzeilt de versie- en sessiecontrole. Stop met `Stop-Debug.ps1 -All`.

Faalt de start (exit 1)? Lees de logs (macOS/Linux) of het bijbehorende venster (Windows) en
rapporteer de fout. Veelvoorkomend: .NET 10 SDK/runtime ontbreekt, of de database draait niet
(`docker compose up -d`).

## Stap 3 — FunctionApp health check

`Start-Debug.ps1` pollt zelf `/api/health` en faalt met exit 1 als de service niet opkomt — een
vaste `Start-Sleep` is dus niet nodig. Dit is de handmatige herhaling:

```powershell
try {
    $health = Invoke-RestMethod "http://localhost:7094/api/health" -ErrorAction Stop
    Write-Host "OK  FunctionApp: versie $($health.version)" -ForegroundColor Green
} catch {
    Write-Host "FOUT  FunctionApp reageert niet: $($_.Exception.Message)" -ForegroundColor Red
}
```

- Versienummer zichtbaar (bijv. `2.5.0.0`) → ✅
- Geen antwoord of fout → ❌ — lees de FunctionApp-log (macOS/Linux) of het venster (Windows) en rapporteer

## Stap 3b — Sportlink live voor de primaire club (#1466)

`Start-Debug.ps1` meldt na het opstarten altijd één van twee regels:
- `Sportlink live-klaar voor de primaire club (dry-run AAN …)` of `(dry-run UIT …)` → ✅
- `Sportlink niet live voor de primaire club:` met per regel de reden → los die op zoals hieronder

**Welke club live gaat, bepaalt de database, niet de code of deze skill.** Dat is de club met
`syncenabled = TRUE` in `public.appsettings` (de primaire club van de installatie). De democlub
`ALLSTARS` heeft geen Sportlink-koppeling en de extensie staat er uit. Hij gaat dus nooit live.
Elke fork werkt ongewijzigd met de eigen primaire club. Noem in issues/PR's/commits nooit de
naam of code van die club (CLAUDE.md veiligheidsregel 4a).

**Wat `-SportlinkLive` doet** (`Set-SportlinkLiveLocalSettings` in `scripts/dev/DevServices.psm1`):
1. zet `AllowExternalIntegrations` op `true` in `local.settings.json` van de tier;
2. maakt een lokale `SportlinkAutoLoginEncryptionKey` (32 bytes, base64) aan **als die ontbreekt
   of ongeldig is**. Een geldige sleutel blijft staan, want een nieuwe sleutel maakt al opgeslagen
   inloggegevens onleesbaar.

Het script toont of logt nooit een waarde. Lees `local.settings.json` als agent ook niet zelf
uit, want het bevat secrets (zie `docs/SPORTLINK-WEB-EXTENSION.md`, de alinea over `local.settings.json`).

> **Gevolg van `AllowExternalIntegrations=true`:** de poort staat dan open voor élke externe
> integratie, niet alleen Sportlink. In de standaard lokale configuratie zijn de Graph-, OpenAI-
> en GitHub-instellingen leeg, dus in de praktijk gaan alleen Sportlink Club en de
> Sportlink-dataservice (de sync-timer) live. Vul je die andere secrets lokaal wél in, dan gaan
> ook die live. Start dan met `offline` als je dat niet wilt.

**Wat alleen de eigenaar doet — nooit een agent:**
- **Inloggegevens invoeren:** menu **Sportlink Ext.** (pagina **Sportlink Web Extension**) →
  kaart **Automatisch inloggen — rol Wedstrijdzaken** → **Automatisch inloggen instellen**.
  Daarvoor is eenmalig een nieuwe invoer nodig na een nieuwe lokale hostsleutel. Lokaal en
  productie hebben elk hun eigen sleutel, dus de versleutelde gegevens zijn niet uitwisselbaar.
- **Dry-run uitzetten** (vinkje *Dry-run: alles simuleren, niets naar Sportlink schrijven* op
  dezelfde pagina). Zolang dry-run aan staat, gaat lezen live en worden schrijfacties alleen
  gesimuleerd en geaudit.
- **De code-locks per schrijfactie** in `Planner.Shared/Integrations/SportlinkClub/SportlinkClubClient.cs`
  (`…LiveBevestigd`-constanten, `docs/SPORTLINK-WEB-EXTENSION.md` §4.4). Voor verwijderen (#1440)
  is dat `ClubMatchDeleteLiveBevestigd`. Zolang die op `false` staat, is verwijderen óók lokaal
  altijd een simulatie, ongeacht de dry-run-instelling. Een agent zet zo'n constante nooit om,
  ook niet lokaal of tijdelijk.

**Verwijderen (#1440) lokaal testen:** de knop **Wedstrijd verwijderen uit Sportlink** verschijnt
alleen onder het resultaat van een **echte** aanmaak op **Wedstrijd aanmaken**. Daarvoor moet de
eigenaar dry-run uitzetten: dan wordt er echt een wedstrijd in Sportlink aangemaakt. Na **Ja, verwijderen**
is het verwijderen zelf zolang `ClubMatchDeleteLiveBevestigd = false` nog een simulatie. De
testwedstrijd blijft dan staan en moet in Sportlink Club met de hand worden opgeruimd. Gebruik
nooit een competitie- of bekerwedstrijd. Na de verwijdering verdwijnt de wedstrijd lokaal pas uit de planning bij de
eerstvolgende geslaagde sync (#1193).

> **Risico om te melden vóór de eerste live login:** of een lokale login met hetzelfde
> serviceaccount een productiesessie ongeldig maakt, is niet aangetoond. Met automatisch inloggen
> (#1411) herstelt productie zich bij de volgende vernieuwing zelf, maar meld het de eigenaar.

Handmatige herhaling van de controle (lokaal is de rolcontrole uitgeschakeld, dus geen token nodig):

```powershell
Import-Module ./scripts/dev/DevServices.psm1 -Force
$sl = try { Invoke-RestMethod "http://localhost:7094/api/beheer/sportlink-extensie/health" -ErrorAction Stop } catch { $null }
Get-SportlinkLiveBlockers -SettingsPath FunctionApp.Postgres/local.settings.json -Health $sl
# Lege uitvoer = live-klaar. $sl.dryRun zegt of schrijfacties gesimuleerd worden.
```

## Stap 3c — GO/NO-GO met `Test-DebugGo.ps1` (verplicht, #1576)

**Zonder een GO van dit script mag niemand zeggen dat de debugomgeving werkt.** Health 200 en HTTP 200
op `index.html` bewijzen niets over een Blazor WASM-app: ze rendert client-side en haalt data bij de
API op. `Start-Debug.ps1` draait de controle aan het einde zelf; draai hem in een **volgende** aanroep
nogmaals, want dat is het enige bewijs dat de services de startaanroep overleven.

```powershell
./scripts/dev/Test-DebugGo.ps1             # volledig; -Offline als de start offline was
```

Wat hij afdwingt: services luisteren (ook na 15 s en na de browsercontrole); `/api/health.version` is
de versie in het csproj van de draaiende worktree én die worktree staat op `origin/develop`; health
`ok` en geen openstaande migraties; `/api/beheer/sync/status` en `/api/beheer/sportlink-extensie/health`
antwoorden en de primaire club is live-klaar (tenzij `-Offline`); `OpenAiApiKey` is lokaal ingesteld, want zonder werkt de
e-mailtester niet ("IChatClient niet geconfigureerd"; tenzij `-ZonderAI`; de waarde van de sleutel leest geen agent); en een echte headless Chromium
(Playwright, `debug-browsercheck.cjs`) opent Start, Instellingen, Planning, E-mailtester, Teamaliassen,
Sportlink-extensie en Speeltijden zonder foutbanner, zonder mislukte of 5xx-aanroep naar de API,
zonder console-fout en met het juiste versienummer. Screenshots staan in `sportlink-debug-go` onder
de tijdelijke map.

- **GO (exit 0)** → pas dan Stap 7 invullen en melden.
- **NO-GO (exit 1)** → lees de regels met `FOUT`, herstel de oorzaak in script of code, en herstart met
  `Stop-Debug.ps1 -All -Clean` + `Start-Debug.ps1`, tot het script GO geeft. Meld nooit "werkt" met
  een NO-GO, en schuif de controle nooit door naar de gebruiker.

## Stap 4 — Blazor fingerprint consistency check

**Dit is de kritieke check die "An unhandled error has occurred" detecteert vóórdat de gebruiker de browser opent.**
Root cause: meerdere `dotnet build`-passes genereren conflicterende content-hash fingerprints; de
server serveert de ene set terwijl de browser de andere verwacht → 404 op framework-JS → crash
vóór App.razor rendert.

> **Deze check kijkt naar `_framework/dotnet.js`, niet naar een importmap (#1286).**
> De vorige versie las de fingerprints uit een `<script type="importmap">` in `index.html`. Die
> staat er niet en mag er niet staan: `OverrideHtmlAssetPlaceholders` is bewust `false`, omdat de
> productie-CSP van Azure SWA geen inline script toestaat en de import-map anders `dotnet.js`
> onvindbaar maakte (#659). Gevolg was dat Stap 4 op élk platform in de tak
> "importmap leeg — wacht 10s en herhaal" viel: de check kón niet slagen en kon dus ook nooit een
> echte mismatch melden. De boot-manifest met de gefingerprinte assetnamen zit in .NET 10 in
> `dotnet.js` zelf; dat is de bron die de browser ook gebruikt.

```powershell
$loader = try { (Invoke-WebRequest "http://localhost:5242/_framework/dotnet.js" -ErrorAction Stop).Content }
          catch { $null }

if (-not $loader) {
    Write-Host "FOUT  _framework/dotnet.js niet bereikbaar — BlazorAdmin start nog op of is stuk" -ForegroundColor Red
    Write-Host "      Wacht 10s en voer Stap 4 opnieuw uit." -ForegroundColor Yellow
} else {
    $assets = [regex]::Matches($loader, 'dotnet\.(?:native|runtime)\.[a-z0-9]{6,}\.(?:js|wasm)') |
              ForEach-Object { $_.Value } | Sort-Object -Unique

    if (-not $assets) {
        Write-Host "LET OP  Geen gefingerprinte assets in dotnet.js — controleer of de build compleet is" -ForegroundColor Yellow
    } else {
        $stuk = @()
        foreach ($a in $assets) {
            $code = try { (Invoke-WebRequest "http://localhost:5242/_framework/$a" -Method Head -ErrorAction Stop).StatusCode }
                    catch { $_.Exception.Response.StatusCode.value__ }
            if ($code -eq 200) { Write-Host "  OK    $a" -ForegroundColor DarkGray }
            else { Write-Host "  FOUT  $a -> HTTP $code" -ForegroundColor Red; $stuk += $a }
        }
        if ($stuk) {
            Write-Host "FOUT  FINGERPRINT MISMATCH — $($stuk.Count) asset(s) geven geen 200" -ForegroundColor Red
            Write-Host "      ACTIE: ./scripts/dev/Stop-Debug.ps1 -Clean en daarna Stap 2 opnieuw" -ForegroundColor Yellow
        } else {
            Write-Host "OK  Blazor fingerprints consistent ($($assets.Count) assets)" -ForegroundColor Green
        }
    }
}
```

Uitkomsten:
- `OK Blazor fingerprints consistent` → fingerprints kloppen, app kan laden
- `FINGERPRINT MISMATCH` → `./scripts/dev/Stop-Debug.ps1 -Clean`, daarna Stap 2 opnieuw
- `dotnet.js niet bereikbaar` → server nog niet klaar, wacht 10s en herhaal Stap 4

> `-UseBasicParsing` is in PowerShell 7 een genegeerde no-op en staat hier daarom niet meer.
> De HTML-parser van Windows PowerShell 5.1, waarvoor die vlag bedoeld was, bestaat op macOS niet.

## Stap 5 — API endpoint check

```powershell
try {
    $null = Invoke-RestMethod "http://localhost:7094/api/beheer/settings" -ErrorAction Stop
    Write-Host "OK  /api/beheer/settings bereikbaar" -ForegroundColor Green
} catch {
    Write-Host "LET OP  /api/beheer/settings: $($_.Exception.Message)" -ForegroundColor Yellow
}
```

## Stap 6 — Browser verificatie (verplicht na elke start; de agent doet dit zelf via Stap 3c)

> ⚠️ **HTTP 200 op de root ≠ Blazor werkt in de browser.**
> Blazor WASM laadt en rendert volledig client-side — HTTP 200 bewijst alleen dat `index.html` wordt geserveerd.
> Een `Invoke-WebRequest` of `curl` op deze URL is hiervoor sowieso ongeschikt: die toont altijd de
> statisch aanwezige, normaal verborgen foutbanner in de HTML. De enige definitieve verificatie is
> een echte browser.

Stap 3c heeft dit al in een echte browser gedaan; vraag de gebruiker dit dus niet over te doen. Wil de gebruiker zelf kijken (kies de toetscombinatie van het platform):

| Platform | Hard refresh |
|---|---|
| Windows | **Ctrl+Shift+F5** (of Ctrl+F5) |
| macOS | **Cmd+Shift+R** |

1. Open `http://localhost:5242` in de browser
2. Hard refresh volgens de tabel hierboven — leegt de cache en forceert verse fingerprints
3. Wacht tot de app volledig geladen is (blauwe loading-ring verdwijnt)
4. Controleer minimaal:
   - Geen rode banner "An unhandled error has occurred. Reload" onderaan de pagina
   - Versienummer zichtbaar in de header (bijv. `v2.5.0`)
   - Navigeer naar `http://localhost:5242/instellingen` — pagina laadt zonder foutmelding

Als "An unhandled error" toch verschijnt na hard refresh:
- **Developer tools → Console** (Windows: F12 · macOS: Cmd+Option+I) — kopieer de foutmelding en rapporteer
- Meest voorkomende oorzaak: fingerprint-conflict → `./scripts/dev/Stop-Debug.ps1 -Clean`, daarna Stap 2 opnieuw

## Stap 7 — Samenvatting

| Service | URL | Status |
|---|---|---|
| GO-controle | `Test-DebugGo.ps1` (Stap 3c) | ✅ GO / ❌ NO-GO — zonder GO geen melding dat het werkt |
| Branch / versie | develop → `/api/health`.version | ✅ laatste develop (fast-forward van origin/develop) |
| Azurite | poort 10000 | ✅/❌ |
| FunctionApp | http://localhost:7094/api/health | ✅/❌ versie: ... |
| Sportlink live (primaire club) | Start-Debug-melding / Stap 3b | ✅ live-klaar (dry-run aan/uit) / ⚠️ reden / n.v.t. bij `offline` |
| BlazorAdmin | http://localhost:5242 | ✅/❌ |
| Fingerprint check | dotnet.*.js HTTP 200 | ✅/❌ |
| /api/beheer/settings | http://localhost:7094/... | ✅/⚠️ |
| SWA emulator | http://localhost:4280 (alleen bij -Swa) | ✅/❌/n.v.t. |

Sluit af met: "Open `http://localhost:5242` in de browser en doe een hard refresh
(Windows: **Ctrl+Shift+F5** · macOS: **Cmd+Shift+R**). Controleer: geen foutbanner, versienummer
zichtbaar, `/instellingen` laadt zonder foutmelding."

Stoppen na afloop: `./scripts/dev/Stop-Debug.ps1` (Azurite blijft draaien) of `-All` om ook
Azurite te stoppen.
