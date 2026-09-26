---
description: Start alle lokale debug-services — Azurite, FunctionApp (:7094) en BlazorAdmin (:5242). Gebruik "swa" als argument voor de SWA emulator (:4280).
disable-model-invocation: true
argument-hint: [swa]
---

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
- Geen argument of leeg: `./scripts/dev/Start-Debug.ps1 -Clean`
- Argument bevat "swa": `./scripts/dev/Start-Debug.ps1 -Clean -Swa`

> Het script gebruikt intern `dotnet watch run` (of `dotnet run` bij `-NoWatch`) voor BlazorAdmin.
> Dit is de ENIGE geautoriseerde manier om BlazorAdmin te starten — het doet build+serve in één pass.
> Roep `dotnet build BlazorAdmin` NIET afzonderlijk aan vóór of na het starten.

Platformverschil in de output, geen fout:
- **Windows** — elke service krijgt een eigen console-venster.
- **macOS/Linux** — `Start-Process` opent daar nooit een venster en `-WindowStyle` is er een no-op.
  De output loopt naar logbestanden in `sportlink-debug-logs` onder de tijdelijke map van de
  gebruiker; het script meldt het pad. Gebruik `-Tail` voor één samengevoegde logstroom in de
  huidige terminal.

Faalt de start (exit 1)? Lees de logs (macOS/Linux) of het bijbehorende venster (Windows) en
rapporteer de fout. Veelvoorkomend: .NET 9 runtime ontbreekt, of de database draait niet
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

## Stap 6 — Browser verificatie (verplicht na elke start)

> ⚠️ **HTTP 200 op de root ≠ Blazor werkt in de browser.**
> Blazor WASM laadt en rendert volledig client-side — HTTP 200 bewijst alleen dat `index.html` wordt geserveerd.
> Een `Invoke-WebRequest` of `curl` op deze URL is hiervoor sowieso ongeschikt: die toont altijd de
> statisch aanwezige, normaal verborgen foutbanner in de HTML. De enige definitieve verificatie is
> een echte browser.

Instrueer de gebruiker (kies de toetscombinatie van het platform waarop de sessie draait):

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
| Azurite | poort 10000 | ✅/❌ |
| FunctionApp | http://localhost:7094/api/health | ✅/❌ versie: ... |
| BlazorAdmin | http://localhost:5242 | ✅/❌ |
| Fingerprint check | dotnet.*.js HTTP 200 | ✅/❌ |
| /api/beheer/settings | http://localhost:7094/... | ✅/⚠️ |
| SWA emulator | http://localhost:4280 (alleen bij -Swa) | ✅/❌/n.v.t. |

Sluit af met: "Open `http://localhost:5242` in de browser en doe een hard refresh
(Windows: **Ctrl+Shift+F5** · macOS: **Cmd+Shift+R**). Controleer: geen foutbanner, versienummer
zichtbaar, `/instellingen` laadt zonder foutmelding."

Stoppen na afloop: `./scripts/dev/Stop-Debug.ps1` (Azurite blijft draaien) of `-All` om ook
Azurite te stoppen.
