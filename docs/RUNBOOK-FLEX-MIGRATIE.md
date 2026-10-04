# Runbook — migratie Linux Consumption → Flex Consumption (epic #1063)

Draaiboek voor de eigenaar: de exacte volgorde van FLEX-05 tot en met FLEX-09, daarna de
`net10.0`-upgrade (issue #1073). Alle commando's zijn **sjablonen**: vul de placeholders lokaal in en
plak de echte waarden nooit terug in een issue, PR of commit (CLAUDE.md §4a).

| Placeholder | Betekenis |
|---|---|
| `func-[clubcode]-sportlink` | De bestaande productie-app (Linux Consumption) |
| `func-[clubcode]-sportlink-flex` | De nieuwe Flex-app |
| `plan-[clubcode]-sportlink-flex` | Het nieuwe Flex-plan (FC1) |
| `st[clubcode]sportlink` | Het bestaande storage account (wordt hergebruikt) |
| `[resourcegroep]` | De bestaande resource group (Flex-app komt in dezelfde, zelfde regio) |
| `[swa-url].azurestaticapps.net` | De Admin GUI; `[club-domein]` voor een eventueel eigen domein |
| `[flex-host]` | De standaardhostname van de Flex-app — **altijd opvragen, nooit samenstellen** (§3.4) |
| `[TENANT_ID]`, `[CLIENT_ID]` | Entra-tenant en App Registration |

> **Stand op 2026-10-02.** De code-kant is voorbereid: bicep (FLEX-04, al gemerged), dit runbook,
> een kant-en-klare testdeploy-workflow als sjabloon (bijlage A), en de inventaris uit FLEX-08 (§6).
> **Alles hieronder vanaf §3 is geblokkeerd door de kostengate FLEX-05 (issue #1068)**: er bestaat
> nog geen Flex-app, en alleen de eigenaar mag die laten aanmaken.

---

## 1. Volgorde in één oogopslag

| Stap | Issue | Wat | Productie-impact | Eigen go nodig |
|---|---|---|---|---|
| 0 | — | Prijscheck op de dag zelf (§2) | geen | — |
| FLEX-05 | #1068 | Flex-app aanmaken, vier kostencontroles, app settings, **triggers uit** | geen (nieuwe resource) | **ja — kostengate** |
| FLEX-06 | #1069 | Easy Auth inrichten op de Flex-app — **vóór er code op komt** | geen | — |
| FLEX-07 | #1070 | Testdeploy van `main`-code naar de Flex-app, health + functies | geen | workflow toevoegen (bijlage A) |
| FLEX-08 | #1071 | CORS op de Flex-app, browsertest via testpad | geen | — |
| FLEX-09 | #1072 | Cutover: variabelen om, timers om, oude app stoppen | **ja** | **ja — aparte go** |
| FLEX-10 | #1073 | `net10.0` — pas ná een geslaagde cutover + 24–48 uur observatie | via gewone release | — |

**Waarom FLEX-06 vóór FLEX-07 moet (en niet parallel, zoals #1070 nog zegt).**
`EasyAuthHelper.RequireRole` leest de rol uit de header `X-MS-CLIENT-PRINCIPAL`. Die header is alleen
betrouwbaar als Easy Auth hem zelf zet en een meegestuurde versie wegstript. Staat Easy Auth op de
Flex-app nog uit, dan geeft één vervalste header — precies wat de smoke test in `deploy.yml` stuurt —
**admin-toegang tot de productiedatabase**, want de Flex-app krijgt dezelfde connectiestring. De
lokale bypass (`WEBSITE_SITE_NAME` afwezig) speelt hier niet: Azure zet die variabele op elke app.
De testdeploy-workflow uit bijlage A weigert daarom te deployen zolang Easy Auth uit staat.

---

## 2. Prijscheck op de dag van uitvoering (verplicht vóór FLEX-05)

Niet vertrouwen op de cijfers in dit document of in #1063/#1065 — het kostenbeleid bestaat juist
omdat Microsoft gratis tiers zonder aankondiging wijzigt.

1. **Microsoft Learn MCP** — `microsoft_docs_search("Flex Consumption plan billing free grant always ready")`.
   De Learn-pagina's bevestigen het *mechanisme* (on-demand heeft een free grant, *"In always ready
   billing, there are no free grants"*) maar **noemen de bedragen niet** en verwijzen naar de
   prijspagina.
2. **Prijspagina** — <https://azure.microsoft.com/pricing/details/functions/>, sectie *Flex
   Consumption*: controleer de zin over de maandelijkse free grant.
3. Leg datum + uitkomst vast als comment op #1068. Bij **elke** afwijking: harde stop met het
   meldingsformat uit CLAUDE.md ("KOSTENWIJZIGING GEDETECTEERD — DEPLOYMENT GESTOPT").

Laatst uitgevoerd **2026-10-02**: *"a monthly free grant of 250,000 executions and 100,000 GB-s of
resource consumption per month per subscription in pay-as-you-go on-demand pricing across all
function apps in that subscription"*; voetnoot: free grants gelden alleen voor de on-demand-meters.
Ongewijzigd t.o.v. 2026-09-12 en 2026-09-16. De bedragen per GB-s worden op die pagina met
JavaScript ingevuld en waren via een fetch niet leesbaar — controleer ze in de browser.

---

## 3. FLEX-05 — Flex-app aanmaken (kostengate, issue #1068)

### 3.1 Niet `main.bicep` uitrollen — alleen de Flex-module

> **Vangnet sinds #1455:** de deploy-actie van `infrastructure.yml` draait eerst een what-if-poort
> (`scripts/ci/check-whatif-appsettings.sh`) die faalt als een bestaande app setting zou verdwijnen,
> en toont dan alleen de namen. Dat is een vangnet, geen reden om `main.bicep` alsnog uit te rollen.

> ⚠️ **Gevonden bij de voorbereiding (2026-10-02), niet eerder benoemd.** `infrastructure/main.bicep`
> declareert óók de bestaande productie-app (`modules/function-app.bicep`), met een
> `siteConfig.appSettings`-lijst van zes settings. Een `az deployment group create` van
> `main.bicep` **vervangt** de app settings van de productie-app door die zes: weg zijn dan onder
> meer `POSTGRES_CONNECTION_STRING`, alle secrets uit groep (c) van #1064, en de SAS-URL in
> `WEBSITE_RUN_FROM_PACKAGE` (bicep zet `'1'`). Dat is een productiestoring. De what-if van FLEX-04
> toonde deze "Modify"-diffs ook — ze werden terecht als bestaande drift herkend, maar een `create`
> voert drift uit. Hetzelfde geldt voor de `deploy`-actie van `.github/workflows/infrastructure.yml`,
> die bovendien `main.parameters.json` met de letterlijke placeholders gebruikt.

Rol daarom **uitsluitend `modules/function-app-flex.bicep`** uit, als zelfstandige template. De module
is resource-group-scoped en verwijst naar het storage account met `existing`; hij raakt geen enkele
bestaande resource behalve dat hij één blob-container en drie role assignments op dat storage account
toevoegt.

### 3.2 Vooraf

```bash
az login                                    # juiste tenant; az account show ter controle
az account set --subscription "[subscription]"

# Host-ID-botsing uitsluiten: twee apps op hetzelfde storage account botsen als de eerste 32 tekens
# van hun (lowercase) naam gelijk zijn. De nieuwe naam is langer dan de oude, dus dat kan alleen als
# de oude naam zelf al 32 tekens of langer is.
printf '%s' "func-[clubcode]-sportlink" | wc -c      # moet < 32 zijn

# What-if van ALLEEN de module — moet uitsluitend Create tonen (plan, site, appsettings,
# container, 3 role assignments) en GEEN Modify op een bestaande resource.
az deployment group what-if \
  --resource-group "[resourcegroep]" \
  --template-file infrastructure/modules/function-app-flex.bicep \
  --parameters flexFunctionAppName="func-[clubcode]-sportlink-flex" \
               flexAppServicePlanName="plan-[clubcode]-sportlink-flex" \
               storageAccountName="st[clubcode]sportlink" \
               instanceMemoryMB=2048 maximumInstanceCount=5 \
               tenantId="[TENANT_ID]" clientId="[CLIENT_ID]"
```

Geef `appInsightsConnectionString` en `sqlConnectionString` hier **niet** mee: dat zijn secrets op
de commandoregel. Die gaan in §3.5 via een prompt.

### 3.3 Aanmaken (pas na de go op #1068 en de prijscheck van §2)

```bash
az deployment group create \
  --resource-group "[resourcegroep]" \
  --template-file infrastructure/modules/function-app-flex.bicep \
  --parameters flexFunctionAppName="func-[clubcode]-sportlink-flex" \
               flexAppServicePlanName="plan-[clubcode]-sportlink-flex" \
               storageAccountName="st[clubcode]sportlink" \
               instanceMemoryMB=2048 maximumInstanceCount=5 \
               tenantId="[TENANT_ID]" clientId="[CLIENT_ID]"
```

Met `tenantId`/`clientId` rolt de module meteen het `authsettingsV2`-blok uit; zie §4 voor wat daarna
nog gecorrigeerd moet worden.

> **Deze module daarna nooit opnieuw uitrollen zonder alle app settings mee te geven.** De resource
> `Microsoft.Web/sites/config` `appsettings` vervangt de complete lijst. Een tweede `create` wist alles
> wat in §3.5 met de CLI is toegevoegd — inclusief de uitgeschakelde triggers van §3.6.

### 3.4 De vier kostenkritische verificaties (plus status en hostname)

```bash
APP="func-[clubcode]-sportlink-flex"; RG="[resourcegroep]"
ID=$(az functionapp show -g "$RG" -n "$APP" --query id -o tsv)
az rest --method get --url "https://management.azure.com${ID}?api-version=2024-04-01" \
  --query "{state:properties.state, sku:properties.sku, httpsOnly:properties.httpsOnly,
            host:properties.defaultHostName,
            memory:properties.functionAppConfig.scaleAndConcurrency.instanceMemoryMB,
            maxInstances:properties.functionAppConfig.scaleAndConcurrency.maximumInstanceCount,
            alwaysReady:properties.functionAppConfig.scaleAndConcurrency.alwaysReady}" -o jsonc
```

| Controle | Verwacht | Waarom |
|---|---|---|
| Plan type | `sku` = `FlexConsumption` | Anders is het niet het plan dat de free grant van §2 krijgt |
| Instance memory | `2048` (512 alleen als bewuste test, §5.5) | #1065: 512 MB werd op 31/31 dagen overschreden |
| Always ready | leeg / `[]` | **Eén always-ready-instance en het gratis tegoed vervalt volledig** |
| Max instance count | `5` (2 à 5), nooit 100 | Geen kostenrem op Pay-As-You-Go; dit begrenst alleen de snelheid |
| Status / HTTPS | `Running`, `httpsOnly: true` | #1064 bevinding 1: de oude app staat op `false` |
| Hostname | noteer `[flex-host]` lokaal | Zie hieronder |

**Let op bij de kostenraming van #1065.** Volgens de huidige Learn-tekst geldt
`maximumInstanceCount` *"to each independently-scaling function group rather than to the app's
combined instances"* ([flex-consumption-plan#scale-out-rate](https://learn.microsoft.com/azure/azure-functions/flex-consumption-plan#scale-out-rate)).
Bij per-function scaling krijgen de HTTP-functies samen één groep, maar schalen niet-HTTP-triggers
afzonderlijk. Het theoretische maximum van "$681 per maand bij 5" uit #1065 is dus een
ondergrens per groep, geen plafond voor de hele app. Aan het werkelijke risico verandert dit weinig
(de timers draaien seconden per dag), maar het hoort in de afweging van FLEX-12.

**Hostname.** Een app die via de **portal** wordt aangemaakt krijgt tegenwoordig een unieke
standaardhostname `<naam>-<hash>.<regio>.azurewebsites.net`; een ARM/bicep-deploy zonder
`autoGeneratedDomainNameLabelScope` (zoals deze module) krijgt de klassieke `<naam>.azurewebsites.net`
([bron](https://learn.microsoft.com/azure/app-service/reference-dangling-subdomain-prevention#secure-unique-default-hostnames-recommended)).
`deploy.yml` stelt de hostname op vijf plekken samen uit de app-naam (§7.2) — maak de app dus via de
bicep aan, niet via de portal, of pas die smoke tests aan vóór de cutover.

### 3.5 App settings overzetten

Bron: de indeling uit #1064. De bicep zet er al vier (`AzureWebJobsStorage__accountName`,
`AzureWebJobsStorage__credential`, `APPLICATIONINSIGHTS_CONNECTION_STRING` (leeg),
`SqlConnectionString` (leeg)). Secrets nooit via een tussenbestand of als argument: gebruik een
prompt.

> **`AzureWebJobsStorage` is op Flex identity-based (#1512).** Er is geen connection string; de code
> (`Planner.Shared/Infrastructure/OpslagVerbinding.cs`) gebruikt `AzureWebJobsStorage` als die er is,
> anders `AzureWebJobsStorage__accountName` (+ optioneel `__clientId`, `__tableServiceUri`,
> `__queueServiceUri`) met de managed identity. Zet dus nooit een connection string terug.

```bash
# Groep (a) — functionele configuratie, waarden overnemen van de bestaande app:
az functionapp config appsettings set -g "$RG" -n "$APP" --settings \
  FETCH_SCHEDULE="[waarde]" EMAIL_POLL_SCHEDULE="[waarde]" EmailProcessorEnabled="[waarde]" \
  EmailReviewMode="[waarde]" EmailReviewRecipient="[waarde]" \
  GitHubOwner="[waarde]" GitHubRepo="[waarde]" \
  AzureSubscriptionId="[waarde]" AzureResourceGroupName="[resourcegroep]" \
  AzureFunctionAppName="func-[clubcode]-sportlink-flex"

# Groep (c) — secrets, één voor één via een verborgen prompt (komt niet in de shellhistory):
read -rs -p "POSTGRES_CONNECTION_STRING: " V; echo
az functionapp config appsettings set -g "$RG" -n "$APP" --settings "POSTGRES_CONNECTION_STRING=$V" -o none; unset V
# idem voor APPLICATIONINSIGHTS_CONNECTION_STRING, GraphClientSecret, GraphClientId, GraphTenantId,
# GraphMailbox, GitHubPat, OpenAiApiKey, MICROSOFT_PROVIDER_AUTHENTICATION_SECRET (alleen als Easy
# Auth hem gebruikt — #1064 meldt clientSecretSettingName = null, dan niet nodig).
```

Drie afwijkingen van "gewoon overnemen":

1. **`AzureFunctionAppName` = de Flex-app, niet de oude.** `AdminSettingsFunction` schrijft bij het
   wijzigen van het sync-schema `FETCH_SCHEDULE` via de Azure Management API naar de app met díe naam.
   Met de oude naam zou een wijziging op de Flex-app de app settings van de productie-app herschrijven
   (mits de identity daar rechten heeft). Geef de system-assigned identity van de Flex-app
   daarvoor de rol *Website Contributor* **op de Flex-app zelf**, zoals de oude app die heeft.
2. **Alleen de actieve databasetier.** #1064: beide connectiestrings staan op de oude app. Neem
   alleen die van `vars.DatabaseTier` mee (vandaag `POSTGRES_CONNECTION_STRING`); laat
   `SqlConnectionString` leeg.
3. **Nooit zetten op Flex** (de bicep doet dat al niet, en de pipeline ook niet, zie §5.3):
   `FUNCTIONS_WORKER_RUNTIME`, `FUNCTIONS_EXTENSION_VERSION`, `WEBSITE_RUN_FROM_PACKAGE`,
   `WEBSITE_CONTENTSHARE`, `WEBSITE_CONTENTAZUREFILECONNECTIONSTRING`, `WEBSITE_MOUNT_ENABLED`,
   `WEBSITE_USE_PLACEHOLDER_DOTNETISOLATED`, `SCM_DO_BUILD_DURING_DEPLOYMENT`, `ENABLE_ORYX_BUILD`,
   `WEBSITE_MAX_DYNAMIC_APPLICATION_SCALE_OUT`, `WEBSITE_TIME_ZONE`, `TZ`. Volledige lijst:
   [Flex Consumption plan deprecations](https://learn.microsoft.com/azure/azure-functions/functions-app-settings#flex-consumption-plan-deprecations).

### 3.6 Alle niet-HTTP-triggers uitschakelen vóór de eerste codedeploy

Tot de cutover is de oude app productie. Draait de Flex-app zijn triggers ook, dan gebeurt alles
dubbel tegen **dezelfde database en hetzelfde storage account**: dubbele dagelijkse sync, dubbele
e-mailverwerking (dus dubbele antwoorden naar clubleden), dubbele Sportlink-tokenrefresh, en de
queue-trigger `SyncJobProcessor` haalt berichten van de gedeelde queue `sync-jobs` weg voor de oude
app. Microsoft classificeert een gedeelde storage-queue als *High* risico bij migratie.

Gemeten uit de `functions.metadata` van een `dotnet publish` van `FunctionApp.Postgres` op
2026-10-02 (108 functies, waarvan 11 niet-HTTP):

```bash
for F in PostgresFetchAndStoreApiData ProcessIncomingEmails SyncJobProcessor \
         SportlinkTokenKeepAlive SportlinkPublicMatchIdWarmup SportlinkContractCheck \
         DatabaseUitvalMonitor CleanupEmailVerwerking CleanupTeambegeleiding \
         CleanupAppSettingsAudit CleanupSportlinkMutationAudit; do
  az functionapp config appsettings set -g "$RG" -n "$APP" \
    --settings "AzureWebJobs.${F}.Disabled=true" -o none
done
```

Deze lijst is een momentopname. De testdeploy-workflow (bijlage A) leidt hem bij elke run opnieuw af
uit het artefact en weigert te deployen als er één ontbreekt — een nieuwe timer valt dus niet stil
door de mand.

---

## 4. FLEX-06 — Easy Auth op de Flex-app (issue #1069)

Doel: dezelfde structuur als de bestaande app, vastgelegd in #1064 (`platform.enabled = true`,
`unauthenticatedClientAction = AllowAnonymous`, `requireAuthentication = true`, Entra-provider met
`allowedAudiences = [api://[CLIENT_ID]]`, token store uit, `requireHttps = true`).

**Twee verschillen tussen de bicep-module en de live-structuur van #1064** — controleren, niet
blind uitrollen:

| Veld | `function-app-flex.bicep` | Live (#1064) |
|---|---|---|
| `globalValidation.requireAuthentication` | `false` | `true` |
| `registration.openIdIssuer` | `https://sts.windows.net/[TENANT_ID]/v2.0` | `https://login.microsoftonline.com/[TENANT_ID]/v2.0` |

Met `AllowAnonymous` laat Easy Auth in beide gevallen anonieme requests door; het verschil zit in
wat er met een ongeldig token gebeurt. Neem de live-waarden over, dan is de 3-user-test een
vergelijking met bekend gedrag.

```bash
az webapp auth show -g "$RG" -n "$APP" -o jsonc        # structuur bekijken (bevat geen secrets)
```

**Entra hoeft voor de Function App-hostname niet te veranderen.** Bij de voorbereiding vastgesteld:
de audience is `api://[CLIENT_ID]` (geen hostname), en de redirect-URI's in de App Registration horen
bij de Admin GUI op de SWA (`https://[swa-url].azurestaticapps.net/authentication/login-callback`),
niet bij de Function App. De taak *"SPA redirect URI toevoegen voor de nieuwe hostname"* in #1069
is daarom niet nodig — tenzij het testpad van §6.4 een tijdelijke localhost-URI nodig heeft.
`scripts/azure/Configure-EntraApp.ps1` raakt geen redirect-URI's.

Daarna: de verplichte 3-user-test (admin / user / geen rol) per CLAUDE.md en
[ENTRA-AUTH-BEHEER.md](ENTRA-AUTH-BEHEER.md), in een verse incognito-sessie. Die test kan pas ná
FLEX-07 (er moet code op de app staan), maar Easy Auth zelf staat dan al aan.

---

## 5. FLEX-07 — deploy naar de Flex-app (issue #1070)

### 5.1 Ontwerp: een aparte, tijdelijke workflow — `deploy.yml` blijft ongewijzigd

De eerste gedachte uit #1070 was een `workflow_dispatch`-input op `deploy.yml`. Dat is afgewezen:
een dispatch van `deploy.yml` draait ook `db-migrate-postgres` (migraties op productie),
`blazor-deploy` (de Admin GUI naar productie) en `deployment-summary`. Alleen het `app-name`-veld
omzetten maakt een testdeploy dus tegelijk een GUI-release en een migratieronde; die jobs
uitschakelen vraagt een `if:` op bijna elke job, en dan is "het reguliere pad is identiek" alleen
nog per regel te beredeneren.

Gekozen: **een losse workflow `.github/workflows/deploy-flex-test.yml`** (sjabloon in bijlage A),
die alleen `build` → `deploy-flex` → `smoke-flex` doet en bij de cutover weer wordt verwijderd.

> **Status: niet toegevoegd aan de repository.** Bij het voorbereiden op 2026-10-02 weigerde de
> veiligheidsclassificatie van de agentomgeving het aanleggen van een nieuwe Azure-deployworkflow
> zonder expliciete toestemming van de eigenaar. Het sjabloon is gevalideerd (bijlage A) maar
> wordt pas een workflow als de eigenaar hem toevoegt — dat is ook het natuurlijke moment, want
> zonder Flex-app en zonder secret `AZURE_FUNCTIONAPP_NAME_FLEX` faalt hij toch op de eerste stap.

### 5.2 Waarom het reguliere pad aantoonbaar identiek blijft

`deploy.yml` wordt niet gewijzigd: `git diff origin/develop -- .github/workflows/deploy.yml` is
leeg. Per job:

| Job in `deploy.yml` | Na toevoegen van de testworkflow |
|---|---|
| `db-check`, `db-migrate`, `db-migrate-postgres` | Ongewijzigd; de testworkflow heeft geen migratiejob en raakt de database niet |
| `build` | Ongewijzigd; de testworkflow heeft een eigen kopie met dezelfde vier stappen en dezelfde gepinde SHA's, plus een extra controle |
| `deploy` | Ongewijzigd, zelfde `app-name: secrets.AZURE_FUNCTIONAPP_NAME \|\| vars.AZURE_FUNCTIONAPP_NAME` |
| `test`, `blazor-deploy`, `deployment-summary` | Ongewijzigd; de testworkflow deployt geen GUI en schrijft geen productierapport |
| Triggers | `deploy.yml`: `push: main` + `workflow_dispatch` (ongewijzigd). Testworkflow: alleen `workflow_dispatch`, alleen vanaf `main` |

Ook de artefactnaam verschilt (`functionapp-flex`), en artefacten zijn per run gescheiden, dus de
twee workflows kunnen elkaars pakket niet oppakken.

### 5.3 Vastgesteld over `Azure/functions-action` op de gepinde versie

Gecontroleerd in de broncode op de SHA uit `deploy.yml` (`c5060b3b…`, de commit achter tag
`v1.5.7`), niet alleen in de README:

- **One Deploy wordt automatisch gekozen.** `src/handlers/contentPreparer.ts`: met RBAC-credentials
  (onze `AZURE_CREDENTIALS`) leest de action `properties.sku` van de site; bij `FlexConsumption`
  wordt `PublishMethodConstant.OneDeployFlex` (`POST https://<scm>/api/publish`), bij `Dynamic` op
  Linux `WebsiteRunFromPackageDeploy`. De input `sku` is alleen nodig bij publish-profile-auth
  (README, parametertabel).
- **De pipeline zet op het Flex-pad geen vervallen app settings.** Alleen de huidige route
  (`websiteRunFromPackageDeploy.ts`) patcht `WEBSITE_RUN_FROM_PACKAGE`; alleen `zipDeploy.ts` zet
  `SCM_DO_BUILD_DURING_DEPLOYMENT`/`ENABLE_ORYX_BUILD`. `oneDeployFlex.ts` doet geen van beide.
  `deploy.yml` zelf zet nergens een app setting, en de Flex-module in `infrastructure/` evenmin
  (wel de oude module: `FUNCTIONS_WORKER_RUNTIME`, `FUNCTIONS_EXTENSION_VERSION`,
  `WEBSITE_RUN_FROM_PACKAGE` — die blijft ongewijzigd tot FLEX-13).
- **`remote-build` is niet nodig.** Default `false`; het artefact is een voorgebouwde
  `dotnet publish`-output. README: *"For a Flex Consumption plan app … don't set
  scm-do-build-during-deployment or enable-oryx-build."* — `deploy.yml` zet ze ook niet.
- **Geen slot-logica.** Geen `slot-name` in `deploy.yml`; README: *"Currently not supported in a
  Flex Consumption plan."*
- **`.azurefunctions` zit in het pakket.** `include-hidden-files: true` staat in de upload, en beide
  publish-routes pakken de map in met dezelfde `archiveFolder`-functie
  (`azure-actions-utility/ziputility.js`). De huidige productie-app draait op precies die route met
  dit artefact, dus het verborgen mapje komt aan. Lokaal geverifieerd: `dotnet publish
  FunctionApp.Postgres` levert `.azurefunctions/` en `functions.metadata` op.

### 5.4 Uitvoeren

1. Eigenaar: secret `AZURE_FUNCTIONAPP_NAME_FLEX` zetten (Secret, geen Variable — #1204).
2. Eigenaar: `deploy-flex-test.yml` uit bijlage A toevoegen via een PR naar `develop`, mee naar
   `main` met de eerstvolgende release (de workflow draait alleen vanaf `main`).
3. Actions → *Testdeploy naar Flex-app* → Run workflow (branch `main`).
4. Per job controleren: `gh run view <run-id> --json jobs --jq '.jobs[] | {name, conclusion}'`.
5. In de log van *Deploy naar Flex-app* moet staan: `Will use Kudu https://<scmsite>/api/publish to
   deploy since Flex consumption plan is detected.` — dat is het bewijs dat One Deploy gekozen is.

De workflow weigert te deployen als: de run niet vanaf `main` komt; de doel-app gelijk is aan de
productie-app; de doel-app geen `FlexConsumption` is; `alwaysReady` niet leeg is; instance memory
niet 512/2048 is; `maximumInstanceCount` > 5 is; Easy Auth uit staat; of één niet-HTTP-trigger niet
is uitgeschakeld. Daarna controleert hij health (200, juiste versie, database online,
instellingen geladen, geen openstaande migraties), 401 zonder token, 401 met een vervalste principal,
en dat élke functie uit het artefact op de app geregistreerd staat.

### 5.5 Geheugentest (512 vs 2048 MB)

#1065 stelde voor in FLEX-07 eenmaal op 512 MB te testen. Doe dat **niet met de timer** (die staat
uit, §3.6) maar met een handmatige sync via de Admin GUI-route op de Flex-app, of via de
sync-route `/api/postgres/sync-matches` met een admin-token. Bij een OOM of een herstart in de logs:
`az functionapp scale config set -g "$RG" -n "$APP" --instance-memory 2048` en opnieuw. 4096 MB
valt buiten het tegoed en is een architectuurbevinding, geen instelling.

---

## 6. FLEX-08 — hostname, CORS en configuratie (issue #1071)

### 6.1 Inventaris: waar de Function App-hostname vandaan komt

| # | Plek | Hoe | Wijzigt bij cutover |
|---|---|---|---|
| 1 | `BlazorAdmin/wwwroot/appsettings.Production.template.json` → `FunctionBaseUrl` | Token `{{AZURE_FUNCTIONAPP_URL}}`, gevuld door `deploy.yml` (job `blazor-deploy`) | Ja — via secret/variable `AZURE_FUNCTIONAPP_URL` |
| 2 | `BlazorAdmin/wwwroot/staticwebapp.config.json` → CSP `connect-src` | Zelfde token, `sed` in `blazor-deploy` | Ja — zelfde secret/variable |
| 3 | `BlazorAdmin/wwwroot/appsettings.json` | `http://localhost:7094` — alleen lokaal | Nee |
| 4 | `BlazorAdmin/Program.cs` | Leest `FunctionBaseUrl` uit configuratie; zowel `BaseAddress` als de `authorizedUrls` van de MSAL-handler | Nee (volgt #1) |
| 5 | `.github/workflows/deploy.yml` → `deploy`.`app-name` | `secrets.AZURE_FUNCTIONAPP_NAME \|\| vars.AZURE_FUNCTIONAPP_NAME` | Ja |
| 6 | `.github/workflows/deploy.yml` → `test` (5 stappen) | `https://${FUNCTIONAPP_NAME}.azurewebsites.net/...` — **samengesteld uit de naam** | Ja, en zie §7.2 |
| 7 | `.github/workflows/deploy.yml` → `test` → `AZURE_FUNCTION_KEY` | Function key van de app (de `?code=`-stappen verwachten 401) | Ja — key van de Flex-app |
| 8 | App setting `AzureFunctionAppName` | Naam voor de Management API-herstart in `AdminSettingsFunction` | Ja — al bij FLEX-05 (§3.5) |
| 9 | CORS op de Function App | Platforminstelling (`az functionapp cors`), **niet** in code of bicep | Ja — op de nieuwe app opnieuw zetten |
| 10 | Entra App Registration | Geen: audience `api://[CLIENT_ID]`, redirect-URI's horen bij de SWA | Nee (§4) |
| 11 | `docs/api-standaarden/openapi.yaml`/`.json` → `servers` | Placeholder `func-{clubcode}-sportlink` | Nee |
| 12 | Docs: `SETUP-NIEUWE-CLUB.md`, `docs/DEVELOPER-SETUP.md`, `docs/SETUP-CHECKLIST.md`, `docs/CUSTOM-DOMAIN.md` | Placeholders en uitleg van #1–#9 | Nee; wel `-flex` vermelden na FLEX-13 als de naam blijft |

### 6.2 Bewijs: geen hardcoded hostname in broncode

Uitgevoerd op `origin/develop` (2026-10-02), elk met nul echte hostnames als uitkomst:

```bash
# 1. Elke azurewebsites.net-vermelding buiten markdown
git grep -n -I -E '[A-Za-z0-9-]+\.azurewebsites\.net' -- ':!*.md'
#    → alleen openapi.yaml/.json (placeholder func-{clubcode}-sportlink) en
#      scripts/security/Clean-GitHistory.ps1 (vervangtemplate op basis van een prompt)

# 2. Elke http(s)-URL in de Blazor-broncode, minus localhost/Entra/schema's
git grep -n -I -E 'https?://' -- 'BlazorAdmin/**/*.cs' 'BlazorAdmin/**/*.razor' \
  'BlazorAdmin/**/*.json' 'BlazorAdmin/**/*.js' 'BlazorAdmin/wwwroot/*.html' ':!**/lib/**' \
  | grep -v -E 'localhost|login\.microsoftonline\.com|schemas?\.|w3\.org|github\.com/'
#    → alleen club.sportlink.com (externe dienst), invoer-placeholders en doc-links

# 3. Hostnames in code, scripts, workflows en IaC
git grep -n -I -E '\.azurewebsites\.net|azurestaticapps\.net' -- \
  '*.cs' '*.razor' '*.bicep' '*.ps1' '*.psm1' '*.sh' '*.yml' '*.json'
#    → deploy.yml (5x, samengesteld uit ${FUNCTIONAPP_NAME}), commentaar en prompts met placeholders
```

De CI-job *Club-infrastructuur patrooncheck* (`security-scan.yml`) bewaakt dit daarnaast op elke
push met het patroon `[a-z0-9-]+-sportlink\.azurewebsites\.net`.

Eén onjuiste opmerking gecorrigeerd: `FunctionApp/Program.cs` stelde dat CORS in productie niet
nodig is *"omdat SWA proxying alles op dezelfde origin houdt"*. Er is geen SWA-proxying (CLAUDE.md,
v2.0-architectuur): de browser roept de Function App rechtstreeks aan, dus CORS is juist verplicht.

### 6.3 CORS op de Flex-app

```bash
az functionapp cors show -g "$RG" -n "func-[clubcode]-sportlink"      # bron: de oude app
az functionapp cors add  -g "$RG" -n "$APP" --allowed-origins "https://[swa-url].azurestaticapps.net"
# en, als in gebruik: https://[club-domein-van-de-gui]  (zie CUSTOM-DOMAIN.md)
az functionapp cors show -g "$RG" -n "$APP"
```

Neem precies de origins van de oude app over — niet meer. Geen `*`, en `supportCredentials` blijft
zoals op de oude app (de client stuurt een Bearer-header, geen cookies).

### 6.4 Testpad: de Admin GUI tegen de Flex-app vóór de cutover

Een CORS-fout is alleen in een echte browser zichtbaar (niet op `:5242`, niet op de SWA-emulator).
Twee opties, beide tijdelijk en na afloop terug te draaien:

- **A (aanbevolen) — productie-build lokaal tegen de Flex-app.** `appsettings.Production.json` lokaal
  genereren met `FunctionBaseUrl=https://[flex-host]` (zelfde `sed` als `blazor-deploy`, bestand
  nooit committen), `dotnet publish BlazorAdmin -c Release`, serveren via de SWA CLI op `:4280`.
  Tijdelijk nodig: CORS-origin `http://localhost:4280` op de Flex-app en een SPA redirect-URI
  `http://localhost:4280/authentication/login-callback` in de App Registration. Beide direct na de
  test weer verwijderen.
- **B — direct bij de cutover.** Geen testpad; de live-check van CLAUDE.md regel 2a is dan de eerste
  echte browsertest, met de rollback van §7.4 als vangnet.

---

## 7. FLEX-09 — cutover (issue #1072, eigen go/no-go)

### 7.1 Rollbackcriterium — vastleggen vóór de cutover

Terug naar de oude app als binnen 2 uur na de cutover één van deze optreedt: de live
browsercheck (regel 2a) faalt; de smoke test van `deploy.yml` faalt; een beheerscherm laadt geen
gegevens; de 3-user-test geeft een afwijkend resultaat. Kies een moment buiten de dagelijkse sync en
buiten een wedstrijdweekend.

### 7.2 Stappen

1. **Smoke test-hostnames in `deploy.yml` controleren.** De vijf stappen in job `test` bouwen
   `https://${FUNCTIONAPP_NAME}.azurewebsites.net`. Klopt `[flex-host]` daarmee niet (unieke
   hostname, §3.4), dan eerst die vijf stappen omzetten naar de hostname uit `AZURE_FUNCTIONAPP_URL`
   — in een gewone PR, vóór de cutover.
2. **Triggers omzetten, in deze volgorde:** eerst op de **oude** app alle niet-HTTP-triggers
   uitschakelen (`AzureWebJobs.<naam>.Disabled=true`, lijst §3.6), dán op de Flex-app de
   `Disabled`-settings verwijderen
   (`az functionapp config appsettings delete -g "$RG" -n "$APP" --setting-names AzureWebJobs.<naam>.Disabled`).
   Zo draait een timer nooit op twee apps tegelijk.
3. **GitHub-configuratie omzetten** (eigenaar): `AZURE_FUNCTIONAPP_NAME` → de Flex-app,
   `AZURE_FUNCTIONAPP_URL` → `https://[flex-host]`, `AZURE_FUNCTION_KEY` → de default key van de
   Flex-app (`az functionapp keys list -g "$RG" -n "$APP"` — niet loggen). Steeds als Secret.
4. **De tijdelijke workflow verwijderen**: `deploy-flex-test.yml` (bijlage A) in dezelfde PR als stap 1,
   of direct erna.
5. **Volledige deploy vanaf `main`**, daarna per job (CLAUDE.md regel 2, stap C) en de live
   browsercheck (regel 2a). `blazor-deploy` zet de nieuwe URL in `FunctionBaseUrl` én in de CSP.
6. **Oude app stoppen, niet verwijderen**: `az functionapp stop -g "$RG" -n "func-[clubcode]-sportlink"`.
7. **Een volledige cyclus observeren**: minimaal één uurlijkse keepalive en één dagelijkse sync
   geslaagd op de Flex-app, daarna 24–48 uur voor FLEX-10.

### 7.3 Na de cutover

- Kostenbewaking (FLEX-12): metrics *On Demand Function Execution Units* en *Count* (§2 van
  [functions-consumption-costs](https://learn.microsoft.com/azure/azure-functions/functions-consumption-costs#viewing-and-estimating-costs-from-metrics)),
  plus *Always Ready Units* — die moet 0 blijven.
- Opruimen (FLEX-13): oude app, oud plan, de SAS-URL-blob in `github-actions-deploy`, en
  `modules/function-app.bicep` uit `main.bicep`.

### 7.4 Rollback

1. Oude app starten: `az functionapp start -g "$RG" -n "func-[clubcode]-sportlink"`.
2. Triggers terug: op de Flex-app weer uitschakelen, op de oude app de `Disabled`-settings verwijderen.
3. De drie GitHub-secrets terugzetten.
4. Admin GUI terugzetten: een `deploy.yml`-run vanaf `main` (die deployt ook de Function App naar de
   oude app — dezelfde code, dus onschadelijk).
5. CORS en Easy Auth op de oude app zijn nooit gewijzigd en hoeven niet hersteld.

---

## 8. Daarna — `net10.0` (issue #1073)

Pas na een geslaagde cutover en 24–48 uur observatie. Volgorde en scope staan in #1073 (inclusief
de scope-aanvulling: alle negen `net9.0`-projecten, de zes `(net9.0)`-jobnamen in `build.yml`, en
`DOTNET_VERSION`/`dotnet-version: 9.0.x` in `deploy.yml`). In de bicep: `runtime.version` naar
`'10.0'` in `function-app-flex.bicep`. **Status 2026-10-04:** eigenaarsbesluit — .NET 10 gaat nu naar productie (#1073, #1074). De rollback van
§7.4 naar de oude Linux Consumption-app is daarmee **niet meer van toepassing**: een `net10.0`-build
is daarvoor een productiebreker en de oude app staat stil. Plan FLEX-13 (oude app opruimen) daarom pas ná
#1073.

---

## Bijlage A — sjabloon `.github/workflows/deploy-flex-test.yml`

Gevalideerd op 2026-10-02 met `actionlint` 1.7.12 (inclusief shellcheck): geen bevindingen. De
`jq`-filter voor de niet-HTTP-triggers is lokaal getest op de echte `functions.metadata` (11
functies, zie §3.6). Niet gedraaid tegen Azure — dat kan pas na FLEX-05.

Twee punten die pas de eerste echte run bewijst: of `az functionapp function list` op een Flex-app
de uitgeschakelde functies meelevert (aangenomen, niet geverifieerd), en of `properties.sku` in de site-GET de waarde `FlexConsumption` heeft (de action leest
exact dat veld, zie §5.3).

```yaml
name: Testdeploy naar Flex-app (FLEX-07, tijdelijk)

# ──────────────────────────────────────────────────────────────────────────────
# Epic #1063, FLEX-07 (issue #1070): handmatig de huidige code naar de NIEUWE Flex Consumption-app
# deployen, zonder de reguliere productiedeploy te raken.
#
# Waarom een aparte workflow en geen workflow_dispatch-input op deploy.yml:
#   deploy.yml doet bij elke run ook de databasemigraties op productie (db-migrate-postgres), de
#   SWA-deploy van de Admin GUI (blazor-deploy) en de productierapportage. Een input die alleen het
#   app-name-veld omzet, zou al die jobs óók laten draaien — dan is een testdeploy naar de Flex-app
#   tegelijk een productierelease van de GUI en een migratieronde. Al die jobs per input uitzetten
#   betekent een `if:` op elke job van deploy.yml, en dan is "het reguliere pad is identiek" alleen
#   nog per regel te beredeneren in plaats van aantoonbaar. Een losse workflow laat deploy.yml
#   byte-voor-byte ongewijzigd (zie docs/RUNBOOK-FLEX-MIGRATIE.md §5.2).
#
# Wat deze workflow bewust NIET doet: migraties, SWA-deploy, iets aan de bestaande app.
# Hij draait uitsluitend vanaf `main` (dezelfde code als productie, waarvan de migraties al zijn
# toegepast) en weigert te deployen tenzij de doel-app aantoonbaar een Flex-app is, Easy Auth aan
# staat, en elke niet-HTTP-trigger (timers + de queue-trigger) op de Flex-app is uitgeschakeld.
#
# Opruimen: bij de cutover (FLEX-09, issue #1072) wordt dit bestand verwijderd; daarna deployt
# deploy.yml regulier naar de Flex-app.
#
# Benodigd (door de eigenaar, niet door een agent): Secret AZURE_FUNCTIONAPP_NAME_FLEX.
# Bewust alleen als Secret, zonder variable-fallback: de naam identificeert de club en een secret
# wordt in de publieke Actions-log gemaskeerd (#1204).
# ──────────────────────────────────────────────────────────────────────────────

on:
  workflow_dispatch:

permissions:
  contents: read

concurrency:
  group: deploy-flex-test
  cancel-in-progress: false

env:
  DOTNET_VERSION: '10.0.x'
  FORCE_JAVASCRIPT_ACTIONS_TO_NODE24: true

jobs:
  preflight:
    name: "Voorcontrole (branch + doel-app ≠ productie-app)"
    runs-on: ubuntu-latest
    steps:
      - name: Alleen vanaf main
        run: |
          if [ "$GITHUB_REF" != "refs/heads/main" ]; then
            echo "::error::Deze workflow draait alleen vanaf main (gestart vanaf $GITHUB_REF)."
            echo "::error::De Flex-app praat met de productiedatabase; alleen main-code heeft zijn migraties daar al toegepast."
            exit 1
          fi

      - name: Doel-app geconfigureerd en niet de productie-app
        env:
          FLEX_NAME: ${{ secrets.AZURE_FUNCTIONAPP_NAME_FLEX }}
          PROD_NAME: ${{ secrets.AZURE_FUNCTIONAPP_NAME || vars.AZURE_FUNCTIONAPP_NAME }}
        run: |
          if [ -z "$FLEX_NAME" ]; then
            echo "::error::Secret AZURE_FUNCTIONAPP_NAME_FLEX ontbreekt. Pas zetten na FLEX-05 (#1068) — zie docs/RUNBOOK-FLEX-MIGRATIE.md."
            exit 1
          fi
          if [ "$(printf '%s' "$FLEX_NAME" | tr '[:upper:]' '[:lower:]')" = "$(printf '%s' "$PROD_NAME" | tr '[:upper:]' '[:lower:]')" ]; then
            echo "::error::AZURE_FUNCTIONAPP_NAME_FLEX is gelijk aan de productie-app. Deze workflow deployt nooit naar productie."
            exit 1
          fi
          echo "Doel-app geconfigureerd en verschilt van de productie-app (namen niet gelogd)."

  build:
    name: "Build (identiek aan deploy.yml → build)"
    runs-on: ubuntu-latest
    needs: [preflight]
    steps:
      - name: Checkout repository
        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1

      - name: Database-tier resolven
        id: tier
        env:
          DatabaseTier: ${{ vars.DatabaseTier }}
          DatabaseTierSwitchConfirmation: ${{ vars.DatabaseTierSwitchConfirmation }}
        run: bash scripts/ci/resolve-database-tier.sh

      - name: .NET SDK installeren
        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Function App bouwen
        run: |
          dotnet publish ${{ steps.tier.outputs.csproj_path }} \
            --configuration Release \
            --output ./output

      # One Deploy (Flex) en WEBSITE_RUN_FROM_PACKAGE (huidige app) pakken dezelfde map in via
      # dezelfde archiveFolder-functie van de action; deze stap maakt de twee eisen expliciet
      # die anders pas op de app zelf blijken: het verborgen .azurefunctions-mapje en de
      # functie-metadata waaruit de host zijn triggers registreert.
      - name: Artefact controleren (.azurefunctions + functions.metadata) en verwachte versie vastleggen
        env:
          CSPROJ: ${{ steps.tier.outputs.csproj_path }}
        run: |
          set -euo pipefail
          test -d ./output/.azurefunctions || { echo "::error::.azurefunctions ontbreekt in ./output"; exit 1; }
          test -s ./output/functions.metadata || { echo "::error::functions.metadata ontbreekt of is leeg"; exit 1; }
          jq -r '.[] | select(any(.bindings[]; (.type | ascii_downcase | endswith("trigger")) and ((.type | ascii_downcase) != "httptrigger"))) | .name' \
            ./output/functions.metadata | LC_ALL=C sort > ./output-niet-http-triggers.txt
          jq -r '.[].name' ./output/functions.metadata | LC_ALL=C sort > ./output-alle-functies.txt
          sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$CSPROJ" | head -n1 > ./output-verwachte-versie.txt
          echo "Functies: $(wc -l < ./output-alle-functies.txt), waarvan niet-HTTP-triggers: $(wc -l < ./output-niet-http-triggers.txt)"
          cat ./output-niet-http-triggers.txt
          echo "Verwachte versie: $(cat ./output-verwachte-versie.txt)"

      - name: Artefact uploaden
        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1
        with:
          name: functionapp-flex
          path: ./output
          include-hidden-files: true  # .azurefunctions folder is hidden en verplicht voor isolated worker

      - name: Controlebestanden uploaden
        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1
        with:
          name: flex-controle
          path: |
            ./output-niet-http-triggers.txt
            ./output-alle-functies.txt
            ./output-verwachte-versie.txt

  deploy-flex:
    name: "Deploy naar Flex-app (One Deploy)"
    runs-on: ubuntu-latest
    needs: [preflight, build]
    steps:
      - name: Controlebestanden downloaden
        uses: actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c # v8.0.1
        with:
          name: flex-controle
          path: ./controle

      - name: Artefact downloaden
        uses: actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c # v8.0.1
        with:
          name: functionapp-flex
          path: ./output

      - name: Azure inloggen
        uses: azure/login@a641126d1b8aa4d1fa005f4f92df94a3a4c4c906 # v3.1.0
        with:
          creds: ${{ secrets.AZURE_CREDENTIALS }}

      # Alles hieronder is read-only (show/list/GET). De resource-ID, resourcegroep en hostname
      # worden gemaskeerd vóór ze in een variabele belanden: ook die identificeren de club.
      - name: "Doel-app controleren: Flex, kostenkritische instellingen, Easy Auth, triggers uit"
        id: azure
        env:
          FLEX_NAME: ${{ secrets.AZURE_FUNCTIONAPP_NAME_FLEX }}
        run: |
          set -euo pipefail
          ID=$(az resource list --resource-type Microsoft.Web/sites --name "$FLEX_NAME" --query "[0].id" -o tsv)
          if [ -z "$ID" ]; then echo "::error::Flex-app niet gevonden met deze credentials."; exit 1; fi
          echo "::add-mask::$ID"
          RG=$(printf '%s' "$ID" | cut -d/ -f5)
          echo "::add-mask::$RG"

          SITE=$(az rest --method get --url "https://management.azure.com${ID}?api-version=2024-04-01" -o json)
          HOST=$(printf '%s' "$SITE" | jq -r '.properties.defaultHostName // empty')
          [ -n "$HOST" ] && echo "::add-mask::$HOST"
          SKU=$(printf '%s' "$SITE" | jq -r '.properties.sku // empty')
          MEM=$(printf '%s' "$SITE" | jq -r '.properties.functionAppConfig.scaleAndConcurrency.instanceMemoryMB // empty')
          MAXI=$(printf '%s' "$SITE" | jq -r '.properties.functionAppConfig.scaleAndConcurrency.maximumInstanceCount // empty')
          AR=$(printf '%s' "$SITE" | jq -r '(.properties.functionAppConfig.scaleAndConcurrency.alwaysReady // []) | length')
          echo "sku=${SKU} instanceMemoryMB=${MEM} maximumInstanceCount=${MAXI} alwaysReady-entries=${AR}"

          FOUT=0
          # Harde guard: alleen een Flex-app. Dit voorkomt ook dat een verkeerd gezet secret
          # alsnog de bestaande Linux Consumption-app (sku Dynamic) raakt.
          [ "$SKU" = "FlexConsumption" ] || { echo "::error::Doel-app is geen Flex Consumption-app (sku='$SKU')."; FOUT=1; }
          # Kostengate (#1063/#1065/#1068): bij always-ready vervalt het gratis tegoed volledig.
          [ "$AR" = "0" ] || { echo "::error::alwaysReady is niet leeg — dan vervalt het gratis tegoed. Eerst op 0 zetten."; FOUT=1; }
          case "$MEM" in 512|2048) ;; *) echo "::error::instanceMemoryMB='$MEM' — alleen 512 of 2048 vallen binnen de onderbouwing van #1065."; FOUT=1 ;; esac
          if [ -z "$MAXI" ] || [ "$MAXI" -gt 5 ]; then
            echo "::error::maximumInstanceCount='${MAXI:-leeg}' — maximaal 5 (#1063: nooit de default van 100)."; FOUT=1
          fi

          # Zonder Easy Auth vertrouwt EasyAuthHelper een meegestuurde X-MS-CLIENT-PRINCIPAL-
          # header: dan geeft één vervalste header admin-toegang tot de productiedatabase.
          # Easy Auth (FLEX-06) staat dus aan VÓÓR er code met productiesecrets op deze app komt.
          AUTH=$(az rest --method get --url "https://management.azure.com${ID}/config/authsettingsV2?api-version=2024-04-01" -o json)
          AUTH_ON=$(printf '%s' "$AUTH" | jq -r '.properties.platform.enabled // false')
          AAD_ON=$(printf '%s' "$AUTH" | jq -r '.properties.identityProviders.azureActiveDirectory.enabled // false')
          echo "easyAuth platform.enabled=${AUTH_ON} azureActiveDirectory.enabled=${AAD_ON}"
          if [ "$AUTH_ON" != "true" ] || [ "$AAD_ON" != "true" ]; then
            echo "::error::Easy Auth staat niet aan op de Flex-app. Eerst FLEX-06 (#1069), dan pas code deployen."; FOUT=1
          fi

          # Timers én de queue-trigger mogen op de Flex-app niet draaien zolang de bestaande app
          # productie is: zelfde database, zelfde storage-queue, dubbele e-mailverwerking en
          # dubbele Sportlink-tokenrefresh. Per functie AzureWebJobs.<naam>.Disabled=true.
          # Alleen de waarde van precies deze setting wordt opgevraagd, nooit de hele lijst.
          SETTINGS=$(az functionapp config appsettings list --resource-group "$RG" --name "$FLEX_NAME" --query "[?starts_with(name, 'AzureWebJobs.') && ends_with(name, '.Disabled')].{n:name,v:value}" -o json)
          while IFS= read -r fn; do
            [ -z "$fn" ] && continue
            V=$(printf '%s' "$SETTINGS" | jq -r --arg n "AzureWebJobs.${fn}.Disabled" '.[] | select(.n == $n) | .v' | tr '[:upper:]' '[:lower:]')
            if [ "$V" != "true" ] && [ "$V" != "1" ]; then
              echo "::error::Niet-HTTP-trigger '$fn' is niet uitgeschakeld op de Flex-app (AzureWebJobs.${fn}.Disabled ontbreekt of is niet true)."
              FOUT=1
            else
              echo "  uitgeschakeld: $fn"
            fi
          done < ./controle/output-niet-http-triggers.txt

          [ "$FOUT" -eq 0 ] || exit 1
          echo "Alle voorcontroles groen."

      # Zelfde action en SHA als deploy.yml. Met RBAC-credentials leest de action zelf de sku van
      # de doel-app en kiest bij 'FlexConsumption' One Deploy (POST /api/publish); 'sku' hoeft
      # dan niet gezet. remote-build blijft op de default false: het artefact is al gebouwd.
      # Geen slot-name: Flex kent geen deployment slots.
      - name: Function App deployen naar Flex-app
        uses: Azure/functions-action@c5060b3b8bb1ebbcb531abd00c822ecbaa8ea656 # v1.5.7
        with:
          app-name: ${{ secrets.AZURE_FUNCTIONAPP_NAME_FLEX }}
          package: ./output

  smoke-flex:
    name: "Smoke test Flex-app (health, versie, auth, functies geregistreerd)"
    runs-on: ubuntu-latest
    needs: [deploy-flex]
    steps:
      - name: Controlebestanden downloaden
        uses: actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c # v8.0.1
        with:
          name: flex-controle
          path: ./controle

      - name: Azure inloggen
        uses: azure/login@a641126d1b8aa4d1fa005f4f92df94a3a4c4c906 # v3.1.0
        with:
          creds: ${{ secrets.AZURE_CREDENTIALS }}

      # De hostname wordt bewust opgehaald in plaats van samengesteld uit de app-naam: een nieuwe
      # app kan een unieke standaardhostname krijgen (<naam>-<hash>.<regio>.azurewebsites.net).
      - name: "Health → 200, database online, instellingen geladen, juiste versie"
        env:
          FLEX_NAME: ${{ secrets.AZURE_FUNCTIONAPP_NAME_FLEX }}
        run: |
          set -uo pipefail
          ID=$(az resource list --resource-type Microsoft.Web/sites --name "$FLEX_NAME" --query "[0].id" -o tsv)
          echo "::add-mask::$ID"
          HOST=$(az rest --method get --url "https://management.azure.com${ID}?api-version=2024-04-01" --query properties.defaultHostName -o tsv)
          echo "::add-mask::$HOST"
          echo "FLEX_HOST=$HOST" >> "$GITHUB_ENV"
          VERWACHT=$(cat ./controle/output-verwachte-versie.txt)
          URL="https://${HOST}/api/health"
          for i in $(seq 1 8); do
            RESPONSE=$(curl -s --max-time 30 -w '\n%{http_code}' "$URL" || true)
            STATUS=$(printf '%s' "$RESPONSE" | tail -n1)
            BODY=$(printf '%s' "$RESPONSE" | sed '$d')
            echo "Poging $i/8 — health: $STATUS"
            if [ "$STATUS" = "200" ]; then
              VERSIE=$(printf '%s' "$BODY" | jq -r '.version // empty')
              DB=$(printf '%s' "$BODY" | jq -r '.database // empty')
              SETTINGS=$(printf '%s' "$BODY" | jq -r '.settingsLoaded // empty')
              PENDING=$(printf '%s' "$BODY" | jq -r '(.pendingMigrations // []) | join(", ")')
              echo "versie=${VERSIE} (verwacht ${VERWACHT}) database=${DB} settingsLoaded=${SETTINGS} pendingMigrations=[${PENDING}]"
              FOUT=0
              [ "$VERSIE" = "$VERWACHT" ] || { echo "::error::Versie op de Flex-app is '$VERSIE', verwacht '$VERWACHT'."; FOUT=1; }
              if [ -n "$DB" ] && [ "$DB" != "online" ]; then echo "::error::database='$DB'"; FOUT=1; fi
              [ "$SETTINGS" != "false" ] || { echo "::error::settingsLoaded=false — app settings op de Flex-app onvolledig?"; FOUT=1; }
              [ -z "$PENDING" ] || { echo "::error::Openstaande migraties: andere database achter de Flex-app dan achter productie?"; FOUT=1; }
              exit $FOUT
            fi
            sleep 20
          done
          echo "::error::Health gaf geen 200 na 8 pogingen (laatste: $STATUS)."
          exit 1

      - name: "Admin-endpoint zonder token → 401"
        run: |
          STATUS=$(curl -s -o /dev/null -w "%{http_code}" --max-time 30 "https://${FLEX_HOST}/api/beheer/settings" || true)
          echo "GET /api/beheer/settings zonder token: $STATUS"
          [ "$STATUS" = "401" ] || { echo "::error::verwacht 401, kreeg $STATUS"; exit 1; }

      - name: "Vervalste X-MS-CLIENT-PRINCIPAL wordt niet vertrouwd → 401"
        run: |
          FAKE_PRINCIPAL=$(echo '{"auth_typ":"aad","claims":[{"typ":"roles","val":"admin"}],"name_typ":"","role_typ":"roles"}' | base64 -w 0)
          STATUS=$(curl -s -o /dev/null -w "%{http_code}" --max-time 30 \
            -H "X-MS-CLIENT-PRINCIPAL: $FAKE_PRINCIPAL" "https://${FLEX_HOST}/api/beheer/settings" || true)
          echo "GET /api/beheer/settings met vervalste principal: $STATUS"
          [ "$STATUS" = "401" ] || { echo "::error::verwacht 401, kreeg $STATUS — Easy Auth op de Flex-app werkt niet. DIRECT de app stoppen."; exit 1; }

      # Een timer die niet geregistreerd wordt faalt stil (#1070). Uitgeschakelde functies staan
      # wel in de lijst, dus dit bewijst registratie zonder dat ze draaien.
      - name: "Alle functies uit het artefact staan geregistreerd op de Flex-app"
        env:
          FLEX_NAME: ${{ secrets.AZURE_FUNCTIONAPP_NAME_FLEX }}
        run: |
          set -uo pipefail
          ID=$(az resource list --resource-type Microsoft.Web/sites --name "$FLEX_NAME" --query "[0].id" -o tsv)
          echo "::add-mask::$ID"
          RG=$(printf '%s' "$ID" | cut -d/ -f5)
          echo "::add-mask::$RG"
          for i in $(seq 1 6); do
            az functionapp function list --resource-group "$RG" --name "$FLEX_NAME" --query "[].name" -o tsv 2>/dev/null \
              | sed 's:.*/::' | LC_ALL=C sort > ./geregistreerd.txt || true
            ONTBREEKT=$(comm -23 ./controle/output-alle-functies.txt ./geregistreerd.txt)
            if [ -z "$ONTBREEKT" ]; then
              echo "Alle $(wc -l < ./controle/output-alle-functies.txt) functies geregistreerd."
              exit 0
            fi
            echo "Poging $i/6 — nog niet geregistreerd: $(printf '%s' "$ONTBREEKT" | wc -l) functie(s); wacht 30s"
            sleep 30
          done
          echo "::error::Niet geregistreerd op de Flex-app:"
          printf '%s\n' "$ONTBREEKT"
          exit 1
```

## Instellingen per instantie (#1515)

Op Flex Consumption draait elke niet-HTTP-trigger op een eigen instantie; de procesbrede
instellingencache is dus per instantie leeg tot hij gevuld wordt. Een worker-middleware
(`Planner.Endpoints/Instellingen/InstellingenLaadGuard.cs`, geregistreerd in beide `Program.cs`)
laadt de instellingen vóór elke functie, eenmaal per proces, met een nieuwe poging bij de volgende
aanroep als de eerste faalde. Een database-uitval laat de functie niet falen (alleen een warning).
