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
> **Enige veilige patroon:** kill services → `dotnet clean BlazorAdmin` → `Start-Debug.ps1`.

## Stap 1 — Controleer lopende services

Voer parallel uit:
```powershell
(Get-NetTCPConnection -LocalPort 7094  -State Listen -ErrorAction SilentlyContinue) | Select-Object -First 1
(Get-NetTCPConnection -LocalPort 5242  -State Listen -ErrorAction SilentlyContinue) | Select-Object -First 1
(Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue) | Select-Object -First 1
```

Rapporteer welke poorten al bezet zijn.
- Als alle drie al luisteren → meld dit en vraag of ze opnieuw gestart moeten worden.
- Als de services al draaien en de gebruiker wil doorgaan → sla Stap 2 over en ga direct naar Stap 3.

## Stap 2 — Clean start

**Stop services + clean BlazorAdmin build output** (verplicht vóór elke (her)start):

```powershell
# Stop draaiende services
Stop-Process -Name "func","dotnet","node" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Verwijder stale fingerprints — BlazorAdmin genereert nieuwe per compilatiepass
dotnet clean BlazorAdmin/BlazorAdmin.csproj | Out-Null
Write-Host "BlazorAdmin build output geclean." -ForegroundColor Gray
```

**Start services:**

Bepaal op basis van `$ARGUMENTS`:
- Geen argument of leeg: voer `.\scripts\dev\Start-Debug.ps1` uit
- Argument bevat "swa": voer `.\scripts\dev\Start-Debug.ps1 -Swa` uit

> `Start-Debug.ps1` gebruikt intern `dotnet watch run` (of `dotnet run` bij -NoWatch) voor BlazorAdmin.
> Dit is de ENIGE geautoriseerde manier om BlazorAdmin te starten — het doet build+serve in één pass.
> Roep `dotnet build BlazorAdmin` NIET afzonderlijk aan vóór of na het starten.

## Stap 3 — Wachten + FunctionApp health check (20 seconden)

```powershell
Start-Sleep -Seconds 20
$health = Invoke-RestMethod "http://localhost:7094/api/health" -ErrorAction SilentlyContinue
if ($health) {
    Write-Host "✅ FunctionApp: versie $($health.version)" -ForegroundColor Green
} else {
    Write-Host "❌ FunctionApp reageert niet — lees de FunctionApp-terminaloutput" -ForegroundColor Red
}
```

- Versienummer zichtbaar (bijv. `2.5.0.0`) → ✅
- Geen antwoord of fout → ❌ — lees de FunctionApp-terminaloutput en rapporteer

## Stap 4 — Blazor fingerprint consistency check

**Dit is de kritieke check die "An unhandled error has occurred" detecteert vóórdat de gebruiker de browser opent.**
Root cause: meerdere `dotnet build`-passes genereren conflicterende fingerprints; de server serveert
de ene set terwijl de browser de andere verwacht → 404 op framework-JS → crash vóór App.razor rendert.

```powershell
# Haal index.html op en extraheer de importmap
$html = (Invoke-WebRequest "http://localhost:5242/" -UseBasicParsing -ErrorAction SilentlyContinue).Content
if (-not $html) {
    Write-Host "❌ BlazorAdmin reageert niet op HTTP — wacht nog 10s en herhaal" -ForegroundColor Red
} else {
    # Extraheer importmap JSON
    $importmapMatch = [regex]::Match($html, '<script type="importmap"[^>]*>(.*?)</script>',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)

    if ($importmapMatch.Success -and $importmapMatch.Groups[1].Value.Trim() -notin @('', '{}')) {
        try {
            $importmapJson = $importmapMatch.Groups[1].Value | ConvertFrom-Json
            $dotnetEntry   = $importmapJson.imports.dotnet -replace '^\.\/', ''
        } catch { $dotnetEntry = $null }

        # .NET 10 Blazor WASM: importmap key is './_framework/dotnet.js' (niet 'dotnet')
        $dotnetEntry = $importmapJson.imports."./_framework/dotnet.js" -replace '^\.\/', ''

        if ($dotnetEntry) {
            $check = Invoke-WebRequest "http://localhost:5242/$dotnetEntry" -UseBasicParsing -ErrorAction SilentlyContinue
            if ($check -and $check.StatusCode -eq 200) {
                Write-Host "✅ Blazor fingerprint consistent: $dotnetEntry" -ForegroundColor Green
            } else {
                $sc = if ($check) { $check.StatusCode } else { "geen antwoord" }
                Write-Host "❌ FINGERPRINT MISMATCH: $dotnetEntry → HTTP $sc" -ForegroundColor Red
                Write-Host "   ACTIE: Stop → dotnet clean BlazorAdmin/BlazorAdmin.csproj → Start-Debug.ps1 opnieuw" -ForegroundColor Yellow
            }
        } else {
            Write-Host "⚠️ dotnet-entry (./_framework/dotnet.js) niet gevonden in importmap — BlazorAdmin start mogelijk nog op" -ForegroundColor Yellow
            Write-Host "   Wacht 10s en voer Stap 4 opnieuw uit." -ForegroundColor Yellow
        }
    } else {
        Write-Host "⚠️ Importmap leeg of onvolledig — BlazorAdmin start nog op" -ForegroundColor Yellow
        Write-Host "   Wacht 10s en voer Stap 4 opnieuw uit." -ForegroundColor Yellow
    }
}
```

Uitkomsten:
- `✅ Blazor fingerprint consistent` → fingerprints kloppen, app kan laden
- `❌ FINGERPRINT MISMATCH` → **stop services, `dotnet clean BlazorAdmin`, herstart** (herhaal vanaf Stap 2)
- `⚠️ importmap leeg/ontbreekt` → server nog niet klaar, wacht 10s en herhaal Stap 4

## Stap 5 — API endpoint check

```powershell
# Instellingen endpoint (achterliggende data voor /instellingen pagina)
try {
    $settings = Invoke-RestMethod "http://localhost:7094/api/beheer/settings" -ErrorAction Stop
    Write-Host "✅ /api/beheer/settings bereikbaar" -ForegroundColor Green
} catch {
    Write-Host "⚠️ /api/beheer/settings: $($_.Exception.Message)" -ForegroundColor Yellow
}
```

## Stap 6 — Browser verificatie (verplicht na elke start)

> ⚠️ **HTTP 200 op de root ≠ Blazor werkt in de browser.**
> Blazor WASM laadt en rendert volledig client-side — HTTP 200 bewijst alleen dat `index.html` wordt geserveerd.
> De enige definitieve verificatie is in de browser zelf.

Instrueer de gebruiker:
1. Open `http://localhost:5242` in de browser
2. **Ctrl+Shift+F5** (hard refresh — leegt cache en forceert verse fingerprints)
3. Wacht tot de app volledig geladen is (blauwe loading-ring verdwijnt)
4. Controleer minimaal:
   - Geen rode banner "An unhandled error has occurred. Reload" onderaan de pagina
   - Versienummer zichtbaar in de header (bijv. `v2.5.0`)
   - Navigeer naar `http://localhost:5242/instellingen` — pagina laadt zonder foutmelding

Als "An unhandled error" toch verschijnt na hard refresh:
- **F12 → Console tab** — kopieer de foutmelding en rapporteer
- Meest voorkomende oorzaak: fingerprint-conflict → herhaal Stap 2 (clean + herstart)

## Stap 7 — Samenvatting

| Service | URL | Status |
|---|---|---|
| Azurite | poort 10000 | ✅/❌ |
| FunctionApp | http://localhost:7094/api/health | ✅/❌ versie: ... |
| BlazorAdmin | http://localhost:5242 | ✅/❌ |
| Fingerprint check | dotnet.*.js HTTP 200 | ✅/❌ |
| /api/beheer/settings | http://localhost:7094/... | ✅/⚠️ |
| SWA emulator | http://localhost:4280 (alleen bij -Swa) | ✅/❌/n.v.t. |

Sluit af met: "Open `http://localhost:5242` in de browser met **Ctrl+Shift+F5**. Controleer: geen foutbanner, versienummer zichtbaar, `/instellingen` laadt zonder foutmelding."
