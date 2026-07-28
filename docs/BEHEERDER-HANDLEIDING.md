# v2 Admin GUI — handleiding

Deze handleiding beschrijft het Admin-portaal (Blazor WebAssembly) en de bijbehorende admin-API
in `FunctionApp/Admin/`. Het portaal is **live** op (vul jouw clubspecifieke URLs in):

- **Admin GUI:** zie Azure Portal → Static Web App → URL
- **Function App:** `https://func-<clubcode>-sportlink.azurewebsites.net`

De SWA dient uitsluitend statische Blazor-bestanden. De Blazor-app haalt zelf een Bearer token
op via MSAL (Entra ID) en stuurt dat mee naar de Function App. Easy Auth op de Function App
valideert het token server-side.

---

## 1. Lokaal ontwikkelen

### Voorbereiding (eenmalig)

1. Stel `FunctionApp/local.settings.json` correct in (zie `local.settings.template.json`)
2. Voer alle migraties uit op de lokale SQL Server:
   ```powershell
   sqlcmd -S YOUR_SQL_SERVER -d SportlinkSqlDb -E -i .\Database\Script.PostDeployment1.sql
   ```
3. Installeer Azurite (voor storage emulator): `npm install -g azurite`

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

**Activeren:** Klik op **Testmodus** onderaan de zijbalk (onder de gebruikersnaam).  
**Verlaten:** Klik op **Testmodus — verlaten** (gele knop, verschijnt in de zijbalk).

In testmodus:
- Toont de zijbalk **"ALLSTARS (testmodus)"** als clubnaam
- Laadt de dagplanning fictieve wedstrijden uit `his.matches WHERE ClubCode='ALLSTARS'`
- Is het submenu **Testdata → Wedstrijden** zichtbaar voor het invoeren van fictieve wedstrijden
- Zijn synchronisatie en e-mailverwerking op de Instellingen-pagina verborgen (niet van toepassing)

Volledige documentatie: [docs/TESTMODUS-ALLSTARS.md](TESTMODUS-ALLSTARS.md)

### Dagplanning — status-badges

De dagplanning toont per wedstrijd een badge in de Status-kolom:

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

Alleen de tab **Optimale planning** is te bewerken. **Huidige situatie** toont de stand uit Sportlink en
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

## 3. Azure resources aanmaken (eenmalig — reeds gedaan)

De resources zijn aangemaakt en actief. Deze sectie is documentatie voor toekomstige herinrichting.

### Static Web App aanmaken

```bash
az staticwebapp create \
  --name swa-<clubcode>-sportlink \
  --resource-group rg-<clubcode>-sportlink \
  --location westeurope \
  --sku Free
```

### Deployment token ophalen en opslaan als GitHub Secret

```bash
az staticwebapp secrets list \
  --name swa-<clubcode>-sportlink \
  --resource-group rg-<clubcode>-sportlink \
  --query "properties.apiKey" -o tsv
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

## 10. Teambegeleiding-pagina (`/teambegeleiding`)

De pagina `/teambegeleiding` stelt beheerders én gebruikers met de **user-rol** in staat team-contactgegevens op te zoeken en vragen door te sturen aan de begeleiding.

### Functionaliteit

1. **Team selecteren** — dropdown met alle teams waarvoor begeleiding beschikbaar is (uit `avg.Teambegeleiding`)
2. **Begeleiders inzien** — kaarten per begeleider met naam en teamrol. E-mailadressen en telefoonnummers worden **nooit getoond** (AVG art. 6.1.f)
3. **Vraag doorsturen** — klik "Stel een vraag" → vul Onderwerp (optioneel) en Bericht in → "Versturen"
   - To: coach (opgezoekt server-side uit `avg.Teambegeleiding`)
   - Reply-To: e-mailadres van de aanvrager (automatisch uit Entra ID)
   - BCC: coördinator (uit `dbo.AppSettings.plannerEmailAdres`)
   - Coach antwoordt rechtstreeks naar aanvrager — aanvrager ziet nooit het coach-adres
4. **Teambegeleiding importeren** — CSV-export uit Sportlink inlezen; het scherm bevat de exportstappen
   en een voorbeeldweergave vóór bevestiging. De CSV wordt in de browser verwerkt en nooit op de server
   opgeslagen.
   - **Een import vervangt de bestaande teambegeleiding van de club volledig** — alle bestaande rijen
     van de club worden eerst verwijderd (`DELETE WHERE ClubCode`), daarna volgt de nieuwe lijst. Er
     wordt niets samengevoegd, dus een onvolledige export herstel je door een complete export opnieuw
     te importeren.
   - Volledige exportinstructie voor de beheerder: [ADMIN-TEAMBEGELEIDING-IMPORT.md](ADMIN-TEAMBEGELEIDING-IMPORT.md)

> **Menupositie:** Teambegeleiding staat bewust direct onder Dashboard in de zijbalk en als eerste tegel
> op het dashboard — het is het meest gebruikte scherm, omdat contactgegevens hier sneller te vinden
> zijn dan in Sportlink Club zelf (#669).

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/teambegeleiding` | Alle teams met begeleiding |
| `GET /api/beheer/teambegeleiding/{team}` | Begeleiders van team (naam + rol, nooit contactgegevens) |
| `POST /api/beheer/teambegeleiding/doorsturen` | Doorsturen van vraag naar coach |
| `POST /api/beheer/teambegeleiding/import` | CSV-import; vervangt alle rijen van de club |

Auth: `RequireAuthenticated()` — toegankelijk voor zowel admin- als user-rol.

---

## 11. Speeltijden-pagina (`/instellingen/speeltijden`)

De pagina `/instellingen/speeltijden` (alleen admin-rol) beheert de speeltijden per leeftijdscategorie. De planner gebruikt uitsluitend `dbo.Speeltijden.WedstrijdTotaal` voor de berekening van veldblokkeertijden — de Sportlink API-waarde `Duration` wordt niet meer gebruikt.

Het veld **Totaal (incl. rust)** is de totale veldblokkeertijd die de planner direct gebruikt. Rust wordt **niet** apart opgeteld in code — WedstrijdTotaal = speeltijd + rust + buffer.

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

Klik op **"Testmodus"** in de zijbalk (onderaan bij de ingelogde gebruiker). De knop activeert de ALLSTARS-modus:
- Alle API-aanroepen sturen voortaan `X-Club-Code: ALLSTARS` mee
- Het menu **"Test data"** verschijnt in de zijbalk
- De actieve club-indicator in de topbalk toont "ALLSTARS"

### Deactiveren

Klik op **"Testmodus — verlaten"** (oranje knop) om terug te keren naar de normale clubmodus.

### Test data → Wedstrijden

De pagina `/testdata/wedstrijden` toont een invoergrid voor het aanmaken van fictieve wedstrijden:

| Kolom | Beschrijving |
|---|---|
| Datum | Datum van de wedstrijd (↓ fill-down beschikbaar) |
| Team (thuis) | Selecteer een echt clubteam uit de dropdown |
| Tegenstander | Vrij tekstveld voor de naam van de tegenstander |
| Starttijd | Aanvangstijd (↓ fill-down beschikbaar) |
| Veld | Veldnaam selecteren uit de dropdown |
| Velddeel | Deelveld-dropdown — verschijnt alleen als het team op een deelveld speelt. JO7-JO10 (¼ veld): A1/A2/B1/B2; JO11-JO12 (½ veld): A/B. De beschikbare opties worden automatisch bepaald op basis van de speeltijden-tabel. |
| Soort | Competitie / Beker / Oefenwedstrijd / Vriendschappelijk |

**Globale invoerbalk** (boven de tabel): Stel datum, soort, tegenstander en starttijd in vóór het toevoegen van rijen — deze waarden worden als default voor nieuwe rijen gebruikt.

**Knoppen:**
- **Alle teams** — voegt één rij per huidig clubteam toe en slaat alles op
- **+ Lege rij** — voegt één lege rij toe
- **↓** in een kolomkop — kopieert de eerste ingevulde waarde naar alle lege cellen in die kolom
- **Verwijder alles** — verwijdert alle testdata-wedstrijden (`WHERE ClubCode='ALLSTARS'`)

**Auto-save:** Elke celwijziging triggert direct een opslaan naar de database. Een ✅ of ⚠️ achter de rij geeft de opslagstatus aan.

### Technische details

- Alle testdata gebruikt `ClubCode = 'ALLSTARS'` — echte wedstrijden (`ClubCode = '<clubcode>'`) blijven onaangetast
- `bk_matches` wordt synthetisch gegenereerd als `ALLSTARS-{guid}` (28 tekens)
- Testdata staat in `his.matches` — hetzelfde schema als productiewedstrijden, klaar voor gebruik door de dagplanning
- De ALLSTARS-modus is persistent in de browser (localStorage via `ClubSelectorService`) en wordt hersteld bij herstart van de browser

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/testdata/wedstrijden` | Alle test-wedstrijden ophalen |
| `GET /api/beheer/testdata/teams` | Echte clubteams ophalen voor dropdown |
| `POST /api/beheer/testdata/wedstrijden` | Test-wedstrijd aanmaken of bijwerken (upsert) |
| `DELETE /api/beheer/testdata/wedstrijden/{bk}` | Één test-wedstrijd verwijderen |
| `DELETE /api/beheer/testdata/wedstrijden` | Alle test-wedstrijden verwijderen |

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

### Veldbeschikbaarheid

Het wekelijkse openingsvenster van het sportpark per veld per dag. Een combinatie veld + dag komt
één keer voor; pas een bestaand venster aan in plaats van een tweede toe te voegen.

| Veld | Uitleg |
|---|---|
| **Veld / Dag** | Alleen instelbaar bij aanmaken — verwijder en maak opnieuw aan om veld of dag te wijzigen |
| **Van / Tot** | Openingsvenster, bijv. 18:00–22:00 |
| **Beperkt tot zonsondergang** | Venster sluit eerder als de zon eerder ondergaat dan de ingestelde eindtijd (alleen relevant zonder kunstlicht) |

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

### API-endpoints

| Endpoint | Beschrijving |
|---|---|
| `GET /api/beheer/teamaliassen?status=pending` | Aliassen ophalen, optioneel gefilterd op status |
| `PUT /api/beheer/teamaliassen/{id}/valideer` | Alias goedkeuren (`validated`) of afwijzen (`rejected`) |
| `DELETE /api/beheer/teamaliassen/{id}` | Alias definitief verwijderen |

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
