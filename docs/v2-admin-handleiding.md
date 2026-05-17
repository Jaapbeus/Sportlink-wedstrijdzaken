# v2 Admin GUI — handleiding

Deze handleiding beschrijft het v2 Admin-portaal (Blazor WebAssembly) en de bijbehorende admin-API
in `FunctionApp/Admin/`. Het portaal en de admin-API zitten in de branch `v2/develop`. Productie
draait op v1 (`main`). v2 wordt pas gedeployed wanneer alle Azure-resources zijn ingericht
(zie sectie *Azure resources aanmaken*).

---

## 1. Lokaal ontwikkelen

### Voorbereiding (eenmalig)

1. Stel `FunctionApp/local.settings.json` correct in (zie `local.settings.template.json`)
2. Voer alle migraties uit op de lokale SQL Server:
   ```powershell
   sqlcmd -S PTCL69L001 -d SportlinkSqlDb -E -i .\Database\Script.PostDeployment1.sql
   ```
3. Installeer Azurite (voor storage emulator)

### Functions en Blazor samen draaien

In **twee terminals**:

```powershell
# Terminal 1: Functions backend op poort 7094
cd FunctionApp
func start --port 7094
```

```powershell
# Terminal 2: Blazor WASM frontend
cd BlazorAdmin
dotnet run
```

De Blazor app draait standaard op `http://localhost:5000` of `http://localhost:5001` en spreekt de
Functions aan op `http://localhost:7094` (zie `BlazorAdmin/wwwroot/appsettings.json`,
`FunctionBaseUrl`). CORS is in `FunctionApp/Program.cs` alleen ingeschakeld in DEBUG.

In de lokale dev-omgeving gebruikt Blazor altijd `LocalAuthService` — je bent altijd ingelogd als
`LocalDev` met admin-rol. Echte Entra ID wordt pas geactiveerd in productie via de SWA-configuratie.

---

## 2. Way of working

### Branches

- `main` — productie (v1). Hier worden geen v2-wijzigingen aan toegevoegd.
- `v2/develop` — alle v2 ontwikkelwerk. Blijft lokaal totdat v2 release-klaar is.
- `feature/*` — losse korte branches voor v1 hotfixes.

### v1 hotfix procedure

1. Branch `feature/v1-fix-...` vanaf `main`
2. Commit + PR → review → merge naar `main`
3. Na merge: `git checkout v2/develop && git merge main` om hotfixes mee te nemen
4. CI op `main` controleert de deploy (zie `.github/workflows/deploy.yml`)

### v2 mergestrategie

- Werk op `v2/develop` met conventionele commits (`feat(v2/#NN): ...`)
- Geen pushes naar origin totdat alles klaar is
- Bij release: PR `v2/develop` → `main`, eenmalige groot review + merge

---

## 3. Azure resources aanmaken (vóór v2 livegang)

### Static Web App aanmaken

```bash
RG=rg-vrc-sportlink
LOC=westeurope
SWA_NAME=swa-vrc-admin

# 1. Resource group bestaat al; SWA aanmaken in 'Free' tier
az staticwebapp create \
  --name $SWA_NAME \
  --resource-group $RG \
  --location $LOC \
  --sku Free \
  --source https://github.com/<owner>/<repo> \
  --branch main \
  --token <GITHUB_PAT>
```

> **Tip:** wanneer je via de GitHub Action deployt in plaats van GitHub-bron koppelen, mag je
> `--source` / `--token` weglaten en de SWA als losse resource gebruiken.

### Deployment token ophalen

```bash
az staticwebapp secrets list \
  --name $SWA_NAME \
  --resource-group $RG \
  --query "properties.apiKey" -o tsv
```

### GitHub Secret instellen

Zet het token in de repo-secrets:

- `AZURE_STATIC_WEB_APPS_API_TOKEN` — de waarde uit het `apiKey` veld hierboven

Optioneel (smoke test):

- `AZURE_STATIC_WEB_APP_HOSTNAME` — bijv. `swa-vrc-admin.azurestaticapps.net`

Zodra `AZURE_STATIC_WEB_APPS_API_TOKEN` is gezet, draait de `blazor-deploy` job in
`.github/workflows/deploy.yml` mee bij elke push naar `main`.

### Function App linken aan SWA

Voor proxying zonder CORS:

```bash
az staticwebapp backends link \
  --name $SWA_NAME \
  --resource-group $RG \
  --backend-resource-id "/subscriptions/.../resourceGroups/.../providers/Microsoft.Web/sites/fa-prd-sportlink-01" \
  --backend-region westeurope
```

Na linken worden calls op `https://<swa>/api/admin/*` automatisch geforward naar de Function App,
inclusief de Entra ID identiteit.

---

## 4. Entra ID app-registratie (stap-voor-stap)

1. Azure Portal → **Microsoft Entra ID** → **App registrations** → **New registration**
2. Naam: `vrc-admin-swa`
3. Supported account types: **Single tenant** (zelfde tenant als de Graph-mailbox)
4. Redirect URI:
   - Type: **Web**
   - URI: `https://<swa-name>.azurestaticapps.net/.auth/login/aad/callback`
5. **Register**

### Client secret aanmaken

1. **Certificates & secrets** → **New client secret**
2. Description: `swa-admin`, Expires: 12 maanden
3. **Add** — kopieer de **Value** (eenmalig zichtbaar)

### API permissions

- Microsoft Graph → **User.Read** (delegated) is standaard al ingesteld
- Geen extra app permissions nodig; de routebescherming verloopt via SWA-rollen

### App rollen aanmaken

1. **App roles** → **Create app role**
2. Display name: `Admin`, Value: `admin`, Description: `VRC Admin GUI volledige toegang`
3. Allowed member types: **Users/Groups**
4. **Apply**

Optioneel een `user`-rol aanmaken voor lees-alleen toegang (niet vereist in eerste release).

### Applicatie-instellingen in SWA

Zet in de SWA configuratie (`Configuration` blade):

| Naam | Waarde |
|---|---|
| `AZURE_CLIENT_ID` | App registration → Application (client) ID |
| `AZURE_CLIENT_SECRET` | De zojuist aangemaakte client secret value |

In `BlazorAdmin/wwwroot/staticwebapp.config.json` staat al:
- `openIdIssuer`: vervang `TENANT_ID` door het tenant-id van Entra ID
- `clientIdSettingName: AZURE_CLIENT_ID`
- `clientSecretSettingName: AZURE_CLIENT_SECRET`

---

## 5. Roltoewijzing

1. Azure Portal → **Microsoft Entra ID** → **Enterprise applications** → `vrc-admin-swa`
2. **Users and groups** → **Add user/group**
3. Selecteer de gebruiker → **Select a role** → **Admin** → **Assign**

Alleen toegewezen gebruikers krijgen toegang. Niet-toegewezen gebruikers krijgen 403 op alle
`/api/admin/*` routes (afgedwongen door `staticwebapp.config.json`).

---

## 6. EntraAuthService in productie (automatisch geactiveerd)

`BlazorAdmin/Program.cs` detecteert de omgeving automatisch:

- **Development** (`ASPNETCORE_ENVIRONMENT=Development`) → `LocalAuthService` — altijd admin, geen login
- **Production** (SWA-deployment) → `AddMsalAuthentication` + `EntraAuthService` — echte Entra ID rollen

Er is geen handmatige stap nodig om te switchen. Vul wel de placeholders in `appsettings.Production.json` in:

```json
// BlazorAdmin/wwwroot/appsettings.Production.json
{
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/TENANT_ID_HIER_INVULLEN",
    "ClientId": "CLIENT_ID_HIER_INVULLEN",
    "ValidateAuthority": true
  }
}
```

- `TENANT_ID_HIER_INVULLEN` → Entra ID → **Overview** → Directory (tenant) ID
- `CLIENT_ID_HIER_INVULLEN` → Entra ID → **App registrations** → `vrc-admin-swa` → Application (client) ID

Commit `appsettings.Production.json` mee (bevat geen secrets — alleen tenant/client ID, die zijn publiek).

---

## 7. Lokaal testen met Azure SWA CLI (aanbevolen vóór productie)

De Azure Static Web Apps CLI emuleert het volledige SWA-gedrag lokaal: routering, auth-emulatie
en `/api/*` proxying naar de Function App. Hiermee kun je de authenticatieflow testen
**zonder een echte Entra ID app-registratie of Azure resource**.

### Vereisten (eenmalig)

1. **Node.js** ≥ 18 — [nodejs.org](https://nodejs.org/)
2. **Azure SWA CLI** installeren:
   ```powershell
   npm install -g @azure/static-web-apps-cli
   swa --version   # controleer installatie
   ```

### Opstarten

Stap 1 — start de bestaande backends (elk in een eigen terminal):

```powershell
# Terminal 1: Azurite (opslag emulator)
azurite --location $env:TEMP\azurite

# Terminal 2: Function App backend
cd FunctionApp
func start --port 7094

# Terminal 3: Blazor WASM dev server
cd BlazorAdmin
dotnet run --launch-profile http
# → draait op http://localhost:5242
```

Stap 2 — start de SWA emulator (vierde terminal):

```powershell
# In de repo root (waar swa-cli.config.json staat)
swa start sportlink-admin
# → SWA emulator draait op http://localhost:4280
```

### Wat de SWA CLI doet

| Functie | Gedrag |
|---|---|
| **Blazor WASM** | Proxy naar `http://localhost:5242` |
| **`/api/*`** | Proxy naar `http://localhost:7094` |
| **Routeregels** | `staticwebapp.config.json` afgedwongen (rollen, redirects) |
| **Auth emulatie** | Mock login op `http://localhost:4280/.auth/login/aad` |

### Authenticatie lokaal testen

Ga naar `http://localhost:4280` — de SWA CLI stuurt je door naar een mock login-pagina.
Vul daar in:

| Veld | Waarde voor testen |
|---|---|
| User ID | Willekeurig getal |
| Username | `testbeheerder` |
| Roles | `admin` |

Na "Login" ben je ingelogd als admin zonder enige Entra ID. Zo kun je controleren of:
- `/api/beheer/*` endpoints bereikbaar zijn met admin-rol
- Pagina's en navigatie kloppen voor ingelogde gebruikers
- Niet-admins netjes een 403 krijgen (rol leeg laten bij test)

> **Let op:** De `ASPNETCORE_ENVIRONMENT` van de Blazor dev server is `Development`,
> dus `LocalAuthService` is actief — de Blazor UI toont altijd admin.
> De SWA CLI emuleert de route-beveiliging (endpoints), niet de Blazor UI.
> Productie-test de UI door de Blazor app te publiceren en te serveren via `swa start`.

### Volledige productie-emulatie (optioneel)

Wil je ook de Blazor `Production`-omgeving testen (met echte EntraAuthService)?

```powershell
# Publiceer Blazor naar een lokale map
dotnet publish BlazorAdmin/BlazorAdmin.csproj -c Release -o ./blazor-publish

# Start SWA met de gepubliceerde bestanden (geen dev server)
swa start ./blazor-publish/wwwroot --api-devserver-url http://localhost:7094
```

Hierbij laadt Blazor `appsettings.Production.json` met de `AzureAd`-sectie.
Je hebt dan wél de echte Tenant ID en Client ID nodig (zie sectie 4 en 6).

---

## 8. Snel testen (endpoints direct)

```bash
# Lokaal: alle endpoints zonder authenticatie (host-key uitschakelen kan via local.settings.json)
curl http://localhost:7094/api/health
curl http://localhost:7094/api/beheer/settings
curl http://localhost:7094/api/beheer/sync/status
curl http://localhost:7094/api/beheer/templates
curl http://localhost:7094/api/beheer/voorkeurstijden
curl http://localhost:7094/api/beheer/email-log
curl -X POST -H "Content-Type: application/json" \
  -d '{"onderwerp":"test","afzender":"x@y.nl","body":"kunnen wij zaterdag 18:00 spelen?"}' \
  http://localhost:7094/api/test/email
```

Of gebruik het geautomatiseerde smoke-test script (zie sectie 9):

```powershell
.\scripts\smoke-test.ps1
```

---

## 9. Architectuur — bekende valkuilen bij lokale oplevering

Bij de v2-implementatie werden vier fouten pas bij runtime ontdekt die `dotnet build` gewoon liet
passeren. Documentatie hiervan zodat deze fouten nooit meer onopgemerkt voorbij komen.

### Valkuil 1: .NET runtime mismatch

**Symptoom:** `func start` crasht direct met exit code `0x80008096`, log toont
`Value cannot be null. (Parameter 'provider')`.

**Oorzaak:** `<TargetFramework>netX.0</TargetFramework>` in het csproj verwijst naar een .NET-versie
die niet geïnstalleerd is op de devmachine. `dotnet build` compileert succesvol mits de SDK
aanwezig is; de runtime is een andere installatie.

**Oplossing:** Controleer welke runtimes beschikbaar zijn (`dotnet --list-runtimes`) en zorg dat
`TargetFramework` daarmee overeenkomt. Huidig: `net10.0`.

**Controle:** `func start` toont "Worker process started and initialized" — anders is er een
runtime mismatch.

---

### Valkuil 2: Gereserveerd route-prefix `admin/`

**Symptoom:** Alle functies met `Route = "admin/..."` staan bij `func start` in error:
`"The specified route conflicts with one or more built in routes"`.

**Oorzaak:** De Azure Functions host reserveert `/admin/*` voor interne endpoints (key-management,
host status). Dit is gedocumenteerd maar niet uitgestoten door de compiler.

**Oplossing:** Gebruik nooit `admin/` als route-prefix. In deze codebase: `beheer/`.
- Fout: `Route = "admin/settings"`
- Correct: `Route = "beheer/settings"`

**Controle:** Zoek na elke nieuwe Function op `"admin/` in route-attributen.

---

### Valkuil 3: Transitive dependency vulnerability

**Symptoom:** `dotnet build` slaagt, maar bevat `NU1903 warning`: hoge ernst kwetsbaarheid
in een transitive package (`Microsoft.Kiota.Abstractions`).

**Oorzaak:** `Microsoft.Graph 5.x` sleepte een kwetsbare Kiota-versie mee
(GHSA-7j59-v9qr-6fq9). De vulnerability warning blokkeert later de Security Gate in CI.

**Oplossing:** Upgrade naar `Microsoft.Graph 6.0.3` (bevat de gefixte Kiota-versie).
Controleer met `dotnet build 2>&1 | Select-String "NU19"` dat er geen vulnerability warnings zijn.

**Controle:** 0 NU1903/NU1904 warnings in build output.

---

### Valkuil 4: CORS poort-mismatch

**Symptoom:** Blazor laadt, maar alle API-calls falen met CORS-error in de browser console.

**Oorzaak:** `BlazorAdmin/Properties/launchSettings.json` wijst naar poort 5242 (Blazor default),
maar de CORS-whitelist in `FunctionApp/Program.cs` bevatte alleen 5000/5001.

**Oplossing:** De CORS origins in `Program.cs` moeten de werkelijke Blazor dev-poort bevatten:
`http://localhost:5242` en `https://localhost:7242`.

**Controle:** `BlazorAdmin/Properties/launchSettings.json` → `applicationUrl` → controleer of alle
vermelde poorten in de CORS-origins staan.

---

### Smoke-test script

Het script `scripts/smoke-test.ps1` automatiseert alle bovenstaande controles:

```powershell
# Volledige smoke test (bouwt, start, controleert, ruimt op):
.\scripts\smoke-test.ps1

# Sneller (sla Blazor-startup over):
.\scripts\smoke-test.ps1 -SkipBlazor
```

Het script doorloopt:
1. `dotnet build FunctionApp` — bouwt met warnings-als-fouten check
2. `dotnet build BlazorAdmin` — idem
3. `func start --port 7094` — wacht op "Worker process started and initialized"
4. Endpoint checks: health, beheer/settings, beheer/sync/status, beheer/templates,
   beheer/voorkeurstijden, beheer/email-log, test/email
5. Route-conflict controle in de func log
6. Geen .NET runtime mismatch controle
7. `dotnet run BlazorAdmin` — wacht op "Now listening on:", haalt `<!DOCTYPE html` op
8. Opruimen (kill alle gestarte processen)

Exitcode 0 = alles groen. Exitcode 1 = minimaal één check gefaald.

**Wanneer uitvoeren:** Altijd vóór een commit of oplevering na significante codewijzigingen.
`dotnet build` slaagt ≠ werkt bij `func start`. De smoke test is de daadwerkelijke verificatie.
