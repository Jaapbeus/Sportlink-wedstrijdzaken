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

## 6. Aanzetten van EntraAuthService in Blazor

De huidige `Program.cs` registreert standaard `LocalAuthService` voor zowel Development als
Production. Voor échte productie-auth:

1. Voeg in `Program.cs` toe:
   ```csharp
   builder.Services.AddMsalAuthentication(options =>
   {
       builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
   });
   ```
2. Vervang `LocalAuthService` door `EntraAuthService` in de Production-tak van Program.cs
3. Voeg `appsettings.Production.json` toe met:
   ```json
   {
     "AzureAd": {
       "Authority": "https://login.microsoftonline.com/TENANT_ID",
       "ClientId": "APPLICATION_CLIENT_ID",
       "ValidateAuthority": true
     }
   }
   ```

> Dit is **bewust nog niet gedaan** in de eerste v2 release — SWA-routing in
> `staticwebapp.config.json` regelt al de daadwerkelijke beveiliging. EntraAuthService is alleen
> nodig zodra je rollen client-side wilt gebruiken (bijv. menu's verbergen).

---

## 7. Snel testen

```bash
# Lokaal: alle endpoints zonder authenticatie (host-key uitschakelen kan via local.settings.json)
curl http://localhost:7094/api/admin/settings
curl http://localhost:7094/api/admin/sync/status
curl http://localhost:7094/api/admin/templates
curl http://localhost:7094/api/admin/voorkeurstijden
curl http://localhost:7094/api/admin/email-log
curl -X POST -H "Content-Type: application/json" \
  -d '{"onderwerp":"test","afzender":"x@y.nl","body":"kunnen wij zaterdag 18:00 spelen?"}' \
  http://localhost:7094/api/test/email
```
