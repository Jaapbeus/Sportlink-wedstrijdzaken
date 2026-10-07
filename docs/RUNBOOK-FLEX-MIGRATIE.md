# Runbook — migratie Linux Consumption → Flex Consumption (epic #1063)

Draaiboek voor de eigenaar: de exacte volgorde van FLEX-05 tot en met FLEX-09, daarna de
`net10.0`-upgrade (issue #1073). Alle commando's zijn **sjablonen**: vul de placeholders lokaal in en
plak de echte waarden nooit terug in een issue, PR of commit (AGENTS.md §4a).

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

> **Huidige stand (2026-10-04) — de migratie is voltooid; dit document is een historisch verslag.**
> Productie draait op de **Flex Consumption-app** met **.NET 10** (`dotnet-isolated 10.0`):
> cutover FLEX-09 op 2026-10-03 (v3.10.0.0), .NET 10 in v3.11.0.0 (#1073, #1074). De oude
> Linux Consumption-app is **gestopt, niet verwijderd** en wordt bewaard tot minimaal 2027-01-03
> (#1076); opruimen (FLEX-13) volgt daarna. De rollback uit §7.4 is niet meer van toepassing (een
> `net10.0`-build draait niet op die oude app). Na de cutover zijn twee Flex-specifieke fouten gevonden en
> opgelost: host-opslag via managed identity (#1512) en instellingen per instantie (#1515, laatste
> sectie). Wie een **nieuwe club** inricht, volgt dit runbook niet: maak de Flex-app direct aan
> ([SETUP-NIEUWE-CLUB.md](../SETUP-NIEUWE-CLUB.md)). De tekst hieronder blijft ongewijzigd als
> werkjournaal van de overstap en is dus niet bijgewerkt naar de huidige tijd.

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
   meldingsformat uit AGENTS.md ("KOSTENWIJZIGING GEDETECTEERD — DEPLOYMENT GESTOPT").

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

Daarna: de verplichte 3-user-test (admin / user / geen rol) per AGENTS.md en
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

Gekozen: **een losse workflow `.github/workflows/deploy-flex-test.yml`** (sjabloon was bijlage A, inmiddels verwijderd),
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
| `deploy` | Ongewijzigd, zelfde `app-name` uit secret `AZURE_FUNCTIONAPP_NAME` (sinds #1237 zonder `vars`-terugval) |
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
2. Eigenaar: `deploy-flex-test.yml` toevoegen (sjabloon inmiddels verwijderd) via een PR naar `develop`, mee naar
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
| 1 | `BlazorAdmin/wwwroot/appsettings.Production.template.json` → `FunctionBaseUrl` | Token `{{AZURE_FUNCTIONAPP_URL}}`, gevuld door `deploy.yml` (job `blazor-deploy`) | Ja — via Secret `AZURE_FUNCTIONAPP_URL` |
| 2 | `BlazorAdmin/wwwroot/staticwebapp.config.json` → CSP `connect-src` | Zelfde token, `sed` in `blazor-deploy` | Ja — zelfde Secret |
| 3 | `BlazorAdmin/wwwroot/appsettings.json` | `http://localhost:7094` — alleen lokaal | Nee |
| 4 | `BlazorAdmin/Program.cs` | Leest `FunctionBaseUrl` uit configuratie; zowel `BaseAddress` als de `authorizedUrls` van de MSAL-handler | Nee (volgt #1) |
| 5 | `.github/workflows/deploy.yml` → `deploy`.`app-name` | `secrets.AZURE_FUNCTIONAPP_NAME` (sinds #1237 zonder `vars`-terugval) | Ja |
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
nodig is *"omdat SWA proxying alles op dezelfde origin houdt"*. Er is geen SWA-proxying (AGENTS.md,
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
- **B — direct bij de cutover.** Geen testpad; de live-check van AGENTS.md regel 2a is dan de eerste
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
   Flex-app (`az functionapp keys list -g "$RG" -n "$APP"` — niet loggen). **Uitsluitend als Secret, nooit als Variable** (#1204): Variables worden niet gemaskeerd in de publieke Actions-logs. Staat een van deze namen nog als Variable, verwijder die dan; sinds #1237 lezen de workflows uitsluitend `secrets.X`.
4. **De tijdelijke workflow verwijderen**: `deploy-flex-test.yml` (nooit toegevoegd; bijlage A is verwijderd) in dezelfde PR als stap 1,
   of direct erna.
5. **Volledige deploy vanaf `main`**, daarna per job (AGENTS.md regel 2, stap C) en de live
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

## Bijlage A — verwijderd

Hier stond het sjabloon voor de tijdelijke workflow `.github/workflows/deploy-flex-test.yml` (FLEX-07).
Dat sjabloon is vervallen: de workflow is nooit als bestand in de repository geplaatst, de testdeploys en
de cutover (FLEX-07 t/m FLEX-09) zijn afgerond, en `deploy.yml` deployt sinds de cutover regulier naar
de Flex-app. De stappen in §5 beschrijven de gevolgde aanpak als historie; het YAML-sjabloon is
met #1525 uit dit document gehaald omdat het niet meer uitvoerbaar of actueel is.

## Instellingen per instantie (#1515)

Op Flex Consumption draait elke niet-HTTP-trigger op een eigen instantie; de procesbrede
instellingencache is dus per instantie leeg tot hij gevuld wordt. Een worker-middleware
(`Planner.Endpoints/Instellingen/InstellingenLaadGuard.cs`, geregistreerd in beide `Program.cs`)
laadt de instellingen vóór elke functie, eenmaal per proces, met een nieuwe poging bij de volgende
aanroep als de eerste faalde. Een database-uitval laat de functie niet falen (alleen een warning).
