# v2 Admin GUI — handleiding

Deze handleiding beschrijft het Admin-portaal (Blazor WebAssembly) en de bijbehorende admin-API.
**Deze installatie draait sinds 2026-09-04 op de Postgres-tier** (`FunctionApp.Postgres/Admin/`).
De SQL Server-tier (`FunctionApp/Admin/`) is daarmee niet minder: het zijn twee gelijkwaardige
tiers, en een fork kiest er één. De admin-API-routes zijn op beide tiers identiek; welke tier jouw installatie gebruikt bepaalt alleen welk project daadwerkelijk
gedeployed is. Het portaal is **live** op (vul jouw clubspecifieke URLs in):

- **Admin GUI:** zie Azure Portal → Static Web App → URL
- **Function App:** `https://func-<clubcode>-sportlink.azurewebsites.net`

De SWA dient uitsluitend statische Blazor-bestanden. De Blazor-app haalt zelf een Bearer token
op via MSAL (Entra ID) en stuurt dat mee naar de Function App. Easy Auth op de Function App
valideert het token server-side.

> **Voor wie is dit document?** **Hoofdstuk 1 t/m 9 zijn voor de technisch beheerder** die de
> installatie opzet: Azure, Entra ID, .NET-runtimes en lokaal ontwikkelen. **Gebruikt u de app
> dagelijks om wedstrijden te plannen en vragen door te sturen? Begin dan bij hoofdstuk 9a** —
> vanaf daar gaat het over de schermen zelf, en is geen technische kennis nodig.

---

## 1. Lokaal ontwikkelen

### Voorbereiding (eenmalig)

1. Start de lokale database (Docker — identiek op Windows en macOS). Kies één tier:
   ```powershell
   docker compose up -d                       # Postgres — de tier die in productie draait
   docker compose --profile sqlserver up -d   # SQL Server (alleen als u aan FunctionApp/ werkt)
   ```
   De service `postgres` in `docker-compose.yml` heeft géén profile en start dus bij een kale
   `docker compose up -d`; `sqlserver` zit juist wél achter het profile `sqlserver`. Een profile
   met de naam `postgres` bestaat niet.
2. Stel `FunctionApp/local.settings.json` (SQL Server) of
   `FunctionApp.Postgres/local.settings.json` (Postgres) correct in (zie de bijbehorende
   `local.settings.template.json`) — zie `docs/DEVELOPER-SETUP.md` §4-5 voor beide paden
3. Voer alle migraties uit op die database — **elke tier heeft een eigen migratiepad**:
   ```powershell
   # Postgres (standaard) — wachtwoord uitsluitend via de omgevingsvariabele:
   $env:POSTGRES_CONNECTION_STRING = "Host=localhost;Port=5432;Username=postgres;Password=…;Database=sportlink"
   .\scripts\dev\Invoke-PostgresMigrations.ps1

   # SQL Server:
   sqlcmd -S localhost,1433 -d SportlinkSqlDb -U sa -C -i Database/Script.PostDeployment1.sql
   ```
   `Invoke-PostgresMigrations.ps1` is een dunne wrapper om `Database.Postgres.Cli`; de
   migratiebestanden staan in `Database.Postgres/migrations/`.
   **`Database/Script.PostDeployment1.sql` raakt geen enkele Postgres-tabel** — wie dat script op
   de Postgres-tier draait houdt een lege database over. Het SQL Server-wachtwoord geeft u mee via
   de omgevingsvariabele `SQLCMDPASSWORD`, zodat het niet in de opdrachtregel en dus niet in de
   processenlijst terechtkomt.
4. Installeer Azurite (voor storage emulator): `npm install -g azurite`

### Services starten

Gebruik `scripts/dev/Start-Debug.ps1` — dit script start Azurite, FunctionApp en BlazorAdmin elk in een
eigen venster in de juiste volgorde:

```powershell
.\scripts\dev\Start-Debug.ps1
# Poorten: Azurite :10000, FunctionApp :7094, BlazorAdmin :5242
```

Wacht ~15 seconden. Zolang de FunctionApp nog opstart verschijnt bovenaan elk scherm een gele
**"Backend start op…"**-banner. Zodra de backend bereikbaar is verdwijnt de banner automatisch.
Bij een 5xx-fout of verbindingsprobleem verschijnt een rode foutbanner met details.

Controleer daarna met:

```powershell
.\scripts\dev\Test-App.ps1          # verificatie: schema + build + endpoints + Blazor-pagina's
.\scripts\dev\Test-App.ps1 -Fix     # herstelt schema-drift automatisch
```

In lokale omgeving is `WEBSITE_SITE_NAME` niet aanwezig, waardoor `EasyAuthHelper` alle
`/api/beheer/*` calls altijd doorlaat — je bent automatisch admin zonder login.

---

## 2. Testmodus — ALLSTARS fictieve wedstrijden

De Admin GUI heeft een ingebouwde testmodus waarmee de dagplanning volledig op fictieve data kan worden getest, zonder de echte Sportlink-wedstrijden te beïnvloeden.

**Activeren:** Kies **AllStars FC** in de club-keuzelijst midden in de bovenbalk. Die keuzelijst
verschijnt zodra er meer dan één club in de installatie staat; de democlub staat er standaard in.  
**Verlaten:** Kies in diezelfde keuzelijst uw eigen club weer.

In testmodus:
- Verschijnt boven in de zijbalk een oranje blok met **TESTMODUS** en daaronder
  **AllStars FC — geen productiedata**, en kleurt de club-keuzelijst in de bovenbalk oranje
- Laadt de dagplanning fictieve wedstrijden in plaats van de echte wedstrijden van uw club
- Verschijnt onderaan de zijbalk het kopje **TESTMODUS** met daaronder het menu-item **Testdata**,
  voor het invoeren van fictieve wedstrijden
- Zijn synchronisatie en e-mailverwerking op de Instellingen-pagina verborgen (niet van toepassing)

Volledige documentatie: [docs/TESTMODUS-ALLSTARS.md](TESTMODUS-ALLSTARS.md)

### Dagplanning — Veldbezetting: hover-highlight en sticky tijdlijn (#1315)

De kaart **"Veldbezetting op [datum]"** bovenaan Dagplanning toont wat er voor die dag al
gepland staat: een tijdlijn per veld, met daaronder een tabel met dezelfde wedstrijden.

- **Hover-highlight:** beweeg de muis over een rij in de tabel, of over een blok in de
  tijdlijn — de bijbehorende wedstrijd licht in beide oranje op. Zo is snel terug te vinden
  waar een wedstrijd uit de lijst zich visueel op het veld bevindt, en andersom.
- **Sticky tijdlijn:** de tijdlijn blijft zichtbaar bovenin beeld terwijl u door de
  wedstrijdentabel eronder scrollt — handig bij een dag met veel wedstrijden. Op een smal
  scherm (mobiel, breedte < 641px) is dit uitgeschakeld, omdat daar ook de bovenbalk zelf niet
  sticky is.

Dit geldt alleen voor deze kaart, niet voor de tijdlijnen in de tabbladen **Optimaal**/**Huidig**
verderop op dezelfde pagina.

### Dagplanning — twee tabs: Optimaal en Huidig

Na een klik op **Optimaliseer** staat bovenaan de samenvattingsbalk (wedstrijden, zonder veld,
zonder tijd, te wijzigen, optimale eindtijd). Daaronder staan twee tabs (#689):

| Tab | Wat je ziet |
|---|---|
| **Optimaal** | De planning zoals de planner die voorstelt. Wedstrijden zijn hier te **verslepen** naar een andere tijd of een ander veld. |
| **Huidig** | De stand zoals die nu in Sportlink staat. Niet te verslepen. |

Elke tab heeft dezelfde opbouw: eerst de **tijdlijn per veld**, daaronder de **wedstrijdenlijst** van
diezelfde stand. Omdat beide tabs op exact dezelfde hoogte beginnen, werkt wisselen als het
vergelijken van twee foto's: je oog springt niet en je ziet direct wat er in de veldbezetting
verandert. Eerder stonden hier twee verschillende vergelijkingen door elkaar — een tabel met
"huidig" en "optimaal" in kolommen naast elkaar, én tabs die alléén de tijdlijn wisselden terwijl de
tabel bleef staan.

De **filterknoppen** (Alles / Wijzigingen / Probleem) gelden voor beide tabs. Filter je op
"Wijzigingen", dan zie je in beide standen dezelfde selectie — dat maakt de vergelijking pas echt
bruikbaar.

De **wedstrijdenlijst** staat altijd gesorteerd op aanvangstijd, vroeg naar laat (#1331). Staan er
meerdere wedstrijden op dezelfde tijd, dan bepaalt de veldvolgorde die voor uw club is ingesteld
(Instellingen → Velden) de volgorde binnen die tijd — dit werkt hetzelfde ongeacht of uw velden
namen als "Veld 1"/"Veld 2" of "A"/"B"/"C" hebben. Een wedstrijd zonder (geldige) tijd of veld
staat altijd onderaan.

De kolom **Wijziging** staat in beide tabs. In de tab Huidig is die juist het nuttigst: daar zie je
welke wedstrijd gaat verschuiven, en met één klik op Optimaal zie je waarheen.

De kolom **Voorkeurstijd** toont in de tab Optimaal de gewenste tijd mét de afwijking; in de tab
Huidig alleen de gewenste tijd. Die afwijking is namelijk berekend op de optimale planning — hem bij
de huidige stand tonen zou een getal beweren dat daar niet op is berekend.

### Dagplanning — status-badges

De dagplanning heeft **twee losse kolommen** die makkelijk verward worden (#666):

**Kolom "Wijziging"** — verplaatst de planner deze wedstrijd t.o.v. wat er nu in Sportlink staat?

| Badge | Betekenis |
|---|---|
| Ongewijzigd (grijs) | De planner laat deze wedstrijd staan |
| Nieuw (geel) | Nieuw timeslot toegewezen (had nog geen veld of tijd) |
| Wijzig (blauw) | Bestaand slot wordt verplaatst |
| Probleem (rood) | Geen slot mogelijk (velden vol) |
| Onbekend (grijs) | Team heeft geen speeltijdsconfiguratie (bijv. veldboeking door 'Toernooi commissie') — wordt ongewijzigd getoond, optimizer slaat het over |

**Kolom "Voorkeurstijd"** — staat de wedstrijd op de gewenste tijd?

| Badge | Betekenis |
|---|---|
| Tijd, groen | Exact op de voorkeurstijd |
| Tijd + afwijking, geel | Tot en met 15 minuten ernaast |
| Tijd + afwijking, rood | Meer dan 15 minuten ernaast |
| — | Geen voorkeurstijd voor dit team en geen standaardtijd voor de leeftijdscategorie |
| ander veld (grijs, achter de tijd) | Het team heeft een voorkeursveld, maar dat was bezet — de planner koos een ander veld |

Achter de tijd staat de herkomst: **regel** (teamregel voorkeursveld met tijd), **team** (eigen
voorkeurstijd) of **standaard** (standaardtijd van de leeftijdscategorie).

> **Waarom twee kolommen?** Tot #666 was er één groene "OK"-badge die alleen keek of de planner iets
> verplaatste. Een wedstrijd die bleef staan toonde dus "OK", ook als die 60 minuten van de gewenste
> tijd af lag. Die twee vragen zijn nu gescheiden.

### Zelf schuiven in de tijdlijn

De berekende planning is met de muis aan te passen (#666). Sleep een wedstrijdblok in de tijdlijn:

- **naar links of rechts** voor een andere tijd — de tijd springt op stappen van 5 minuten;
- **naar een andere rij** voor een ander veld.

De tabel, de eindtijd en het aantal te wijzigen wedstrijden lopen direct mee. Wedstrijden die je zelf
hebt verplaatst krijgen een stippellijn, zodat je onderscheid ziet met wat de planner koos.

Ontstaat er een onmogelijke planning — twee wedstrijden die niet samen op één veld passen, of te weinig
ruimte ertussen — dan verschijnt boven de tijdlijn een waarschuwing die benoemt welke twee wedstrijden
het betreft. De wijziging wordt niet geblokkeerd; je ziet alleen dat het zo niet kan.

Alleen de tab **Optimaal** is te bewerken. De tab **Huidig** toont de stand uit Sportlink en
blijft ongewijzigd.

Teams met een grijze "Onbekend"-badge blokkeren wel hun tijdslot voor andere teams; ze worden niet als fout beschouwd.

---

## 3. Way of working

### Branches

- `main` — productie; alleen via PR, nooit direct pushen
- `develop` — integratiebranch; geen deploy, voor lokaal combineren en testen
- `feature/#<nr>-<slug>` — losse branches voor features en bugfixes (basis: `develop`)
- `hotfix/#<nr>-<slug>` — urgente productiefixes (basis: `main`)

### Feature-workflow

1. Branch aanmaken vanaf `develop`: `git checkout -b feature/#<nr>-<slug> develop`
2. Implementeren, bouwen, verifiëren (`.\scripts\dev\Test-App.ps1`)
3. Commit + push + PR naar `develop`
4. CI security gate groen → PR mergen

### Hotfix-workflow

1. Branch aanmaken vanaf `main`: `git checkout -b hotfix/#<nr>-<slug> main`
2. Fix + PR naar `main`
3. Na merge ook PR `main` → `develop` aanmaken zodat `develop` gesynchroniseerd blijft
4. CI op `main` controleert de deploy (`.github/workflows/deploy.yml`)

---

## 3a. Azure resources aanmaken (eenmalig — reeds gedaan)

De resources zijn aangemaakt en actief. Deze sectie is documentatie voor toekomstige herinrichting.

### Static Web App aanmaken

```bash
az staticwebapp create --name swa-<clubcode>-sportlink --resource-group rg-<clubcode>-sportlink --location westeurope --sku Free
```

### Deployment token ophalen en opslaan als GitHub Secret

```bash
az staticwebapp secrets list --name swa-<clubcode>-sportlink --resource-group rg-<clubcode>-sportlink --query "properties.apiKey" -o tsv
```

Sla de waarde op als GitHub Secret `AZURE_STATIC_WEB_APPS_API_TOKEN`. De `blazor-deploy` job
in `.github/workflows/deploy.yml` gebruikt dit token bij elke push naar `main`.

> **Geen SWA-Function koppeling:** de Function App is **niet** gelinkt aan de SWA.
> De SWA dient alleen statische Blazor-bestanden. API-calls gaan rechtstreeks van Blazor
> naar de Function App via Bearer tokens — geen SWA-proxying, geen `az staticwebapp backends link`.

---

## 4. Entra ID app-registratie

### App Registration aanmaken

1. Azure Portal → **Microsoft Entra ID** → **App registrations** → **New registration**
2. Naam: `Sportlink Admin GUI`
3. Supported account types: **Single tenant**
4. Redirect URI: **Single-page application (SPA)** → `https://<swa-host>/authentication/login-callback`
5. **Register**

### API scope aanmaken

1. **Expose an API** → **Add a scope**
2. Application ID URI: accepteer de default (`api://<client-id>`)
3. Scope name: `Admin.Access`
4. Wie kan toestemming geven: **Admins and users**
5. **Add scope**

> Deze scope wordt gebruikt door Blazor als `DefaultAccessTokenScopes` in `Program.cs`.

### App rollen aanmaken

1. **App roles** → **Create app role**
2. Display name: `Admin`, Value: `admin`, Allowed member types: **Users/Groups** → **Apply**
3. Optioneel: herhaal voor `user` (lees-alleen, toekomstige gebruik)

### Easy Auth configureren op de Function App

Easy Auth valideert het Bearer token server-side vóórdat het de functies bereikt.

1. Azure Portal → **Function App** (`func-<clubcode>-sportlink`) → **Authentication**
2. **Add identity provider** → **Microsoft**
3. App Registration: **Pick an existing app** → `Sportlink Admin GUI`
4. Unauthenticated requests: **HTTP 401 Unauthorized**
5. **Add**

Controleer na het instellen dat `WEBSITE_AUTH_ENABLED = True` in de Application Settings staat.

### Assignment required

1. Azure Portal → **Enterprise applications** → `Sportlink Admin GUI`
2. **Properties** → **Assignment required** → **Yes**

Zonder deze instelling kan elke tenant-gebruiker een token ophalen — ook zonder toegewezen rol.

---

## 5. Roltoewijzing

1. Azure Portal → **Enterprise applications** → `Sportlink Admin GUI`
2. **Users and groups** → **Add user/group**
3. Selecteer de gebruiker → **Select a role** → **Admin** → **Assign**

Alleen gebruikers met de `admin`-rol krijgen toegang tot de `/api/beheer/*` endpoints.
Gebruikers zonder rol zien de `NoAccess`-pagina in Blazor (frontend-gate, App.razor) én
krijgen 403 van de Function App (backend-gate, EasyAuthHelper).

### Verplichte 3-user-test bij elke auth-wijziging

| Gebruiker | Configuratie | Verwacht resultaat |
|---|---|---|
| Admin-user | Rol `admin` toegewezen | Volledige UI, alle API-calls slagen |
| Tweede user | Rol `user` toegewezen | UI laadt, read-API werkt |
| Derde user | Geen rol | `NoAccess`-pagina, geen sidebar/navigatie |
| Externe user | Andere tenant of guest | Kan niet inloggen (Entra weigert) |

---

## 6. Auth-architectuur in productie

### Hoe het werkt

```
Browser (Blazor WASM)
  │
  ├─ App.razor: GetAuthenticationStateAsync() als eerste actie
  │    Niet ingelogd? → NavigateToLogin → Microsoft login-pagina
  │    Ingelogd maar geen rol? → NoAccess-pagina
  │    Admin? → MainLayout + volledige UI
  │
  ├─ MSAL haalt Bearer token op bij Entra ID
  │    Token bevat 'roles' claim met 'admin'
  │    CustomUserFactory pakt JSON-array uit naar losse claims
  │
  └─ AdminApiClient stuurt Bearer token mee via AuthorizationMessageHandler
       │
       ▼
  Azure Function App (func-<clubcode>-sportlink)
    Easy Auth valideert het token (X-MS-CLIENT-PRINCIPAL header)
    EasyAuthHelper.RequireAdmin() checkt 'admin' rol op alle /api/beheer/* endpoints
```

### Configuratie (`BlazorAdmin/wwwroot/appsettings.Production.json`)

```json
{
  "FunctionBaseUrl": "https://func-<clubcode>-sportlink.azurewebsites.net",
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/<tenant-id>",
    "ClientId": "<client-id>",
    "ValidateAuthority": true
  },
  "PostLogoutRedirectUrl": "https://www.<clubdomein>.nl/"
}
```

Dit bestand wordt **automatisch aangemaakt door CI** (`deploy.yml`) vanuit
`appsettings.Production.template.json` + GitHub Variables. **Nooit handmatig committen —
het staat in `.gitignore` en mag niet in de repository.** Zie `CLAUDE.md` tabel
"Wat bevatten de bestanden in git?".

### Verificatie na elke auth-wijziging

1. Open de site in een verse Incognito/InPrivate sessie (geen oude MSAL-token in localStorage)
2. Microsoft login-pagina verschijnt binnen 2-3 seconden — anders is MSAL niet correct geconfigureerd
3. Vóór inloggen: geen sidebar, geen navigatie, geen FEEDBACK-knop zichtbaar
4. Na inloggen met admin-rol: volledige UI laadt, alle API-calls slagen
5. F12 → Network: controleer dat MSAL redirect naar `login.microsoftonline.com` gaat

---

## 7. Lokaal testen met Azure SWA CLI (optioneel)

De SWA CLI emuleert de statische hosting lokaal en dwingt `staticwebapp.config.json` routeregels af.
Omdat de auth in v2 via MSAL in Blazor verloopt (niet via SWA-routeregels), is de SWA CLI
voornamelijk nuttig voor het testen van de navigatiefallback en cache-headers.

### Vereisten (eenmalig)

```powershell
npm install -g @azure/static-web-apps-cli
swa --version
```

### Opstarten

Start de backends met `scripts/dev/Start-Debug.ps1`, daarna in een vierde terminal:

```powershell
# In de repo root (waar swa-cli.config.json staat)
swa start sportlink-admin
# SWA emulator draait op http://localhost:4280
```

De SWA CLI proxied de Blazor dev server (`http://localhost:5242`). Er is geen `/api/*` proxy
meer naar de Function App — API-calls gaan rechtstreeks van Blazor naar poort 7094.

In de lokale Blazor dev-omgeving (`ASPNETCORE_ENVIRONMENT=Development`) is altijd
`AlwaysAuthenticatedStateProvider` actief — je bent automatisch admin zonder MSAL.
De SWA CLI voegt hieraan geen extra auth-laag toe.

Voor een volledige productie-emulatie (met échte MSAL-flow):

```powershell
dotnet publish BlazorAdmin/BlazorAdmin.csproj -c Release -o ./blazor-publish
swa start ./blazor-publish/wwwroot --api-devserver-url http://localhost:7094
```

---

## 8. Snel testen (endpoints direct)

```powershell
# Lokaal: alle endpoints zonder authenticatie (Easy Auth niet actief lokaal)
Invoke-RestMethod http://localhost:7094/api/health
Invoke-RestMethod http://localhost:7094/api/beheer/settings
Invoke-RestMethod http://localhost:7094/api/beheer/sync/status
Invoke-RestMethod http://localhost:7094/api/beheer/templates
Invoke-RestMethod http://localhost:7094/api/beheer/voorkeurstijden
Invoke-RestMethod http://localhost:7094/api/beheer/email-log
```

Of gebruik het geautomatiseerde verificatie-script:

```powershell
.\scripts\dev\Test-App.ps1              # schema + build + endpoints + Blazor-pagina's
.\scripts\dev\Test-App.ps1 -Fix         # herstelt schema-drift automatisch
.\scripts\dev\Test-App.ps1 -Verbose     # volledige output per check
```

Zie [docs/VERIFICATIE-SCRIPTS.md](../docs/VERIFICATIE-SCRIPTS.md) voor een volledig overzicht van
wat `scripts/dev/Test-App.ps1` controleert.

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
`TargetFramework` daarmee overeenkomt. Huidig: `net9.0`.

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
in een transitive package.

**Oorzaak:** Een dependency sleept een kwetsbare subpackage mee. De vulnerability warning
blokkeert later de Security Gate in CI.

**Oplossing:** Controleer met `dotnet build 2>&1 | Select-String "NU19"` en upgrade de
betreffende package naar een versie zonder kwetsbare transitive dependencies.

**Controle:** 0 NU1903/NU1904 warnings in build output.

---

### Valkuil 4: CORS poort-mismatch

**Symptoom:** Blazor laadt, maar alle API-calls falen met CORS-error in de browser console.

**Oorzaak:** `BlazorAdmin/Properties/launchSettings.json` wijst naar poort 5242 (Blazor default),
maar de CORS-whitelist in `FunctionApp/Program.cs` bevat een andere poort.

**Oplossing:** De CORS origins in `Program.cs` moeten de werkelijke Blazor dev-poort bevatten:
`http://localhost:5242` en `https://localhost:7242`.

**Controle:** `BlazorAdmin/Properties/launchSettings.json` → `applicationUrl` → controleer of alle
vermelde poorten in de CORS-origins staan.

---

### Valkuil 5: Blazor WASM rolt roles JSON-array naar string

**Symptoom:** Gebruiker heeft de `admin`-rol in Entra ID maar `IsInRole("admin")` geeft `false`.
De gebruiker ziet de `NoAccess`-pagina ondanks correcte roltoewijzing.

**Oorzaak:** Blazor WASM cast een `"roles": ["admin"]` JSON-array uit het ID-token naar één claim
met de JSON-string als waarde (`'["admin"]'`), waardoor `IsInRole("admin")` faalt.

**Oplossing:** `CustomUserFactory` in `BlazorAdmin/Services/CustomUserFactory.cs` pakt de array
uit naar losse claims. Geregistreerd via `.AddAccountClaimsPrincipalFactory<CustomUserFactory>()`
in `Program.cs`.

**Controle:** Na inloggen met admin-rol: geen `NoAccess`-pagina, sidebar en navigatie zichtbaar.

---

### Verificatiescript

`scripts/dev/Test-App.ps1` automatiseert de meeste controles:

```powershell
# Start services (Azurite + FunctionApp + BlazorAdmin)
.\scripts\dev\Start-Debug.ps1

# Wacht ~15 seconden, dan volledige verificatie:
.\scripts\dev\Test-App.ps1

# Met schema-drift herstel:
.\scripts\dev\Test-App.ps1 -Fix
```

Het script doorloopt:
1. Database-verbinding en schema-validatie (tabelstructuur + kolommen)
2. `dotnet build FunctionApp` — bouwt met warnings-als-fouten check
3. API smoke tests: health, beheer/settings, beheer/sync/status, beheer/templates,
   beheer/voorkeurstijden, beheer/velden, beheer/email-log
4. Feedback widget (GitHub-integratie)
5. Blazor-pagina checks: alle gewijzigde routes

Exitcode 0 = alles groen. Exitcode 1 = minimaal één check gefaald.

**Wanneer uitvoeren:** Altijd vóór een commit of oplevering. `dotnet build` slaagt ≠ werkt.

---

## 9a. Het startscherm en de bovenbalk

> **Vanaf hier gaat het over de schermen zelf.** De hoofdstukken hierboven zijn voor de technisch
> beheerder die de installatie opzet; u heeft ze niet nodig om met de app te werken.

### Het dashboard

Na inloggen komt u op het **Dashboard**. Dat is een startpagina met vier snelkoppelingen naar de
schermen die u het vaakst nodig heeft:

| Tegel | Waarvoor |
|---|---|
| **Teambegeleiding** | Contactgegevens van teambegeleiders bekijken en een vraag doorsturen |
| **Dagplanning** | Wedstrijden inplannen en de veldindeling voor een speeldag beheren |
| **Leermomenten** | Correcties op de AI-classificatie beoordelen (zie hoofdstuk 16a) |
| **Email-tester** | De e-mailverwerking uitproberen zonder iets te versturen (zie hoofdstuk 20) |

Klik op een tegel om er direct naartoe te gaan. Alles is ook bereikbaar via de zijbalk links.
Heeft u bij **Thema** een clublogo ingesteld, dan staat dat bovenaan het dashboard.

### De bovenbalk — waaronder het wisselen van club

Boven elk scherm staat een smalle balk met, van links naar rechts:

- **De club-keuzelijst** (midden). Deze verschijnt alleen als er meer dan één club in de
  installatie staat — in de praktijk uw eigen club plus de democlub **AllStars FC**. Kiest u hier
  een andere club, dan tonen álle schermen voortaan de gegevens van die club. Dit is tegelijk de
  schakelaar voor de testmodus: kiest u AllStars FC, dan werkt u in demodata (zie hoofdstuk 2 en
  hoofdstuk 13). Uw keuze wordt door de browser onthouden, ook na afsluiten — kiest u later uw
  eigen club weer, dan bent u terug in de echte gegevens.
- **Backend start op…** verschijnt op deze plek in plaats van de keuzelijst zolang de server nog
  aan het opstarten is. Dit duurt bij een koude start tot ongeveer een halve minuut en verdwijnt
  vanzelf.
- **Het versienummer** van de applicatie (rechts).
- **De zon/maan-knop** voor lichte of donkere weergave — zie hoofdstuk 21.
- **De FEEDBACK-knop** om een melding te doen — zie hoofdstuk 23.
- **About**, een link naar de broncode van het project.

### De zijbalk

De zijbalk links bevat in deze volgorde: **Dashboard**, **Teambegeleiding**, **Dagplanning**,
**Leermomenten**, **Teamaliassen**, **Email-tester**, dan (alleen onder een voorwaarde, zie
hieronder) **Wijzigingsverzoeken** en **Wedstrijden**, en tot slot het uitklapbare menu
**Instellingen** met daarin *Instellingen*, *Speeltijden*, *Velden*, *Voorkeurstijden*,
*E-mailtemplates*, *Thema* en *Sportlink Ext.* (het menu-item; de functie zelf heet Sportlink Web
Extension, zie §19).

Drie menu-items verschijnen alleen onder een voorwaarde:

- **Wijzigingsverzoeken** en **Wedstrijden** (menu-item voor het scherm "Oefenwedstrijd aanmaken",
  zie §18a) staan er alleen als de Sportlink Web Extension is ingeschakeld (hoofdstuk 19).
- Onder het menu Instellingen komt nog het kopje **TESTMODUS** met daaronder **Testdata**; dat
  staat er alleen als AllStars FC in de club-keuzelijst is gekozen.

Onderaan de zijbalk staat wie er is ingelogd, met de knop **Uitloggen**.

---

## 10. Teambegeleiding-pagina (`/teambegeleiding`)

De pagina `/teambegeleiding` stelt beheerders én gebruikers met de **user-rol** in staat team-contactgegevens op te zoeken en vragen door te sturen aan de begeleiding.

### Functionaliteit

1. **Team selecteren** — keuzelijst met alle teams waarvan begeleiders bekend zijn
2. **Begeleiders inzien** — kaarten per begeleider met naam, teamrol, e-mailadres en telefoonnummer.
   U moet ingelogd zijn om deze pagina te kunnen openen, dus deze contactgegevens zijn alleen
   zichtbaar voor mensen die toegang hebben tot dit beheerscherm. Zichtbaarheid is daarmee geen
   aparte afweging per veld, maar een gevolg van wie er mag inloggen.
3. **"Email Aan"-veld** — bewerkbaar tekstveld, standaard gevuld met alle begeleiders van het team in
   `"Naam" <adres>; ...`-notatie. Dit veld bepaalt **daadwerkelijk** wie de mail bij "Vraag doorsturen"
   ontvangt (#765) — er is dus geen verschil meer tussen wat u ziet en wat er verstuurd wordt.
   - **Herstel** — zet het veld terug naar de volledige begeleiderslijst
   - **Kopieer** — kopieert de huidige (eventueel bewerkte) inhoud naar het klembord, voor gebruik in
     een los Outlook-bericht
   - Verwijder een begeleider uit het veld om diegene over te slaan, of voeg een eigen adres toe
   - Format per ontvanger: `"Naam" <trainer@voorbeeld.nl>` of een kaal adres, gescheiden door `;` of `,`
   - Maximaal 15 ontvangers per verzending; een ongeldig adres wordt geweigerd met een melding die
     precies aangeeft welk adres niet klopt
   - Een geel waarschuwingsbalkje verschijnt (niet-blokkerend) bij een adres dat niet in de
     begeleiderslijst van dit team voorkomt — controleer dit voordat u verstuurt
4. **Vraag doorsturen** — klik "Stel een vraag" → de kaart toont "Wordt verstuurd naar: …" met exact de
   inhoud van het "Email Aan"-veld → vul Onderwerp (optioneel) en Bericht in → "Versturen"
   - To: de ontvangers uit het "Email Aan"-veld (leeg → server-side fallback: hoogst-geprioriteerde
     begeleider, Trainer > Coach > Teamleider, met de coördinator als laatste terugval)
   - Reply-To: e-mailadres van de aanvrager (automatisch uit Entra ID)
   - BCC: de coördinator — het adres dat bij Instellingen is ingevuld onder *Planner e-mailadres*
   - Ontvangers antwoorden rechtstreeks naar de aanvrager
   - **Zelf testen**: vul uw eigen e-mailadres in bij "Email Aan" (in plaats van of naast de
     begeleiders) en verstuur een testvraag — u ontvangt de mail dan zelf en kunt controleren of
     "Beantwoorden" in Outlook naar u terugkomt. Klik daarna "Herstel" om het veld weer op de echte
     begeleiders te zetten
   - Elke verzending — automatisch opgezocht of zelf opgegeven — wordt vastgelegd in het
     e-mailoverzicht van de app, hetzelfde overzicht als de automatische e-mailafhandeling, en na
     30 dagen automatisch geanonimiseerd. De teller "e-mailverwerking" op de Instellingen-pagina
     telt deze verzendingen op dit moment gewoon mee — er is (nog) geen aparte detailweergave per
     bericht

> **Menupositie:** Teambegeleiding staat bewust direct onder Dashboard in de zijbalk en als eerste tegel
> op het dashboard — het is het meest gebruikte scherm, omdat contactgegevens hier sneller te vinden
> zijn dan in Sportlink Club zelf (#669). De CSV-import staat sinds #1322 niet meer op dit scherm,
> zie 10a hieronder.

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/teambegeleiding` | Alle teams met begeleiding |
| `GET /api/beheer/teambegeleiding/{team}` | Begeleiders van team (naam, rol, e-mailadres, telefoonnummer) |
| `POST /api/beheer/teambegeleiding/doorsturen` | Doorsturen van vraag; `ontvangers` bepaalt de ontvangers (leeg → server-side coach-lookup) |

Auth: `RequireAdmin()` — alleen toegankelijk voor de admin-rol. Namen, e-mailadressen en
telefoonnummers zijn persoonsgegevens; sinds #310 (mei 2026) is dit voor alle vier
Teambegeleiding-endpoints admin-only, ook al toonde deze sectie eerder ten onrechte
`RequireAuthenticated()`.

---

## 10a. Teambegeleiding importeren (`/instellingen/teambegeleiding-import`)

Losgekoppeld van de team selectie/weergave-pagina bij #1322: CSV-import is een incidentele
beheerdersactie (vervangt de teambegeleiding van de club volledig), geen dagelijks scherm — daarom
staat deze pagina onder Instellingen in plaats van in het hoofdmenu. Bereikbaar via
**Instellingen → Teambegeleiding importeren** in het submenu, of via de kaart op de
Instellingen-pagina zelf.

- CSV-export uit Sportlink inlezen; het scherm bevat de exportstappen en een voorbeeldweergave vóór
  bevestiging.
- **Wat er met de gegevens gebeurt.** Uw browser leest het bestand in en toont een voorbeeld van
  de eerste vijf rijen, zodat u kunt controleren of u het juiste bestand heeft. Klikt u daarna op
  importeren, dan wordt **de volledige inhoud van de CSV naar de server gestuurd** (beveiligd,
  alleen voor uw ingelogde sessie) en daar meteen in de database verwerkt. De persoonsgegevens
  verlaten dus wél uw browser — dat is inherent aan een import. Het bestand zelf wordt nergens
  op de server bewaard en de inhoud komt niet in de logbestanden; wat blijft staan zijn de
  begeleidersgegevens in de database, plus één regel in het importlogboek met wie wanneer welk
  bestand heeft geïmporteerd en hoeveel rijen erin zaten.
- **Een import vervangt de bestaande teambegeleiding van de club volledig.** Het vervangen
  gebeurt in één keer: óf de volledige nieuwe lijst komt erin, óf er verandert niets. Een fout
  halverwege — bijvoorbeeld een te lange teamnaam — laat de vorige lijst dus ongemoeid, en twee
  mensen die tegelijk importeren kunnen geen half-samengevoegde lijst veroorzaken. Er wordt niets
  samengevoegd, dus een onvolledige export herstelt u door een complete export opnieuw te
  importeren.
- Volledige exportinstructie voor de beheerder: [ADMIN-TEAMBEGELEIDING-IMPORT.md](ADMIN-TEAMBEGELEIDING-IMPORT.md)

### API-endpoint

| Endpoint | Beschrijving |
|---|---|
| `POST /api/beheer/teambegeleiding/import` | CSV-import; vervangt alle rijen van de club |

Auth: `RequireAdmin()`.

---

## 11. Speeltijden-pagina (`/instellingen/speeltijden`)

De pagina `/instellingen/speeltijden` (alleen admin-rol) beheert de speeltijden per
leeftijdscategorie. De planner rekent uitsluitend met het getal in de kolom **Totaal (min)** op
deze pagina. Wat Sportlink zelf als wedstrijdduur doorgeeft, wordt genegeerd — u bepaalt het hier.

Het veld **Totaal (min)** — in het scherm met de toevoeging *incl. rust* — is de totale
veldblokkeertijd die de planner direct gebruikt. Rust wordt er dus **niet** nog eens apart bij
opgeteld: Totaal = speeltijd + rust + buffer. Voorbeeld senioren: 2×45 + 15 rust + 10 buffer = 115.

### Categorieregels
- Categorie `1-99` = Senioren mannen; `VR` = Senioren vrouwen → beide 115 minuten
- MO-categorieën hebben dezelfde WedstrijdTotaal als de equivalente JO-categorie
- Ontbrekende categorie → foutmelding met verwijzing naar deze pagina

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/speeltijden` | Alle speeltijden voor de club |
| `POST /api/beheer/speeltijden` | Nieuwe speeltijd toevoegen |
| `PUT /api/beheer/speeltijden/{leeftijd}` | Speeltijd bijwerken |
| `DELETE /api/beheer/speeltijden/{leeftijd}` | Speeltijd verwijderen |

---

## 12. Real-time Sportlink API voor veldbeschikbaarheid (`/instellingen`)

### Wat doet deze instelling?

Op de `/instellingen`-pagina staat de schakelaar **"Real-time Sportlink API raadplegen"** (standaard: aan).

| Stand | Gedrag |
|---|---|
| **Aan** | De planner haalt bij elke beschikbaarheidscheck live wedstrijdgegevens op bij de Sportlink `/programma`-API. Dit geeft de meest actuele veldocupatie, ook als de nachtelijke sync nog niet is gelopen. |
| **Uit** | Alleen de lokale database wordt geraadpleegd. Sneller en werkt zonder internet- of API-verbinding, maar kan achterliggen als de dagelijkse sync recent nog niet is uitgevoerd. |

### Automatische fallback

Bij een API-fout (time-out, netwerk, service onbeschikbaar) schakelt de planner automatisch terug naar de database. Beheerders hoeven hier niets voor te doen — de fallback is transparant.

### Wanneer uitschakelen?

- Testomgeving zonder geldige Sportlink API-credentials
- Lokale ontwikkelomgeving zonder internet
- Problematische API-respons tijdelijk omzeilen tijdens een incident

---

## 13. Test data modus (ALLSTARS) — `/testdata/wedstrijden`

De **Testmodus** maakt het mogelijk fictieve wedstrijden aan te maken die worden gebruikt voor lokale tests van de dagplanning en optimalisatie, zonder productiewedstrijden te raken.

### Activeren

Kies **AllStars FC** in de club-keuzelijst midden in de bovenbalk. De testmodus is dan actief:
- Alle opvragingen bij de server gebruiken voortaan de democlub in plaats van uw eigen club
- Boven in de zijbalk verschijnt een oranje blok **TESTMODUS — AllStars FC — geen productiedata**
- Onderaan de zijbalk verschijnt het kopje **TESTMODUS** met daaronder het menu-item **Testdata**
- De club-keuzelijst zelf kleurt oranje

### Deactiveren

Kies in diezelfde keuzelijst uw eigen club weer.

### Testdata — wedstrijden invoeren

De pagina `/testdata/wedstrijden` toont een invoergrid voor het aanmaken van fictieve wedstrijden:

| Kolom | Beschrijving |
|---|---|
| Datum | Datum van de wedstrijd (↓ fill-down beschikbaar) |
| Team (thuis) | Selecteer een echt clubteam uit de dropdown |
| Tegenstander | Vrij tekstveld voor de naam van de tegenstander |
| Starttijd | Aanvangstijd (↓ fill-down beschikbaar) |
| Veld | Veldnaam selecteren uit de dropdown |
| Velddeel | Deelveld-keuzelijst — verschijnt alleen als het team op een deel van een veld speelt. Welke delen u kunt kiezen volgt uit **Veldafmeting** bij Instellingen → Speeltijden voor de leeftijdscategorie van het team: een half veld geeft A/B, een derde veld A/B/C en een kwart veld A1/A2/B1/B2. Staat er een heel veld (1,00), dan blijft deze kolom leeg. |
| Soort | Competitie / Oefenwedstrijd / Toernooi / Vriendschappelijk |

**Globale invoerbalk** (boven de tabel): stel **Datum**, **Soort**, **Tegenstander**, **Starttijd**
en **Veld** in vóór het toevoegen van rijen — deze vijf waarden worden als startwaarde voor nieuwe
rijen gebruikt en besparen het meeste typewerk.

**Knoppen naast de invoerbalk:**
- **Alle teams** — voegt één rij per huidig clubteam toe en slaat alles op
- **Lege rij** (met plus-pictogram) — voegt één lege rij toe
- **↓** in een kolomkop — kopieert de eerste ingevulde waarde naar alle lege cellen in die kolom

**Verplaats datum** (een eigen blok onder de invoerbalk): vul bij **Van** de datum in waarop nu
testwedstrijden staan en bij **Naar** de datum waar ze naartoe moeten, en klik op **Verplaats**.
Alle testwedstrijden van die ene dag schuiven in één keer mee. Handig om een eerder opgezette
speeldag opnieuw te gebruiken zonder alles over te typen. De knop werkt pas als beide datums zijn
ingevuld en verschillend zijn. Naast de knop verschijnt hoeveel wedstrijden verplaatst zijn, of
waarom het niet lukte.

**Filter van / tot** (een blok daaronder): beperkt de lijst tot een datumbereik. Ditzelfde bereik
bepaalt wat de verwijderknop weggooit.
- **Wis filter** — maakt het datumbereik weer leeg
- **Verwijder gefilterd** — verwijdert de testwedstrijden in het ingestelde datumbereik. Deze knop
  werkt pas nadat u bij **Filter van / tot** een bereik heeft opgegeven; zonder bereik staat er
  *Verwijder (stel filter in)* en is de knop grijs. Wilt u alles weggooien, kies dan een bereik dat
  alle wedstrijden omvat.

**Automatisch opslaan:** elke celwijziging wordt direct opgeslagen. Een ✅ of ⚠️ achter de rij geeft
de opslagstatus aan.

> **Voor de technisch beheerder**
>
> - Alle testdata gebruikt `ClubCode = 'ALLSTARS'` — echte wedstrijden blijven onaangetast
> - `bk_matches` wordt synthetisch gegenereerd als `ALLSTARS-{guid}` (28 tekens)
> - Testdata staat in `his.matches` — hetzelfde schema als productiewedstrijden, klaar voor gebruik
>   door de dagplanning
> - De keuze voor de democlub wordt in de browser bewaard (`localStorage`) en overleeft het sluiten
>   van de browser
>
> Voor de gewone beheerder volstaat: testwedstrijden staan apart van de echte wedstrijden en kunnen
> die nooit beïnvloeden. Uw keuze voor de democlub onthoudt de browser, ook na afsluiten.
> Zie verder [docs/TESTMODUS-ALLSTARS.md](TESTMODUS-ALLSTARS.md).

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/testdata/wedstrijden` | Alle test-wedstrijden ophalen |
| `GET /api/beheer/testdata/teams` | Echte clubteams ophalen voor keuzelijst |
| `POST /api/beheer/testdata/wedstrijden` | Test-wedstrijd aanmaken of bijwerken (upsert) |
| `POST /api/beheer/testdata/wedstrijden/verplaats-datum` | Alle test-wedstrijden van één datum naar een andere datum verplaatsen |
| `DELETE /api/beheer/testdata/wedstrijden/{bk}` | Één test-wedstrijd verwijderen |
| `DELETE /api/beheer/testdata/wedstrijden` | Test-wedstrijden verwijderen; met de optionele parameters `van`/`tot` alleen binnen dat datumbereik (dat is wat de knop **Verwijder gefilterd** doet), zonder parameters allemaal |

---

## 14. Voorkeurstijden & Teamregels (`/voorkeurstijden`)

De pagina `/voorkeurstijden` beheert twee soorten plannerregels per team.

### Team voorkeurstijden

Geef per team de gewenste aanvangstijden op voor een bepaalde dag van de week. De planner gebruikt deze tijden als richtpunt bij het inplannen.

| Veld | Uitleg |
|---|---|
| **Team** | Teamnaam, dezelfde waarden als in het wedstrijdprogramma |
| **Dag** | Dag van de week (1 = maandag … 7 = zondag) |
| **Tijd** | Gewenste aanvangstijd in HH:mm (bijv. `14:30`). Typ ook `1430` — de applicatie normaliseert dit automatisch |
| **Prioriteit** | Getal 1–10. **1 = hoogste prioriteit** (sterkste voorkeur), 10 = laagste. Gebruik 1 voor de primaire speeltijd van het team en hogere nummers voor alternatieven. Als een team meerdere tijden heeft, gebruikt de planner de laagste prioriteitswaarde als eerste keus |
| **Actief** | Aangevinkt = de regel telt mee. Uitgevinkt = tijdelijk uitschakelen zonder verwijderen |

### Teamregels

Fijnere regels per team: buffers vóór/na wedstrijden en een vaste veldvoorkeur. Teamregels worden in aflopende prioriteitsvolgorde toegepast — de regel met het hoogste getal wint bij een conflict.

| Regeltype | Waarde | Uitleg |
|---|---|---|
| **Buffer vóór** | Aantal minuten (0–240) | Reserveert extra tijd vóór de wedstrijd op het veld (bijv. 60 min = opslagveld vrijhouden voor warming-up) |
| **Buffer na** | Aantal minuten (0–240) | Reserveert extra tijd ná de wedstrijd op het veld (bijv. 30 min = uitlooptijd) |
| **Voorkeursveld** | Veldnummer + optionele aanvangstijd | Wijst een voorkeursveld toe aan het team, optioneel alleen op een bepaald tijdstip |

#### Prioriteit bij Teamregels

| Prioriteit | Effect |
|---|---|
| 0 | Laagste prioriteit — wordt als laatste toegepast |
| 1–98 | Normale volgorde: hoger = eerder toegepast door de planner |
| 99 | Hoogste prioriteit — overschrijft alle andere regels voor dit team |

**Tip:** Gebruik hogere prioriteiten voor regels die absoluut gelden (bijv. "eerste elftal altijd op veld 1") en lagere voor richtlijnen.

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/voorkeurstijden` | Alle voorkeurstijden voor de club |
| `POST /api/beheer/voorkeurstijden` | Nieuwe voorkeurstijd aanmaken |
| `PUT /api/beheer/voorkeurstijden/{id}` | Voorkeurstijd bijwerken |
| `DELETE /api/beheer/voorkeurstijden/{id}` | Voorkeurstijd verwijderen (soft-delete) |
| `GET /api/beheer/teamregels` | Alle teamregels voor de club |
| `POST /api/beheer/teamregels` | Nieuwe teamregel aanmaken |
| `PUT /api/beheer/teamregels/{id}` | Teamregel bijwerken |
| `DELETE /api/beheer/teamregels/{id}` | Teamregel verwijderen (soft-delete) |

---

## 15. Velden, veldbeschikbaarheid en trainingsschema (`/instellingen/velden`)

De pagina `/instellingen/velden` beheert alles rond de velden van de club: hoeveel er zijn, welk
type, wanneer het sportpark open is en wanneer een veld door training bezet is. Er is bewust geen
vaste aanname over aantal velden of kunstgras-versus-gras — elke club richt dit naar eigen situatie
in (#679).

### Velden

| Veld | Uitleg |
|---|---|
| **Veldnummer** | Uniek nummer, deployment-breed (niet alleen binnen de club) — eenmaal gekozen bij aanmaken, niet meer wijzigbaar |
| **Naam** | Weergavenaam, bijv. "veld 1" |
| **Type** | Vrije tekst, bijv. `kunstgras` of `natuurgras` — geen vaste lijst. Bepaalt welke velden de planner ontlast bij de grasveld-ontlasten optimalisatie |
| **Kunstlicht** | Bepaalt of de zonsondergang-beperking geldt voor dit veld |
| **Actief** | Uitvinken deactiveert het veld zonder het te verwijderen (geen harde delete — andere tabellen verwijzen ernaar) |

### Periodes

Een periode is een herbruikbaar regime met een vaste geldigheidsrange, bijvoorbeeld "Zomerstop"
(bijv. 1 juli t/m 15 augustus) of "Competitie". Een veldbeschikbaarheid-venster kan aan een periode
gekoppeld worden — het geldt dan uitsluitend terwijl die periode loopt, in plaats van het hele jaar.
Zo hoeft u niet langer twee keer per jaar handmatig vensters toe te voegen en weer te verwijderen
om de zomerstop te overbruggen (#581).

| Veld | Uitleg |
|---|---|
| **Naam** | Vrije tekst, bijv. "Zomerstop" |
| **Van / Tot** | Geldigheidsrange (kalenderdatums, beide inclusief) |
| **Actief** | Uitvinken schakelt de periode tijdelijk uit zonder hem te verwijderen |

Er mag nooit meer dan één actieve periode van dezelfde club tegelijk lopen — een overlappende
periode wordt bij het opslaan geweigerd. Verwijder eerst de gekoppelde veldbeschikbaarheid-vensters
(of koppel ze los) voordat u een periode verwijdert.

### Veldbeschikbaarheid

Het wekelijkse openingsvenster van het sportpark per veld per dag, optioneel gekoppeld aan een
periode. Een combinatie veld + dag + periode komt één keer voor; pas een bestaand venster aan in
plaats van een tweede toe te voegen.

| Veld | Uitleg |
|---|---|
| **Veld / Dag** | Alleen instelbaar bij aanmaken — verwijder en maak opnieuw aan om veld of dag te wijzigen |
| **Van / Tot** | Openingsvenster, bijv. 18:00–22:00 |
| **Beperkt tot zonsondergang** | Venster sluit eerder als de zon eerder ondergaat dan de ingestelde eindtijd (alleen relevant zonder kunstlicht) |
| **Periode** | "Standaard" (leeg) laat het venster het hele jaar gelden, behalve wanneer een andere periode actief is. Een gekozen periode laat het venster uitsluitend tijdens die periode gelden |

**Voorbeeld:** een kunstgrasveld is doordeweeks normaal gesproken gesloten (geen venster), maar is
tijdens de zomerstop juist wel beschikbaar omdat er geen trainingen zijn. Maak een periode
"Zomerstop" aan en voeg voor dat veld een venster toe dat aan die periode gekoppeld is — buiten de
zomerstop verandert er niets aan het reguliere schema.

### Trainingsschema

Terugkerende trainingsbezetting per veld per weekdag — telt automatisch mee als bezetting bij het
plannen van wedstrijden en in e-mailreacties, zonder aparte instelling elders. Dit is expliciet
**per dag** vrij in te richten: een club met weinig training op maandag en een volle donderdagavond
zet dat gewoon zo neer, in plaats van één vast wekelijks patroon te moeten forceren.

| Veld | Uitleg |
|---|---|
| **Veld / Dag** | Welk veld en welke weekdag het trainingsblok bezet |
| **Van / Tot** | Tijdvenster dat door training bezet is — mag korter zijn dan het hele openingsvenster |
| **Omschrijving** | Optioneel, bijv. "JO15-2 training" — zichtbaar in het overzicht, niet in e-mailreacties |
| **Actief** | Uitvinken schakelt het blok tijdelijk uit zonder het te verwijderen (bijv. tijdens een schoolvakantie) |

Een club die geen trainingsblokken toevoegt, merkt geen enkel verschil — de tabel is dan leeg en
telt nergens in mee.

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/velden` | Alle velden voor de club |
| `POST /api/beheer/velden` | Nieuw veld aanmaken |
| `PUT /api/beheer/velden/{veldNummer}` | Veld bijwerken |
| `GET /api/beheer/veldbeschikbaarheid` | Alle beschikbaarheidsvensters voor de club |
| `POST /api/beheer/veldbeschikbaarheid` | Nieuw venster aanmaken |
| `PUT /api/beheer/veldbeschikbaarheid/{id}` | Venster bijwerken |
| `DELETE /api/beheer/veldbeschikbaarheid/{id}` | Venster verwijderen |
| `GET /api/beheer/veldtraining` | Alle trainingsblokken voor de club |
| `POST /api/beheer/veldtraining` | Nieuw trainingsblok aanmaken |
| `PUT /api/beheer/veldtraining/{id}` | Trainingsblok bijwerken |
| `DELETE /api/beheer/veldtraining/{id}` | Trainingsblok verwijderen |
| `GET /api/beheer/veldperiodes` | Alle periodes voor de club |
| `POST /api/beheer/veldperiodes` | Nieuwe periode aanmaken |
| `PUT /api/beheer/veldperiodes/{id}` | Periode bijwerken |
| `DELETE /api/beheer/veldperiodes/{id}` | Periode verwijderen |

---

## 16. Teamaliassen (`/teamaliassen`)

Teamnamen komen niet altijd exact zo binnen als ze in Sportlink staan. Een e-mail van een
tegenstander noemt bijvoorbeeld `13-1` of `J013 1`, terwijl het team officieel `JO13-1` heet.
Zulke afwijkende schrijfwijzen worden automatisch vastgelegd als **alias** bij het team waar het
systeem denkt dat ze bij horen — met status **te beoordelen**.

Een alias wordt **nooit automatisch vertrouwd**. Pas nadat u hem op deze pagina goedkeurt, geldt
de schrijfwijze bij teamherkenning als volwaardige match. Zo kan een verkeerde gok van de AI zich
niet vastzetten en steeds opnieuw naar hetzelfde verkeerde team wijzen.

### Wat u op de pagina ziet

| Kolom | Uitleg |
|---|---|
| **Aangetroffen schrijfwijze** | De tekst exact zoals die in de e-mail of de Sportlink-data stond |
| **Hoort bij team** | De officiële teamnaam waaraan de alias is gekoppeld, met leeftijdscategorie |
| **Bron** | *Sportlink-sync* (uit de data), *AI-keuze* (door de AI toegewezen) of *Correctie coördinator* |
| **Status** | Te beoordelen, Goedgekeurd of Afgewezen |
| **Keer gebruikt** | Hoe vaak deze schrijfwijze al is aangetroffen — een hoog getal betekent dat goedkeuren of afwijzen echt effect heeft |
| **Aangemaakt** | Moment waarop de alias voor het eerst werd gezien (in uw eigen tijdzone) |

Bovenaan staan drie tellers (te beoordelen / goedgekeurd / afgewezen) en filterknoppen. De pagina
opent standaard op **Alleen te beoordelen**; met **Alles** ziet u ook de al beoordeelde aliassen.

### Wat u kunt doen

| Actie | Effect |
|---|---|
| **Goedkeuren** | De schrijfwijze wordt vanaf nu vertrouwd en wijst voortaan direct naar dit team |
| **Afwijzen** | De schrijfwijze wordt genegeerd; het systeem blijft per geval bepalen bij welk team hij hoort |
| **Verwijderen** | Verwijdert de alias volledig (met bevestigingsvraag). Duikt de schrijfwijze later weer op, dan verschijnt hij opnieuw als *te beoordelen* |

**Twijfelt u?** Wijs de alias af of laat hem staan. Alleen goedkeuren wat u zeker weet is
veiliger dan een fout vastleggen — een goedgekeurde alias stuurt namelijk toekomstige
e-mailverwerking naar dat team.

### Teamlijst opnieuw opbouwen

Bovenaan dezelfde pagina staat de knop **"Teamlijst opnieuw opbouwen"**.

Aliassen hangen aan de teamlijst, en die lijst wordt normaal bijgewerkt aan het eind van elke
nachtelijke synchronisatie. Staat de synchronisatie uit, of heeft die sinds een update nog niet
gelopen, dan kan de lijst leeg of verouderd zijn. U merkt dat aan:

- teamkeuzelijsten (bijvoorbeeld bij Voorkeurstijden) die leeg blijven;
- de melding dat er **niet** gecontroleerd kon worden of een team die dag al speelt;
- teams die niet herkend worden in binnenkomende e-mail.

De knop bouwt de lijst direct opnieuw op uit de al opgehaalde Sportlink-teams. U krijgt terug
hoeveel teams er nu actief zijn (en hoeveel het er daarvoor waren), zodat zichtbaar is of er
werkelijk iets veranderd is.

| Situatie | Wat u ziet |
|---|---|
| Lijst opnieuw opgebouwd | Aantal actieve teams vóór en na, plus het aantal goedgekeurde schrijfwijzen |
| Teams hersteld na een wijziging in de naamherkenning | Een extra regel met het aantal herstelde teams |
| Er is nog nooit gesynchroniseerd | Een waarschuwing dat er niets is om uit af te leiden — draai eerst een synchronisatie |

**Herhalen mag.** De knop is idempotent: twee keer indrukken verandert niets extra. Goedgekeurde
en afgewezen aliassen blijven staan; alleen de schrijfwijzen die uit de Sportlink-data zelf komen
worden bijgewerkt.

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/teamaliassen?status=pending` | Aliassen ophalen, optioneel gefilterd op status |
| `PUT /api/beheer/teamaliassen/{id}/valideer` | Alias goedkeuren (`validated`) of afwijzen (`rejected`) |
| `DELETE /api/beheer/teamaliassen/{id}` | Alias definitief verwijderen |
| `POST /api/beheer/teams/herstel` | Teamlijst opnieuw opbouwen (de knop hierboven) |

---

## 16a. AI-leermomenten (`/leermomenten`)

Het systeem leest binnenkomende e-mail en bepaalt zelf om wat voor soort verzoek het gaat. Heeft
het dat een keer verkeerd ingeschat en is die inschatting daarna gecorrigeerd, dan legt het systeem
dat vast als **leermoment**. Op deze pagina — in de zijbalk **Leermomenten**, met bovenaan de kop
*AI-leermomenten* — beoordeelt u die gevallen.

### Wat u ziet

Bovenaan staan drie tellers: **Te beoordelen**, **Gevalideerd (actief als voorbeeld)** en
**Afgewezen**. Staat er iets bij *Te beoordelen*, dan kleurt die teller oranje — dan wacht er werk
op u.

Daaronder staan vier filterknoppen — **Te beoordelen**, **Gevalideerd**, **Afgewezen** en **Alle** —
en een tabel met per leermoment: de **Datum**, het **Origineel type** (wat het systeem er eerst van
maakte), het **Afgeleid type** (wat het na de correctie had moeten zijn), de **Originele
samenvatting**, de **Correctie samenvatting** en de **Status**.

### Wat u kunt doen

| Actie | Effect |
|---|---|
| **Valideer** | Het systeem neemt dit geval voortaan mee als voorbeeld bij het beoordelen van nieuwe e-mail |
| **Afwijzen** | Het geval wordt niet als voorbeeld gebruikt |

De twee knoppen verschijnen alleen bij leermomenten die nog niet beoordeeld zijn.

**Twijfelt u?** Net als bij Teamaliassen geldt: keur alleen goed wat u zeker weet. Een gevalideerd
leermoment stuurt namelijk hoe toekomstige e-mail wordt afgehandeld.

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/leermomenten` | Leermomenten ophalen, eventueel gefilterd op status |
| `GET /api/beheer/leermomenten/stats` | De tellers boven aan de pagina |
| `PUT /api/beheer/leermomenten/{id}/valideer` | Eén leermoment valideren of afwijzen |

---

## 17. KNVB-verzetten zonder datum (`/instellingen`)

### Wat doet deze instelling?

Op de `/instellingen`-pagina staat de sectie **"KNVB-verzetten zonder datum"** met een schakelaar
en een regio-dropdown (#561).

Vraagt een tegenstander per e-mail om de wedstrijd te verzetten zonder zelf een concrete nieuwe
datum voor te stellen, dan zegt het systeem **geen nieuwe datum toe** — dat hoort eerst met de
begeleiding van het eigen team afgestemd te worden. In plaats daarvan verstuurt de pipeline
automatisch een antwoord naar de tegenstander met:

- de begeleiding van het eigen team in **BCC**, zodat beide teams het onderling kunnen afstemmen;
- de KNVB-speeldagenkalender-PDF van het huidige seizoen als **bijlage**;
- een paar concrete zaterdagen in de tekst waarop het eigen team volgens het huidige programma nog
  geen wedstrijd heeft, als voorzet voor het overleg.

| Instelling | Gedrag |
|---|---|
| **KNVB-kalender bij verzet-verzoek van tegenstander** (schakelaar, standaard **aan**) | Uit: geen bijlage/BCC/datumvoorstel — de bestaande herplan-afhandeling (alternatieve speeltijden op basis van veldbeschikbaarheid) blijft ongewijzigd van kracht. |
| **KNVB-regio** (dropdown, standaard **niet ingesteld**) | Bepaalt welke van de zes KNVB-speeldagenkalenders wordt meegestuurd. **Staat deze op "niet ingesteld", dan wordt de hele nieuwe flow overgeslagen** — er is bewust geen standaardregio in code, elke club vult de eigen regio hier zelf in. |

### Wanneer instellen?

Vul de KNVB-regio in zodra bekend is in welk KNVB-district de club uitkomt (West, Noord, Oost,
Zuid, Landelijk of LandelijkJeugd voor landelijke jeugdteams). Zonder deze instelling verandert er
niets aan het bestaande gedrag bij herplanverzoeken.

### Beperking huidige versie

De regio geldt voor de hele club (één instelling, geen per-team-regio). Clubs met teams in
meerdere districten (bijv. een landelijk seniorenteam naast jeugd in een regionaal district)
krijgen dus voor alle teams dezelfde kalender mee. Per-team-regio is een toekomstige uitbreiding
zodra teamregio automatisch uit Sportlink-data kan worden afgeleid.

---

## 17a. Uitgesloten e-mailadressen (`/instellingen`)

Onderaan de Instellingen-pagina staat de lijst **Uitgesloten e-mailadressen**, met daaronder de
toelichting *"Adressen op deze lijst worden altijd overgeslagen door de email-verwerker."*
Berichten van een adres dat hier staat, worden door de automatische e-mailverwerking overgeslagen:
er gaat geen antwoord uit en er wordt niets ingepland. Gebruik dit voor nieuwsbrieven,
no-reply-adressen, het wedstrijdsecretariaat van uw eigen club en andere afzenders waarop het
systeem nooit moet reageren.

De tabel heeft de kolommen **E-mailadres**, **Omschrijving** en **Actief**.

- **Toevoegen:** klik rechtsboven de lijst op **Adres toevoegen**. Er verschijnt een kaartje
  *Nieuw adres toevoegen* met de velden **E-mailadres** en **Omschrijving (optioneel)** — vul die
  omschrijving in, zodat een volgende beheerder ziet waarom het adres er staat. Bevestig met
  **Toevoegen**.
- **Verwijderen:** klik op het prullenbak-knopje achter de regel.

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/uitgesloten-emails` | De volledige lijst ophalen |
| `POST /api/beheer/uitgesloten-emails` | Een adres toevoegen |
| `DELETE /api/beheer/uitgesloten-emails/{id}` | Een adres verwijderen |

---

## 18. Wijzigingsverzoeken (`/wijzigingsverzoeken`)

Toont wijzigingsverzoeken die tegenstanders in Sportlink Club hebben ingediend voor de datum, tijd
of accommodatie van een wedstrijd, ingedeeld naar het voorbeeld van Sportlinks eigen scherm.

**Statusfilter** bovenaan (knoppenrij, met aantallen): **Openstaand** (standaard), Goedgekeurd,
Afgewezen, Ingetrokken, Alle. Alleen openstaande verzoeken wachten op een beslissing van uw club.

**Kolommen:** statusicoon (oranje uitroepteken = openstaand, groen vinkje = goedgekeurd, rood kruis
= afgewezen, grijs = ingetrokken), Wedstrijdnr., Thuis, Uit, Datum, Tijd, Accommodatie, Gevraagd
(alleen wat afwijkt van de huidige planning), Reden. Wedstrijdnummer en teamnamen komen uit de
eigen wedstrijdgegevens van de app; staat er een streepje, dan is die wedstrijd nog niet aan het
Sportlink-kenmerk gekoppeld (dat gebeurt automatisch door de dagelijkse voorbereidingstaak, of zodra
u de wedstrijd in Dagplanning opent).

Per openstaand verzoek staan twee compacte knoppen:
- **✓ (groen)** — keurt de wijziging rechtstreeks goed in Sportlink Club.
- **✗ (rood)** — opent een toelichtingsveld; de toelichting is verplicht en gaat naar de
  tegenstander. Pas na **Afwijzen** in dat veld wordt het verzoek daadwerkelijk afgewezen.

Staat dry-run aan (§19), dan wordt de actie gesimuleerd en gelogd; het scherm meldt dat expliciet.

Deze pagina en "Oefenwedstrijd aanmaken" (menu-item: **Wedstrijden**, zie §18a) staan alleen in het menu als de Sportlink Web Extension
aan staat (§19); staat hij uit, dan verdwijnen beide menu-items en toont de Dagplanning geen
Sportlink-kolom. Deze pagina is onderdeel van de Sportlink Web Extension (zie §19) en vereist dus dat die feature
is ingeschakeld en gekoppeld voor de rol die deze acties uitvoert.

---

## 18a. Oefenwedstrijd aanmaken (`/oefenwedstrijd-aanmaken`)

> **Scaffolding (#997/#1116):** de aanroep naar Sportlink Club wordt altijd gesimuleerd totdat een
> mens de exacte aanmaak-body met een netwerktrace heeft bevestigd. U ziet na het aanmaken wél wat
> er zou zijn meegestuurd, maar er verandert niets in Sportlink.

Bedoeld voor snelle invoer: één scherm met **datum**, **aanvangstijd**, **duur** (standaard 90
minuten), **team** (keuzelijst met de actieve clubteams uit de eigen database), **tegenstander**
(vrije tekst), **veld** (keuzelijst met de actieve velden) en een optionele **omschrijving**. Enter
in een veld verstuurt het formulier.

Wat u níet hoeft in te vullen, doet de server:

| Sportlink-veld | Waar het vandaan komt |
|---|---|
| Team-ID | Het gekozen team, via de teamkoppeling met de gesynchroniseerde Sportlink-teams. Ontbreekt die koppeling (bijv. een puur lokaal team), dan blijft het leeg en ziet u dat als waarschuwing |
| Leeftijdscategorie | Van het gekozen team (bijv. `JO10`) |
| Locatie | Altijd de eigen accommodatie: het veld **Accommodatie** op de Instellingen-pagina wordt op naam opgezocht in de locatielijst van Sportlink Club. Niet (eenduidig) gevonden → leeg + waarschuwing |
| Omschrijving | Leeg gelaten → `Oefenwedstrijd [team] - [tegenstander] ([veld])` |

Het gekozen veld wordt nog **niet** als Sportlink-veld meegestuurd: dat gebeurt in het plan van
#997 pas ná het aanmaken via de bestaande veldwijziging. Het staat wel in de standaard-omschrijving
en in het auditlog.

Na het aanmaken toont een blauw (gesimuleerd/geslaagd) of rood (afgewezen) blok de melding plus de
afgeleide gegevens en eventuele waarschuwingen. Deze pagina is onderdeel van de Sportlink Web
Extension (§19) en vereist dat die is ingeschakeld en gekoppeld voor de rol Wedstrijdzaken.

---

## 19. Sportlink Web Extension (`/sportlink-extension-settings`) — schrijfrechten naar Sportlink Club

> Deze feature is **gedeeltelijk gebouwd** (epic #986) — zie
> [docs/SPORTLINK-WEB-EXTENSION.md](SPORTLINK-WEB-EXTENSION.md) voor de actuele stand per
> deelfunctie vóór u hierop vertrouwt.

U opent dit scherm via **Instellingen → Sportlink Ext.** in de zijbalk, of via de knop
**Openen →** op de doorverwijskaart onderaan de Instellingen-pagina. De instellingen staan sinds
#1122 dus niet meer op Instellingen zelf.

Bovenaan het scherm staat het blok **Aan/uit** met de schakelaar
**"Sportlink Web Extension inschakelen"** (standaard **uit**). Daaronder staat de tabel
**"Sportlink-serviceaccounts per rol"**: elke functionele rol (bijv. "Wedstrijdzaken") gebruikt een
eigen, smal-geschaald Sportlink-serviceaccount — nooit uw persoonlijke Sportlink-login — zodat
Sportlinks eigen audit-log de rolnaam toont in plaats van een persoonsnaam.

| Kolom | Betekenis |
|---|---|
| Rol | De functionele rol waarvoor dit account wordt gebruikt |
| Gekoppeld | Of er een geldige, actieve toegangssleutel voor deze rol is opgeslagen |
| Laatst gekoppeld door / op | Wie de koppeling voor het laatst (opnieuw) heeft geregistreerd, en wanneer |
| Sportlink-account | Naam van het gekoppelde Sportlink-serviceaccount |

Achter elke rol staat de knop **Koppeling (opnieuw) registreren**. Die knop overschrijft alleen de
weergavenaam en de "laatst gekoppeld door/op"-gegevens — de werkende toegangssleutel blijft
daarbij ongewijzigd. Het daadwerkelijk *verkrijgen* van een nieuwe sleutel kan niet vanuit de
webapp: Sportlink staat geen inlog via onze eigen applicatie toe (de terugverwijzing naar een eigen
adres is aan hun kant dichtgezet). Dit is dus altijd een aparte, eenmalige technische stap die een
**technisch beheerder** van deze installatie zelf uitvoert, op zijn eigen computer, met een lokaal
hulpprogramma (`Tools/SportlinkTokenCapture`, met een echte browserlogin — nooit door een
geautomatiseerd script of AI-agent, zie `docs/SPORTLINK-WEB-EXTENSION.md` §3.3/§4.4 voor de
volledige stappen). Het resultaat plakt die beheerder daarna in het vak **Refresh-token
registreren** onder in datzelfde registratiekaartje — "refresh-token" is de technische naam voor
die toegangssleutel — en bevestigt met **Token registreren**. Een gekoppelde rol houdt zichzelf
daarna automatisch actief via een uur-timer, ook zonder dagelijks gebruik; dit hoeft dus niet
routinematig herhaald te worden.

**Dry-run: alles simuleren, niets naar Sportlink schrijven** — naast de aan/uit-schakelaar staat een
tweede schakelaar die **standaard AAN** staat. Zolang deze aan staat, doorloopt elke kleedkamer-/
veldwijziging en elk goed-/afgekeurd wijzigingsverzoek de volledige controle (rol-koppeling,
guardrails, audit-logging), maar de daadwerkelijke aanroep naar Sportlink Club wordt overgeslagen —
u ziet in de Admin GUI een informatieve melding ("Dry-run: niets gewijzigd in Sportlink Club — de
aanroep is gesimuleerd en gelogd") in plaats van een succes- of foutmelding. Zet dit pas uit nadat u
de rol-koppeling en de statussectie hieronder heeft gecontroleerd.

**Status Sportlink Web Extension** — een sectie onder de rollen-tabel die in één oogopslag toont of
de extension/dry-run aan staan, of uitgaande verbindingen zijn toegestaan, de koppelingsstatus en
laatste tokenverversing per rol, de laatste mutatiefout, en de uitkomst van de dagelijkse
contract-check (een geautomatiseerde controle die vroegtijdig waarschuwt als Sportlink zijn eigen
website heeft gewijzigd). Dit alles komt uit onze eigen gegevens — er gaat geen aanroep naar
Sportlink uit, tenzij u zelf op **"Nu live controleren"** klikt.

Eenmaal gekoppeld verschijnt in **Dagplanning** per wedstrijd een Sportlink-paneel met de actuele
Sportlink-status en (afhankelijk van wat Sportlink voor die wedstrijd toestaat) invoervelden om
kleedkamers, veld en officials (scheidsrechter/AR1/AR2) rechtstreeks terug te schrijven, plus een
"Open in Sportlink"-knop die de wedstrijd in een nieuw tabblad op club.sportlink.com opent.

Bij een thuiswedstrijd staat onderaan het paneel ook **"Wijzigingsverzoek datum/tijd/accommodatie"**:
een nieuwe datum, starttijd en/of accommodatie invullen met een verplichte toelichting, en op
**"Wijzigingsverzoek valideren"** klikken. Dit valideert alleen — Sportlinks meldingen (indien
aanwezig) verschijnen letterlijk onder het formulier. Er is bewust **geen bevestigknop**: het
daadwerkelijk versturen van een wijzigingsverzoek naar de tegenstander is nog niet gebouwd, dus deze
actie blijft altijd een simulatie, ook als dry-run voor uw club uit staat.

---

## 20. E-mail tester (`/email-tester`)

Voert de AI-classificatie van een binnenkomend bericht uit als **dry-run** — er wordt niets
verzonden en niets in de e-maillog vastgelegd. Handig om te controleren hoe de AI een nieuw of
grensgeval van een bericht zou classificeren vóórdat het echt binnenkomt, of om een
classificatie-instelling te verifiëren na een wijziging in de e-mailtemplates.

---

## 20a. E-mailtemplates (`/email-templates`)

Hier past u de standaardteksten aan die het systeem automatisch verstuurt — bijvoorbeeld het
antwoord op een verzoek om een wedstrijd te verzetten. U opent het scherm via
**Instellingen → E-mailtemplates** in de zijbalk.

### Gedeelde e-mail voetnoot

Bovenaan staat het vak **Gedeelde e-mail voetnoot**. Wat u daar intypt komt automatisch onder
*elke* uitgaande e-mail te staan — typisch een afsluiting met de naam van de coördinator. Klik op
**Opslaan** om de voetnoot vast te leggen.

### De templatelijst

Daaronder staat een tabel met per template de kolommen **Template** (de sleutel die de code
gebruikt), **Naam** (het onderwerp), **Categorie** en **Status**. De status is **Standaard** —
de tekst die standaard in de applicatie zit — of **Aangepast**: dan heeft iemand hier een eigen
tekst vastgelegd.

| Knop | Effect |
|---|---|
| **Aanpassen** | Opent het bewerkformulier voor die template |
| **Verwijder aanpassing** | Gooit uw eigen tekst weg en zet de standaardtekst van de applicatie terug. Verschijnt alleen bij een template met status *Aangepast*, en raakt de andere templates niet |
| **+ Nieuwe template** | Legt een eigen tekst vast voor een template die nu nog op de standaard staat; u kiest eerst uit de lijst om welk type bericht het gaat |

### Het bewerkformulier

Het formulier heeft de velden **Template key** (bij een bestaande template vast), **Onderwerp**,
**Body template** en het vinkje **Actief**. Bewaren doet u met **Opslaan**, weggooien met
**Annuleren**.

In de tekst staan **plaatshouders** tussen dubbele accolades. Die vult het systeem bij verzending
in met de echte waarde. Beschikbaar zijn: `{{voornaam}}`, `{{aanhef}}`, `{{datum}}`, `{{team}}`,
`{{tegenstander}}` en `{{aanvangstijd}}`. Laat de accolades en de naam ertussen precies staan zoals
ze zijn — typt u er iets anders, dan komt er letterlijk `{{team}}` in de mail te staan. De voetnoot
uit het vak bovenaan wordt automatisch onder de body geplakt; die hoeft u hier dus niet te
herhalen.

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/templates` | Alle templates ophalen |
| `PUT /api/beheer/templates/{key}` | Eén template opslaan of bijwerken |
| `POST /api/beheer/templates/{key}/reset` | De eigen tekst weggooien en terug naar de standaardtekst |

---

## 21. Lichte of donkere weergave

Rechtsboven in de balk staat een knopje met een maantje (of een zonnetje, als u al donker kijkt).
Daarmee schakelt u de hele Admin GUI om tussen een lichte en een donkere weergave.

- **Uw keuze wordt onthouden** in de browser waarmee u werkt. Gebruikt u thuis een andere computer
  of een ander profiel, dan stelt u het daar apart in.
- **Heeft u nog niets gekozen**, dan volgt de site de voorkeur van uw besturingssysteem of browser.
  Staat uw laptop 's avonds automatisch op donker, dan is de Admin GUI dat ook.
- **Het omschakelen is direct.** Er wordt niets opnieuw opgehaald bij de server en u hoeft de pagina
  niet te herladen.

Wat níet meekleurt, en met opzet: de statuskleuren. Groen blijft "klaar", rood blijft "fout", en de
oranje markering die aangeeft dat u in de demo-/testclub werkt blijft oranje. Die kleuren dragen
betekenis — als ze per weergave zouden verschillen, zou u een waarschuwing kunnen missen.

Welke kleuren de club in beide weergaven gebruikt, stelt u in bij **Thema** — zie hoofdstuk 22.
Heeft uw club nog geen eigen donkere kleuren ingesteld, dan gebruikt de site een neutrale donkere
set: de site werkt dus meteen, ook zonder dat u iets instelt.

---

## 22. Kleuren van de club instellen (`/instellingen/thema`)

Op dit scherm stelt u de kleuren in die de Admin GUI gebruikt. Sinds de lichte/donkere weergave
(hoofdstuk 21) stelt u **twee** sets in: één voor licht en één voor donker.

### Zo werkt het

1. **Kies welke weergave u bewerkt** met de knoppen *Licht bewerken* / *Donker bewerken*. De hele
   interface schakelt meteen mee, zodat u ziet wat u instelt in plaats van het te moeten voorstellen.
2. **Kies eventueel een basisthema** uit de keuzelijst bovenaan. Dat vult in één keer alle kleuren
   van de weergave die u nu bewerkt. Daarna kunt u elke kleur los bijstellen — een basisthema is een
   startpunt, geen keurslijf.
3. **Stel de losse kleuren bij** met de kleurenkiezer of door de code in te typen.
4. **Opslaan.** Tot u opslaat is alles wat u ziet een voorbeeld: sluit u het scherm zonder opslaan,
   dan blijft alles zoals het was.

### Kleuren van de eigen website ophalen

Vult u bij **Club-website URL** het adres van de clubsite in en klikt u op **Ophalen**, dan zoekt
het systeem de kleuren, het icoontje en het logo van die pagina. Klik daarna op een gevonden kleur
om die als primaire kleur van de weergave die u nu bewerkt over te nemen.

Twee dingen om te weten:

- **Er worden alleen kleuren gevonden die letterlijk in de pagina staan.** Veel moderne
  clubwebsites zetten hun huisstijl in een apart opmaakbestand; dan levert Ophalen weinig of niets
  op. Dat is geen storing — typ de kleurcodes in dat geval gewoon in. Uw clubbeheerder of
  websitebouwer kent ze.
- **Alleen het adres dat u hier heeft opgeslagen mag worden benaderd.** Dat is een bewuste
  beveiligingsmaatregel: hij voorkomt dat het scherm gebruikt kan worden om willekeurige adressen op
  te vragen. Wilt u een andere site uitlezen, sla dan eerst dat adres op.

### Icoontje en logo

Onder de website-URL staan nog twee velden: **Favicon URL** (het kleine icoontje in het
browsertabblad) en **Club-logo URL** (het logo links boven in de zijbalk en boven aan het
dashboard). Vindt **Ophalen** ze op de clubsite, dan verschijnt er een voorbeeldje met de knop
**Gebruiken** om die waarde over te nemen; anders plakt u er zelf het webadres van een afbeelding
in. Leeg laten mag: dan toont de app geen logo en het standaard-icoontje.

### Wat u níet kunt instellen, en waarom

Statuskleuren liggen vast: groen voor "klaar", rood voor "fout", en oranje voor de markering dat u
in de demo-/testclub werkt. Die kleuren dragen betekenis — als ze per club of per weergave zouden
verschillen, zou u een waarschuwing kunnen missen.

### Standaard herstellen

De knop **Standaard herstellen** zet beide weergaven terug op de standaardkleuren van de
applicatie. Ook dat is pas definitief nadat u opslaat.

---

## 23. Feedback geven (FEEDBACK-knop) — met voorbeeld vóór publicatie

De FEEDBACK-knop rechtsboven maakt van uw melding een **GitHub-issue**. Die issue staat in een
publieke repository: hij is **openbaar op internet** en voor iedereen leesbaar, ook zonder account.
Dat is bewust — het is de plek waar de ontwikkelaar het werk bijhoudt — maar het betekent dat alles
wat u typt openbaar wordt.

### De vier stappen

| Stap | Wat u doet | Wat het systeem doet |
|---|---|---|
| 1. **Formulier** | Kies Fout / Verzoek / Vraag en beschrijf de melding in eigen woorden | Beoordeelt of de beschrijving compleet is en stelt zo nodig maximaal drie aanvulvragen |
| 2. **Overzicht** | Controleer type, pagina en uw eigen tekst | — |
| 3. **Voorbeeld** | **Lees de volledige tekst die gepubliceerd wordt** en kies: *Aanpassen* of *Openbaar publiceren* | Stelt de exacte titel en body samen — inclusief de AI-samenvatting en acceptatiecriteria — en toont die, zónder iets te publiceren |
| 4. **Bevestiging** | — | Pas nu wordt het issue aangemaakt; u krijgt het issuenummer met een link |

Stap 3 is de publicatiegrens: tot u daar op **Openbaar publiceren** klikt, is er niets naar
internet gegaan. Klikt u op **Aanpassen**, dan keert u terug naar het formulier met uw tekst intact.

### Wat u zelf moet controleren in het voorbeeld

Het systeem blokkeert automatisch e-mailadressen en Nederlandse telefoonnummers — zowel in uw eigen
tekst als in de tekst die de AI ervan maakt. Ziet u die melding, dan haalt u het betreffende gegeven
weg en probeert u het opnieuw.

Die controle is een **vangnet, geen anonimisering**. Niet herkend worden onder meer:

- namen van personen (leden, trainers, leiders, ouders)
- adressen, woonplaatsen en postcodes
- geboortedata en leeftijden van individuele personen
- lidnummers, relatiecodes en andere identificerende nummers
- wachtwoorden, tokens, API-sleutels en connectiestrings
- buitenlandse telefoonnummers

Lees het voorbeeld daarom woord voor woord. Twijfelt u? Klik op **Aanpassen** en herschrijf de
melding zonder het gegeven — of beschrijf de situatie in algemene termen ("een speler van JO13-1"
in plaats van een naam).

> **Eenmaal gepubliceerd is niet terug te draaien.** GitHub bewaart bewerkingsgeschiedenis, en
> zoekmachines en archiefdiensten nemen nieuwe issues binnen minuten op. Een issue later aanpassen
> of verwijderen haalt de gegevens daar niet meer weg.

### Grenzen

- Maximaal 5 publicaties per 10 minuten. Het voorbeeld opvragen telt niet mee — dat publiceert niets.
- Werkt alleen als de GitHub-koppeling is geconfigureerd; anders meldt de widget dat en wordt er
  niets verstuurd.
