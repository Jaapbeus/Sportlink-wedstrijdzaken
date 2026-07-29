# Architectuur — E-mail module

> **Dit document is analyse + ontwerp, geen implementatie.** Er is in deze sessie geen code
> gewijzigd. Raadpleeg dit document vóórdat je: (a) een nieuw Blazor-scherm bouwt dat e-mail moet
> kunnen versturen, (b) `EmailGraphService`, `EmailProcessingRepository` of `EmailTemplateService`
> aanraakt, of (c) de "verzenden als mezelf"-eis (afzenderstrategie, §4.6) oppakt.

> **De code is leidend.** Staat er iets in dit document dat je niet terugvindt op de genoemde
> bestand:regel-verwijzing, dan is dit document fout — meld het als issue onder het epic (§7).
> Elke bewering hieronder is geverifieerd door de aangehaalde broncode te lezen; waar dat niet kon
> (met name de OBO-tokenmechaniek in §4.6), staat dat expliciet als open vraag en niet als feit.

## Waarom dit document bestaat

E-mailfunctionaliteit is in dit project organisch gegroeid vanuit één feature (automatische
AI-verwerking van inkomende post) en is daarna hergebruikt voor een tweede, functioneel heel ander
doel (een beheerder die handmatig een vraag doorstuurt, #765). Dat hergebruik werkte, maar kostte
elke keer een nieuwe, ad-hoc doorverbinding met de bestaande bouwstenen in plaats van een stabiel
contract. De eigenaar wil vanuit meerdere (ook nog te bouwen) Blazor-schermen kunnen e-mailen, plus
een generiek e-mailscherm, terwijl de automatische verwerking ongewijzigd blijft — en een nieuwe
eis: de ingelogde medewerker kiest zelf zijn eigen afzenderadres. Dit document inventariseert wat er
nu is, legt concreet bloot wat een derde/vierde verzendpad zou breken, en ontwerpt één centrale,
modulaire e-mail-laag zodat een toekomstig scherm sanitizing, logging, ontvangerresolutie en
Graph-verzending kan "inpluggen" zonder ze opnieuw te bouwen.

---

## 1. Huidige situatie — volledige inventarisatie

### 1.1 Bestanden en verantwoordelijkheden

| Bestand | Verantwoordelijkheid |
|---|---|
| `FunctionApp/Email/EmailGraphService.cs` | Enige plek die Microsoft Graph `SendMail` aanroept. Wrapt Graph SDK: ongelezen mail ophalen, categoriseren, `SendReplyAsync` (automatische AI-reply), `StuurTeamContactDoorAsync` (doorsturen naar begeleiding). Leest de vaste mailbox uit de env var `GraphMailbox` (regel 17, 23-24) |
| `FunctionApp/Email/IEmailGraphService.cs` | Het huidige contract — 6 methoden, waarvan 2 daadwerkelijk versturen |
| `FunctionApp/Email/EmailBijlage.cs` | Immutable `record` voor één bijlage (bestandsnaam, bytes, content-type) |
| `FunctionApp/Email/EmailProcessorFunction.cs` | Timer-getriggerde orchestrator van de volledige AI-pipeline (fase 1: Graph+AI zonder DB, fase 2: DB-state-machine), 1063 regels |
| `FunctionApp/Email/EmailProcessingRepository.cs` | `internal static` ADO.NET-repository voor `planner.EmailVerwerking` — inclusief de synthetische audit-insert (regel 96-121, zie §1.6) |
| `FunctionApp/Email/EmailPersistenceService.cs` | Dunne, DI-testbare wrapper rond `IEmailPersistenceRepository` (zie §1.5 voor de laagstructuur) |
| `FunctionApp/Email/IEmailPersistenceRepository.cs` | Interface + enige productie-implementatie `SqlEmailPersistenceRepository`, die op zijn beurt alles doorzet naar de `static` klassen `EmailProcessingRepository`/`LearningMomentRepository` |
| `FunctionApp/Email/EmailBatchFilterService.cs` | Fase-1-voorfilter: eigen mailbox, gecachede uitsluitingslijst |
| `FunctionApp/Email/EmailClassificationService.cs` | Batch-wrapper rond de AI-classificatie, vangt quota-fouten |
| `FunctionApp/Email/EmailReplyPolicyService.cs` | Beslist óf en bouwt het antwoord, markeert verzendintentie vóór het versturen (#716), roept `EmailGraphService.SendReplyAsync` aan |
| `FunctionApp/Email/EmailTemplateService.cs` | DB-templates (`dbo.EmailTemplateInstellingen`) met 5-minuten TTL-cache per `(clubCode, key)`, valt terug op hardcoded defaults, `ApplyPlaceholders` voor `{{key}}`-substitutie |
| `FunctionApp/Email/BerichtAiService.cs` | AI-classificatie; gebruikt al `IChatClient` (regel 11-14) — goed precedent voor provider-abstractie |
| `FunctionApp/Email/BerichtModels.cs` | `VerzoekType`, `BerichtClassificatie`, `InkomendBericht` (kanaal-agnostisch bericht-DTO), `EmailStatus` |
| `FunctionApp/Email/BerichtResponseGenerator.cs` | Bouwt onderwerp+body per classificatietype, met/zonder DB-template-override |
| `FunctionApp/Processing/BerichtPipeline.cs` | Kanaal-agnostische orchestratie tussen classificatie, `PlannerService` en templating |
| `FunctionApp/Email/CleanupEmailVerwerkingFunction.cs` | Wekelijkse AVG-cleanup-timer (roept twee stored procedures aan) |
| `FunctionApp/Email/ClubAppSettingsSnapshot.cs` | Club-specifieke settings-snapshot voor het dry-run-pad van de Email-tester (#677) |
| `FunctionApp/Utilities/EmailSanitizer.cs` | HTML-sanitizing: `StripHtml` (inkomend), `SanitizeHtmlAllowlist`/`BouwVeiligeHtmlBody` (uitgaand), `SanitizeFoutMelding` (logging) |
| `FunctionApp/Utilities/OntvangerParser.cs` | **Nieuw, nog niet gecommit (#765).** Enige plek die een vrije-tekst-ontvangersregel omzet naar een gevalideerde, gededupliceerde lijst e-mailadressen (max 15) |
| `FunctionApp/Admin/AdminTeambegeleidingFunction.cs` | De handmatige "doorsturen"-actie — enige bestaande "gebruiker klikt knop, mail gaat uit"-flow buiten de AI-pipeline |
| `FunctionApp/Admin/EmailTestFunction.cs` | Dry-run classifier/preview — **verstuurt niets** (zie §1.2, correctie op de aanname dat dit een verzendpad is) |
| `FunctionApp/Admin/AdminTemplatesFunction.cs` | CRUD op `dbo.EmailTemplateInstellingen` (`GET/PUT/POST .../reset`) |
| `FunctionApp/Admin/AdminEmailLogFunction.cs` + `Repositories/AdminEmailLogRepository.cs` | Leest `planner.EmailVerwerking`-metadata (nooit body/antwoord), maskeert afzender tot domein |
| `FunctionApp/Admin/AdminUitgeslotenEmailFunction.cs` + `Repositories/AdminUitgeslotenEmailRepository.cs` | CRUD op `dbo.UitgeslotenEmailAdressen` |
| `FunctionApp/Admin/EasyAuthHelper.cs` | Claims-extractie server-side: rol, naam, e-mail, club-code, correlation-id |
| `FunctionApp/Admin/AdminEndpoint.cs` | Herbruikbare wrapper (auth + correlation-scope + DB-wait + 500-fallback) — gebruikt door 2 van de 5 e-mail-gerelateerde admin-endpoints (zie §1.7) |
| `FunctionApp/Program.cs` | DI-registratie van `GraphServiceClient` (app-only credential, regel 13-22) en `IChatClient` |
| `BlazorAdmin/Pages/Teambegeleiding.razor` | GUI voor de doorstuur-actie; bevat inline ontvangersveld + live-validatie (regel 78-115, 351-363) |
| `BlazorAdmin/Pages/EmailTester.razor` | GUI voor de dry-run-tester |
| `BlazorAdmin/Pages/EmailTemplates.razor` | GUI voor template-CRUD + de gedeelde "e-mail voetnoot"-instelling (regel 20-36) |
| `FunctionApp.Tests/Email/TestDoubles/FakeEmailGraphService.cs` | Laat het huidige `IEmailGraphService`-contract zien vanuit testperspectief |

### 1.2 De drie manieren waarop een e-mail vandaag de deur uitgaat (of niet)

> **Correctie op de vooraf aangenomen kaders:** `EmailTestFunction`/`EmailTester.razor` is **geen**
> verzendpad. `EmailTestFunction.DryRun` (`FunctionApp/Admin/EmailTestFunction.cs:34-135`) roept
> nergens `IEmailGraphService` aan — het classificeert, bouwt een planner-response en een
> voorbeeldantwoord, en retourneert dat. De UI zegt het ook expliciet: *"deze test verstuurt niets
> en slaat niets op"* (`BlazorAdmin/Pages/EmailTester.razor:9-13`). Er zijn dus twee échte
> verzendpaden, niet drie.

| Aspect | Automatische AI-reply | Handmatige "doorsturen" (Teambegeleiding) |
|---|---|---|
| Trigger | Timer (`EMAIL_POLL_SCHEDULE`), `EmailProcessorFunction.Run` | HTTP POST, `AdminTeambegeleidingFunction.Doorsturen` |
| Graph-aanroep | `EmailGraphService.SendReplyAsync` (regel 193-256) of `.StuurTeamContactDoorAsync` (regel 271-311) voor `TeamContactOpvragen` | `EmailGraphService.StuurTeamContactDoorAsync` (regel 271-311) |
| `EmailGraphService`-constructie | `new EmailGraphService(graphClient, loggerFactory.CreateLogger<...>())` — `EmailProcessorFunction.cs:231` | `new EmailGraphService(graphClient, loggerFactory.CreateLogger<...>())` — `AdminTeambegeleidingFunction.cs:213` (los, tweede keer dezelfde constructie-boilerplate) |
| Afzender-mailbox | Vaste `GraphMailbox` (systeem, app-only) | Zelfde vaste `GraphMailbox` |
| Sjabloon | `EmailTemplateService` + `BerichtResponseGenerator`, incl. gedeelde `EmailVoetnoot` | Handgebouwde HTML-string met losse `WebUtility.HtmlEncode(...)`-aanroepen per veld (`AdminTeambegeleidingFunction.cs:216-222`) — **geen** `EmailTemplateService`, **geen** gedeelde voetnoot |
| Ontvangerresolutie | Server-side lookup via `PlannerDataAccess.GetTeamleiderContactAsync` (automatische classificatie) | `OntvangerParser.Parse` op het vrije "Email Aan"-veld, met server-side TOP-1-fallback als het veld leeg is (regel 145-203) |
| Opt-out-check | `IEmailPersistenceService.LaadUitgeslotenAdressenAsync` (via DI-testbare laag) | `new SqlEmailPersistenceRepository().GetExcludedEmailAddressesAsync(clubCode)` — **rechtstreeks geïnstantieerd**, buiten de DI-testbare laag om (`AdminTeambegeleidingFunction.cs:155`) |
| Logging | `planner.EmailVerwerking`, rijke statusmachine (`Pogingen`, `VerzendPogingOpUtc`, `IsBeantwoord`, idempotentie) | `planner.EmailVerwerking`, **synthetische** rij met gegenereerd `MessageId` (§1.6) |
| Auth-boilerplate | N.v.t. (timer-trigger, geen `HttpRequest`) | Inline `RequireAdmin` + correlation-scope, **niet** via `AdminEndpoint.ExecuteAsync` |

### 1.3 Sender identity vandaag: één vaste systeem-mailbox

`FunctionApp/Program.cs:13-22` registreert `GraphServiceClient` als singleton met een
`ClientSecretCredential` (app-only, client-credentials flow):

```csharp
var tenantId = Environment.GetEnvironmentVariable("GraphTenantId");
var clientId = Environment.GetEnvironmentVariable("GraphClientId");
var graphAppCredential = Environment.GetEnvironmentVariable("GraphClientSecret");
if (...) builder.Services.AddSingleton(new GraphServiceClient(credential));
```

`EmailGraphService` leest daarnaast de doelmailbox uit een aparte env var (`FunctionApp/Email/EmailGraphService.cs:23-24`):

```csharp
_mailbox = Environment.GetEnvironmentVariable("GraphMailbox")
    ?? throw new InvalidOperationException("GraphMailbox environment variable is niet geconfigureerd");
```

Beide zijn Function App **application settings** (niet `dbo.AppSettings`) — zie
`FunctionApp/local.settings.template.json:12-15` (`GraphTenantId`, `GraphClientId`,
`GraphClientSecret`, `GraphMailbox`). Dit is de "systeem-afzender" uit de kernvraag: één vaste
mailbox, één app-only credential, gebruikt door **beide** bestaande verzendpaden.

`IEmailGraphService` is **niet** in DI geregistreerd — beide call-sites doen zelf
`new EmailGraphService(graphClient, loggerFactory.CreateLogger<EmailGraphService>())` nadat ze de
`GraphServiceClient` uit `context.InstanceServices` hebben opgehaald. Dat is losse, kopieerbare
boilerplate, geen gedeelde registratie.

### 1.4 Wat de server al weet over de ingelogde gebruiker

`FunctionApp/Admin/EasyAuthHelper.cs` haalt vandaag al drie dingen uit de `X-MS-CLIENT-PRINCIPAL`-claims
die Easy Auth injecteert:

- `GetCallerName` (regel 65-80) — claim `name`
- `GetCallerEmail` (regel 86-104) — claim `preferred_username`, `upn` of `email`, met de expliciete
  toelichting *"Uitsluitend voor server-side gebruik ... Nooit in response terugsturen"*
- `GetClubCodeFromRequest` (regel 110-118) — `X-Club-Code`-header met terugval op `dbo.AppSettings`

`GetCallerEmail` wordt vandaag gebruikt als **Reply-To** (niet als afzender) in zowel de handmatige
doorstuur-actie (`AdminTeambegeleidingFunction.cs:142`) als de automatische
`TeamContactOpvragen`-flow. Er bestaat dus al server-side identiteit van de ingelogde gebruiker —
alleen nog niet gebruikt om ook als afzender te versturen.

`BlazorAdmin/Program.cs:27-45` toont de huidige MSAL-configuratie in productie:

```csharp
var apiScope = $"api://{clientId}/Admin.Access";
builder.Services.AddMsalAuthentication(options => {
    ...
    options.ProviderOptions.DefaultAccessTokenScopes.Add(apiScope);   // regel 33
    options.ProviderOptions.LoginMode = "redirect";
    options.UserOptions.RoleClaim = "roles";
})
.AddAccountClaimsPrincipalFactory<CustomUserFactory>();
```

Er wordt **uitsluitend** de eigen API-scope aangevraagd — geen Microsoft Graph-scope. Dit bevestigt
dat "verzenden als mezelf" vandaag geen enkele bouwsteen heeft, noch client-side (geen Graph-scope
in MSAL) noch server-side (geen OBO-code, geen tweede Graph-credential).

### 1.5 Layering-eigenaardigheid: drie lagen boven één static repository

`EmailProcessorFunction` gebruikt `IEmailPersistenceService` (interface, voor testbaarheid), waarvan
de enige implementatie `EmailPersistenceService` (`FunctionApp/Email/EmailPersistenceService.cs:95-185`)
zelf weer een `IEmailPersistenceRepository` injecteert. De enige productie-implementatie daarvan,
`SqlEmailPersistenceRepository` (`FunctionApp/Email/IEmailPersistenceRepository.cs:33-106`), doet op
zijn beurt niets anders dan elke methode doorzetten naar de `internal static class
EmailProcessingRepository` (het echte ADO.NET) of `LearningMomentRepository`. Bijvoorbeeld:

```csharp
// SqlEmailPersistenceRepository (IEmailPersistenceRepository.cs:53-54)
public Task<int> InsertEmailVerwerkingAsync(InkomendBericht email)
    => EmailProcessingRepository.InsertEmailVerwerkingAsync(email);
```

Dit is **geen gedupliceerde bedrijfslogica** — elke laag delegeert correct en voegt niets dubbel
uit. Het is wel drie lagen indirectie voor testbaarheid van precies één call-site
(`EmailProcessorFunction`), waarvan de binnenste twee laagjes puur pass-through zijn. Belangrijker:
`AdminTeambegeleidingFunction` **omzeilt deze hele laag**. Voor de opt-out-check instantieert het
rechtstreeks `new SqlEmailPersistenceRepository()` (`AdminTeambegeleidingFunction.cs:155`), en voor
de audit-insert roept het rechtstreeks de `static EmailProcessingRepository.InsertTeambegeleidingDoorsturenAuditAsync`
aan (regel 232-233) — buiten `IEmailPersistenceService` om. Er zijn dus vandaag al **twee
verschillende toegangspatronen** tot dezelfde onderliggende tabel vanuit twee verschillende
call-sites, geen van beide consistent via de "officiële" testbare interface.

### 1.6 De synthetische MessageId-smell (al gedocumenteerd, nu concreet aangetoond)

`docs/EMAIL-VERWERKING.md:710-717` benoemt dit al expliciet:

> *"Beide schrijven wel naar dezelfde tabel `planner.EmailVerwerking`: de automatische pipeline met
> `VerzoekType = TeamContactOpvragen`, de handmatige actie met `VerzoekType =
> TeambegeleidingDoorsturen` (een synthetische audit-rij, geen echt inkomend bericht — `MessageId`
> is gegenereerd, niet afkomstig van Graph)."*

De code erachter, `EmailProcessingRepository.InsertTeambegeleidingDoorsturenAuditAsync`
(`FunctionApp/Email/EmailProcessingRepository.cs:96-121`):

```csharp
internal static async Task InsertTeambegeleidingDoorsturenAuditAsync(
    string teamNaam, string aanvragerEmail, string ontvangersRegel, string clubCode)
{
    ...
    cmd.Parameters.AddWithValue("@MessageId", $"teambegeleiding-doorsturen-{Guid.NewGuid()}");
    ...
    VALUES (@MessageId, @Afzender, @Onderwerp, SYSUTCDATETIME(), 'TeambegeleidingDoorsturen',
            'AntwoordVerstuurd', 1, @VerstuurdNaar, @ClubCode, 1)
}
```

De reden staat in het commentaar erboven (regel 97-101): dit hergebruikt bewust de bestaande
30-dagen-anonimisering van `sp_CleanupEmailVerwerking` en de bestaande Email-log-pagina, "geen aparte
bewaartermijn nodig". Dat is een begrijpelijke kortetermijnkeuze, maar de tabel is ontworpen voor een
**inkomend-bericht-statusmachine** (`Pogingen`, `VerzendPogingOpUtc`, `IsBeantwoord`,
`ConversationId`, `ReplyOpVerwerkingId` — zie `Database/planner/Tables/EmailVerwerking.sql:1-38`), en
geen van die kolommen heeft een zinnige betekenis voor een eenmalige, door een beheerder
geïnitieerde verzendactie. `AdminTeambegeleidingFunction.Doorsturen` vult ze met neptransparant-
waarden (`'AntwoordVerstuurd'`, `IsBeantwoord=1`, `Pogingen=1`) puur om aan de constraints van de
tabel te voldoen. Dit is precies het "smell"-voorbeeld dat de eigenaar wil laten oplossen: een tabel
die één functie goed doet (AI-idempotentie) wordt oneigenlijk gebruikt als generieke audit-log omdat
er geen andere generieke audit-log bestaat.

### 1.7 Twee verschillende auth-boilerplate-patronen op e-mail-gerelateerde admin-endpoints

`AdminEndpoint.ExecuteAsync` (`FunctionApp/Admin/AdminEndpoint.cs:19-42`) bundelt
`RequireAdmin`-guard, correlation-scope, `WaitForDatabaseAsync` en een uniforme 500-fallback. Het
wordt gebruikt door `AdminEmailLogFunction.Get` en alle drie de methoden van
`AdminUitgeslotenEmailFunction`. Het wordt **niet** gebruikt door `AdminTeambegeleidingFunction`
(3 functies), `EmailTestFunction.DryRun` of `AdminTemplatesFunction` (3 functies) — die vijf
schrijven dezelfde vier regels (`ExtractOrCreateCorrelationId` → `RequireAdmin` → `BeginScope` →
eigen try/catch) telkens opnieuw inline. Geen functioneel probleem vandaag, wel een duidelijk
voorbeeld van hoe makkelijk boilerplate zich vermenigvuldigt zonder een verplicht patroon.

### 1.8 Templates, opt-out en logging — datamodel

| Tabel | Schema-bestand | Rol |
|---|---|---|
| `planner.EmailVerwerking` | `Database/planner/Tables/EmailVerwerking.sql` | Inkomend-bericht-statusmachine: `MessageId` (`UNIQUE`), `Status`, `Pogingen`, `VerzendPogingOpUtc` (#716), `IsBeantwoord` (#718 — overleeft anonimisering, in tegenstelling tot `VerstuurdNaar`), `VerzoekType`, `ClubCode` |
| `dbo.EmailTemplateInstellingen` | `Database/dbo/Tables/EmailTemplateInstellingen.sql` | Per-club DB-override van hardcoded default-templates, uniek op `(TemplateKey, ClubCode)` |
| `dbo.UitgeslotenEmailAdressen` | `Database/dbo/Tables/UitgeslotenEmailAdressen.sql` | Opt-out-lijst, uniek op `(EmailAdres, ClubCode)` |
| `avg.Teambegeleiding` | `Database/avg/Tables/Teambegeleiding.sql` | Bron van teambegeleider-emailadressen: `Team`, `Teamrol`, `Naam`, `Emailadres`, `Telefoonnummer`, `ClubCode`. Gevuld via CSV-import (`AdminTeambegeleidingFunction.Import`), **geen FK** naar andere tabellen — puur een opgeslagen contactenlijst per club |

`sp_CleanupEmailVerwerking` (`Database/planner/System Stored Procedures/sp_CleanupEmailVerwerking.sql:1-72`)
is het bestaande, te repliceren AVG-patroon: rijen tussen 30-90 dagen oud worden geanonimiseerd
(`Afzender`, `Onderwerp`, `VerstuurdNaar`, `EmailBody`, `AntwoordEmail`, `PlannerResponse`,
`GeextraheerdeData`, `FoutMelding` → `NULL`/placeholder), rijen ouder dan 90 dagen worden verwijderd.
`IsBeantwoord` en `VerzendPogingOpUtc` worden bewust **nooit** aangeraakt (regel 17-22) — dat zijn
geen persoonsgegevens en dragen twee harde functionele grenzen (replydetectie,
dubbel-verzend-preventie).

### 1.9 Bestaande testdekking (`FunctionApp.Tests/Email/`)

`EmailBatchFilterServiceTests.cs`, `ReplyPolicyTests.cs`, `BerichtResponseGeneratorClubSettingsTests.cs`,
`BerichtResponseGeneratorVeldTypeTests.cs`, `EmailClassificationServiceTests.cs`,
`EmailHardeningTests.cs`, `EmailIdempotentieTests.cs`, `EmailPersistenceServiceTests.cs`,
`EmailProcessorFunctionTests.cs`, `EmailReplyPolicyServiceTests.cs`, `EmailSanitizerTests.cs`,
`EmailTemplateServiceTests.cs`, `MultiDatumAfsluitzinTests.cs`,
`TestDoubles/{FakeEmailGraphService,FakeEmailPersistenceRepository,RecordingEmailPersistenceService}.cs`.
Plus, nog niet gecommit: `FunctionApp.Tests/Utilities/OntvangerParserTests.cs` — 13 tests die het
volledige gedrag van `OntvangerParser.Parse` vastleggen (naam+adres, kaal adres, `;`/`,`-scheiding,
deduplicatie, max 15, foutmeldingen per ongeldig fragment).

### 1.10 Terzijde: documentatie-gap gevonden tijdens dit onderzoek

`docs/API.md` documenteert vandaag **geen** van de endpoints `/beheer/email-log`,
`/beheer/uitgesloten-emails` of `/beheer/templates` (geverifieerd: geen treffers voor deze routes in
`docs/API.md`), ondanks de notitie in het root-`CLAUDE.md` dat de spec "alle 51 productieroutes"
dekt. Dit is een bestaande gap, losstaand van dit ontwerp, maar relevant voor §6.7: een nieuw
`/api/beheer/email/send`-endpoint moet niet in dezelfde gap verdwijnen.

---

## 2. Probleemanalyse

### 2.1 Wat kost het vandaag om e-mail aan een nieuw scherm toe te voegen?

Om een derde scherm (bijv. "stuur bericht naar alle ouders van een team") e-mail te laten versturen,
moet een developer vandaag zelf:

1. Een nieuwe `HttpTrigger`-functie schrijven met de auth-boilerplate met de hand kopiëren (§1.7 —
   twee patronen om uit te kiezen, geen duidelijke standaard).
2. `GraphServiceClient` uit `context.InstanceServices` halen en zelf `new EmailGraphService(...)`
   construeren (§1.3).
3. Zelf beslissen hoe de ontvangerslijst gevalideerd wordt: `OntvangerParser` hergebruiken (goed) of
   een eigen validatie schrijven (waarschijnlijk, want er is geen hogere-orde-service die parse +
   opt-out-check combineert).
4. Zelf de opt-out-lijst raadplegen — en dan kiezen tussen de DI-testbare `IEmailPersistenceService`
   (niet ontworpen voor hergebruik buiten de AI-pipeline) of, zoals `AdminTeambegeleidingFunction`
   al deed, rechtstreeks `new SqlEmailPersistenceRepository()` (§1.5).
5. Zelf de HTML-body bouwen: `EmailTemplateService` (ontworpen rond de vaste classificatie-keys van
   de AI-pipeline, niet vrij herbruikbaar) of, zoals vandaag, handmatige string-interpolatie met
   losse `HtmlEncode`-aanroepen (§1.2) — zonder de gedeelde `EmailVoetnoot`.
6. Zelf beslissen waar de verzending gelogd wordt: nóg een synthetische rij in
   `planner.EmailVerwerking` (de smell herhalen, §1.6) of een eigen nieuwe tabel (de audit
   versnipperen over meerdere plekken, waardoor de bestaande Email-log-pagina niet meer het volledige
   beeld toont).
7. Zelf een Blazor-formulier bouwen — er bestaat geen herbruikbaar `<EmailComposer>`-component,
   alleen de inline implementatie in `Teambegeleiding.razor`.

Dat zijn zeven zelfstandige beslissingen, waarvan er vandaag voor minstens vier al **twee**
verschillende precedenten in de bestaande code staan (auth-boilerplate, opt-out-toegang,
body-opbouw, logging-doel). Een derde precedent zou de inconsistentie niet oplossen maar
vermeerderen.

### 2.2 Wat gaat er concreet mis bij een vierde verzendpad?

- **Auth-boilerplate**: een derde patroon naast de twee uit §1.7 is waarschijnlijker dan dat iemand
  de bestaande twee eerst opruimt.
- **Opt-out-check kan stilzwijgend ontbreken.** Er is geen enkele plek die een nieuwe schrijver
  dwingt de uitsluitingslijst te raadplegen — `AdminTeambegeleidingFunction` deed het toevallig wél
  (rechtstreeks, §1.5), maar niets in de architectuur verplicht dat voor een vijfde call-site. Een
  vergeten opt-out-check is een AVG-incident, geen stijlfout.
- **Logging vertakt zich verder.** Zonder een generieke tabel is de keuze: nóg een synthetische
  `planner.EmailVerwerking`-rij (de statusmachine-kolommen worden voor steeds meer betekenissen
  misbruikt) óf een geheel nieuwe, ad-hoc tabel per scherm (de Email-log-pagina in de Admin GUI
  toont dan nog maar een deel van "alle e-mailactiviteit").
- **Body-opbouw divergeert verder.** De handmatige doorstuur-actie mist vandaag al de gedeelde
  `EmailVoetnoot` die de automatische pipeline wél toepast (`EmailTemplates.razor:20-23` beschrijft
  'm als *"wordt automatisch onder elke uitgaande e-mail geplaatst"* — dat klopt dus vandaag al niet
  voor het tweede verzendpad). Een derde/vierde pad zou waarschijnlijk een derde stijl van
  HTML-opbouw introduceren.
- **Geen enkel bestaand contract ondersteunt een afzenderkeuze.** `IEmailGraphService.SendReplyAsync`
  en `.StuurTeamContactDoorAsync` versturen altijd via de vaste `GraphMailbox` (§1.3). Om "verzenden
  als mezelf" toe te voegen aan een van de twee bestaande paden zou je het contract van
  `IEmailGraphService` moeten wijzigen — met impact op de andere, ongerelateerde aanroeper. Een
  nieuw scherm zou dus per definitie een derde, incompatibel send-pad nodig hebben tenzij er een
  afzender-parameter in het stabiele contract komt (§3.2).

---

## 3. Doelarchitectuur — de e-mail module

### 3.0 Uitgangspunt: verbouwen, niet herbouwen

`EmailSanitizer`, `OntvangerParser` en `EmailTemplateService` zijn al puur, los en getest — die
worden hergebruikt, niet herschreven. `planner.EmailVerwerking` en de volledige AI-statusmachine
(`EmailProcessorFunction`, `EmailProcessingRepository`, `EmailPersistenceService`,
`EmailReplyPolicyService`, `EmailIdempotentie`, `EmailBatchFilterService`,
`EmailClassificationService`) blijven **ongewijzigd** — ze zijn functioneel compleet voor hun eigen
scope (inkomende post) en hebben geen enkele architecturale reden om te migreren. Alleen de
synthetische-audit-insert (§1.6) verdwijnt eruit.

De namespace `SportlinkFunction.Email` wordt vandaag gebruikt door 16 testbestanden en tientallen
call-sites. Een verhuizing naar een nieuwe top-level map `FunctionApp/EmailModule/` zou een grote,
risicovolle diff zijn voor geen functioneel voordeel. In plaats daarvan: nieuwe submappen ónder het
bestaande `FunctionApp/Email/`.

### 3.1 Nieuwe mapstructuur

```
FunctionApp/Email/
├── Verzending/                        (NIEUW)
│   ├── IEmailVerzendService.cs        — het stabiele verzend-contract
│   ├── EmailVerzendService.cs         — implementatie; wrapt IEmailGraphService
│   ├── EmailVerzendOpdracht.cs        — record: ontvangers, onderwerp, body, afzender, bcc, bijlage
│   ├── AfzenderStrategie.cs           — Systeem | IngelogdeGebruiker(...)
│   └── EmailOorsprong.cs              — enum: AutomatischeAiReply, TeambegeleidingDoorsturen, GenericComposer, ...
├── Ontvangers/                        (NIEUW, bouwt voort op de bestaande OntvangerParser)
│   ├── OntvangerResolutieService.cs   — parse + opt-out-check in één aanroep
│   └── IUitsluitingslijstRepository.cs — verplaatste GetExcludedEmailAddressesAsync-query
├── Logging/                           (NIEUW)
│   ├── IEmailLogService.cs
│   └── EmailLogService.cs             — schrijft naar dbo.EmailLog (§3.4)
├── EmailTemplateService.cs            (ONGEWIJZIGD, blijft hier)
├── EmailGraphService.cs / IEmailGraphService.cs   (ONGEWIJZIGD — interne Graph-adapter, niet meer
│                                        rechtstreeks door nieuwe call-sites aangeroepen)
├── EmailBijlage.cs                    (ONGEWIJZIGD)
└── ... (AI-pipeline-bestanden ONGEWIJZIGD: EmailProcessorFunction, EmailProcessingRepository,
        EmailPersistenceService, BerichtAiService, BerichtResponseGenerator,
        EmailReplyPolicyService, EmailBatchFilterService, EmailClassificationService,
        CleanupEmailVerwerkingFunction)

FunctionApp/Utilities/
├── OntvangerParser.cs                 (ONGEWIJZIGD — puur, blijft hier, wordt aangeroepen door
│                                        Ontvangers/OntvangerResolutieService.cs)
└── EmailSanitizer.cs                  (ONGEWIJZIGD)
```

### 3.2 Eén stabiel verzend-contract

```csharp
namespace SportlinkFunction.Email.Verzending;

public interface IEmailVerzendService
{
    Task<EmailVerzendResultaat> VerstuurAsync(EmailVerzendOpdracht opdracht, ILogger log);
}

public sealed record EmailVerzendOpdracht(
    IReadOnlyList<string> Ontvangers,
    string Onderwerp,
    string Body,                    // platte tekst of eigen-opmaak-HTML — zelfde detectie als
                                     // EmailSanitizer.BouwVeiligeHtmlBody (EmailSanitizer.cs:118-129)
    AfzenderStrategie Afzender,
    EmailOorsprong Oorsprong,
    string ClubCode,
    string? BronScherm = null,      // bijv. "Teambegeleiding", "EmailComposer" — voor dbo.EmailLog
    string? ReplyTo = null,
    IReadOnlyList<string>? Bcc = null,
    EmailBijlage? Bijlage = null,
    string? ConversationId = null);

public abstract record AfzenderStrategie
{
    private AfzenderStrategie() { }
    public sealed record Systeem : AfzenderStrategie;
    public sealed record IngelogdeGebruiker(string GebruikerEmail) : AfzenderStrategie;
}

public enum EmailOorsprong
{
    AutomatischeAiReply,
    TeambegeleidingDoorsturen,
    GenericComposer
    // toekomstige schermen voegen hier een waarde toe — nooit een nieuwe tabel of regex
}

public sealed record EmailVerzendResultaat(bool Verstuurd, string? FoutMelding);
```

`IEmailGraphService` blijft bestaan als interne Graph-SDK-adapter met zijn huidige twee
verzendmethoden, **uitsluitend** voor de AI-pipeline (die al zijn eigen `VerzendPoging`-markers om de
Graph-call heen heeft gebouwd, specifiek voor de idempotentie van herhaalde polls — dat hoort niet
in de nieuwe generieke laag thuis en verandert niet, zie §4 Fase 1). `EmailVerzendService` is een
nieuwe, dunnere adapter ernaast voor **alle nieuwe call-sites**, en:

1. roept `OntvangerResolutieService` aan (parse + opt-out, §3.3);
2. roept `EmailSanitizer.BouwVeiligeHtmlBody` aan op de body;
3. kiest op basis van `AfzenderStrategie` tussen de bestaande app-only `GraphServiceClient`
   (`Systeem`) en — ná §3.5 — een per-gebruiker Graph-aanroep (`IngelogdeGebruiker`);
4. roept `IEmailLogService` aan, vóór én na de verzending (zelfde vroeg-vastleggen-patroon als
   `MarkeerVerzendPogingAsync`/`WisVerzendPogingAsync` in de AI-pipeline, maar generiek).

### 3.3 Ontvanger-resolutielaag

```csharp
public sealed class OntvangerResolutieService(IUitsluitingslijstRepository uitsluitingslijst)
{
    public async Task<OntvangerResolutieResultaat> ResolveAsync(string ruweRegel, string clubCode)
    {
        var parseResultaat = OntvangerParser.Parse(ruweRegel);
        if (!parseResultaat.IsValid)
            return OntvangerResolutieResultaat.Ongeldig(parseResultaat.FoutMelding!);

        var uitgesloten = await uitsluitingslijst.GetExcludedEmailAddressesAsync(clubCode);
        var geweigerd = parseResultaat.Emailadressen.Where(uitgesloten.Contains).ToList();
        return geweigerd.Count > 0
            ? OntvangerResolutieResultaat.Ongeldig($"Op de uitsluitingslijst: {string.Join(", ", geweigerd)}")
            : OntvangerResolutieResultaat.Geldig(parseResultaat.Emailadressen);
    }
}
```

`IUitsluitingslijstRepository` verplaatst de bestaande `GetExcludedEmailAddressesAsync(clubCode)`-query
(vandaag in `SqlEmailPersistenceRepository`, `IEmailPersistenceRepository.cs:8` en `:35-48`) naar een
eigen, kleine interface zodat zowel de AI-pipeline als elk nieuw scherm hem via DI krijgen — in
plaats van dat een admin-functie zelf een `new SqlEmailPersistenceRepository()` opzoekt zoals nu
(`AdminTeambegeleidingFunction.cs:155`). Dit is de bouwsteen die §2.2's "opt-out-check kan
stilzwijgend ontbreken"-risico wegneemt: één plek die parse + uitsluiting combineert, niet twee losse
stappen die een nieuwe call-site apart moet onthouden.

### 3.4 Eén generiek logging-schema — nieuwe tabel, niet `planner.EmailVerwerking`

`planner.EmailVerwerking` blijft **ongewijzigd** en uitsluitend voor de AI-verwerkingsstatusmachine.
De kolommen (`Pogingen`, `VerzendPogingOpUtc`, `IsBeantwoord`, `ReplyOpVerwerkingId`) hebben geen
zinnige waarde voor een simpele uitgaande-mail-log, en de synthetische-MessageId-truc (§1.6) bestaat
precies omdat iemand die tabel toch hergebruikte voor iets dat er functioneel niet in past. In plaats
daarvan een nieuwe tabel (schema hieronder is functioneel leidend; schema-keuze `dbo` vs. `planner`
is een implementatiedetail):

```sql
CREATE TABLE [dbo].[EmailLog] (
    [Id]              INT IDENTITY(1,1) NOT NULL,
    [Richting]        NVARCHAR(20)   NOT NULL,   -- 'Uitgaand' (inkomend blijft planner.EmailVerwerking)
    [Oorsprong]       NVARCHAR(50)   NOT NULL,   -- EmailOorsprong-enum als string
    [BronScherm]      NVARCHAR(100)  NULL,       -- 'Teambegeleiding', 'EmailComposer', ...
    [AfzenderType]    NVARCHAR(20)   NOT NULL,   -- 'Systeem' | 'IngelogdeGebruiker'
    [AfzenderAdres]   NVARCHAR(200)  NULL,       -- alleen gevuld bij IngelogdeGebruiker; persoonsgegeven
    [Onderwerp]       NVARCHAR(500)  NOT NULL,
    [Ontvangers]      NVARCHAR(1000) NULL,       -- persoonsgegeven, zelfde 30-dagenregel als VerstuurdNaar
    [Status]          NVARCHAR(20)   NOT NULL,   -- 'Verstuurd' | 'Mislukt'
    [FoutMelding]     NVARCHAR(1000) NULL,
    [ClubCode]        NVARCHAR(20)   NOT NULL CONSTRAINT [CK_EmailLog_ClubCode] CHECK (LEN([ClubCode]) > 0),
    [mta_inserted]    DATETIME2      NOT NULL CONSTRAINT [DF_EmailLog_Ins] DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_EmailLog] PRIMARY KEY CLUSTERED ([Id] ASC)
);
```

Plus een nieuwe `sp_CleanupEmailLog` die exact hetzelfde 30-dagen-anonimiseren/90-dagen-verwijderen-
patroon toepast als `sp_CleanupEmailVerwerking` (`Database/planner/System Stored
Procedures/sp_CleanupEmailVerwerking.sql:1-72`): geanonimiseerd worden `AfzenderAdres` en
`Ontvangers`; **nooit aangeraakt**: `Status` en `Oorsprong` (geen persoonsgegevens, wel nodig om te
blijven rapporteren "hoeveel e-mails zijn er verstuurd"). Aanroepen vanuit dezelfde wekelijkse timer
als `CleanupEmailVerwerkingFunction` (of een eigen timer met identiek schema).

`AdminEmailLogFunction`/`AdminEmailLogRepository` wordt **uitgebreid**, niet vervangen: een `UNION`
over `planner.EmailVerwerking` (inkomend, AI) en `dbo.EmailLog` (uitgaand, handmatig + generiek) zodat
de bestaande Email-log-pagina in de Admin GUI één plek blijft voor "alle e-mailactiviteit", nu met een
expliciete `Richting`-kolom in plaats van impliciet afgeleid uit `VerzoekType ==
'TeambegeleidingDoorsturen'`.

### 3.5 Herbruikbaar admin-endpoint + Blazor-component

- **Backend:** nieuw `FunctionApp/Admin/AdminEmailFunction.cs` — `POST /api/beheer/email/send`,
  gebruikt `AdminEndpoint.ExecuteAsync` (het bestaande wrapper-patroon uit `AdminEndpoint.cs`, zie
  §1.7 — deze migratie is een goed moment om de twee patronen gelijk te trekken). Body-DTO:
  `{ ontvangers, onderwerp, bericht, bronScherm, afzender: "systeem" | "zelf" }`. Valideert via
  `OntvangerResolutieService`, roept `IEmailVerzendService.VerstuurAsync` aan.
- **Frontend:** nieuw herbruikbaar component `BlazorAdmin/Shared/EmailComposer.razor`, analoog aan
  het bestaande `TimeInput.razor`-patroon (CLAUDE.md, "Tijdinvoer-normalisering — altijd via
  TimeHelper + TimeInput"). Eén component bundelt: het ontvangersveld + live-validatie (het patroon
  dat nu inline in `Teambegeleiding.razor:78-115`/`OnbekendeAdressen()` (regel 351-363) staat) +
  onderwerp/body + verstuur-knop + (na §3.6) een "Van"-dropdown.

Voorbeeld van een toekomstige "stekker"-aanroep vanuit een nieuw scherm:

```razor
@* Nieuw scherm, bijv. BlazorAdmin/Pages/OuderCommunicatie.razor *@
<EmailComposer BronScherm="OuderCommunicatie"
               StandaardOntvangers="@ouderEmailadressen"
               OnVerstuurd="@(() => succesBoodschap = "Verstuurd.")" />
```

Geen nieuwe sanitizing, geen nieuwe ontvangerparser, geen nieuwe logging-tabel, geen nieuwe
auth-boilerplate — het scherm levert alleen de ontvangerslijst en een `BronScherm`-label aan.

### 3.6 Afzenderstrategie — de kernbeslissing

#### Optie A — Server-side On-Behalf-Of (OBO)

1. De bestaande Entra App Registration van de Function App (die vandaag `api://{clientId}/Admin.Access`
   blootstelt, `BlazorAdmin/Program.cs:28`) krijgt een API-permission **Mail.Send (Delegated)**
   richting Microsoft Graph. Volgens de Microsoft Graph permissions reference is hiervoor geen
   tenant-admin-consent verplicht om de permission te *declareren*
   (`AdminConsentRequired: No` voor het delegated `Mail.Send`-scope), maar behandel dit in de praktijk
   altijd als consent-plichtig voor een productie-app.
2. Diezelfde App Registration heeft een **client secret of certificate** nodig (een
   `ConfidentialClientApplication`) om `AcquireTokenOnBehalfOf` te kunnen aanroepen.
3. Backend gebruikt `Microsoft.Identity.Client`
   (`IConfidentialClientApplication.AcquireTokenOnBehalfOf(scopes, new
   UserAssertion(binnenkomendBearerToken))`) om het binnenkomende token in te wisselen voor een
   Graph-token met scope `Mail.Send`.
4. Met dat Graph-token: `graphClient.Me.SendMail.PostAsync(...)` — verstuurt als de ingelogde
   gebruiker, verschijnt in diens eigen Verzonden Items (bevestigd via Microsoft Graph-documentatie
   over `sendMail`-gedrag).

> **Open technisch risico — geverifieerd via Microsoft Learn, nog niet via een spike in dit project:**
> OBO vereist dat het binnenkomende bearer-token (audience `api://{clientId}/Admin.Access`, vandaag
> gevalideerd door Easy Auth) ongewijzigd als `UserAssertion` bij de functiecode aankomt. Easy Auth
> injecteert bevestigd `X-MS-CLIENT-PRINCIPAL` voor elke request met een geldig token
> (`EasyAuthHelper.cs`), maar Microsoft's documentatie over het doorgeven van het ruwe access-token
> (`X-MS-TOKEN-AAD-ACCESS-TOKEN`, zie *"Manage OAuth tokens in Azure App Service"*) beschrijft een
> ANDER scenario: Easy Auth voert daar zelf de login-redirect uit en beheert een eigen token store.
> Dit project gebruikt die flow niet — de SPA doet zelf MSAL en stuurt een kant-en-klaar bearer-token
> mee; Easy Auth valideert alleen ("AllowAnonymous"-modus, zie root-`CLAUDE.md`). Of de originele,
> ruwe `Authorization`-header in die configuratie ongewijzigd bij de Function-code aankomt (nodig als
> OBO-assertion) is in dit project **niet geverifieerd**. **Vereiste spike vóór implementatie:** log
> in een testfunctie de ruwe `Authorization`-header van een binnenkomend admin-request en controleer
> of dat token bruikbaar is als OBO-assertion (`aud`-claim, scope). Dit is exact het soort
> Azure-auth-mechaniek waarvoor het root-`CLAUDE.md` verplicht stelt Microsoft Learn te raadplegen
> vóór implementatie — dat is hierboven gedaan, met als uitkomst "een expliciete verificatiestap
> nodig", geen kant-en-klaar antwoord.

**Kostenimplicatie:** geen. Mail.Send (delegated) valt, net als de huidige app-only permission,
binnen de bestaande M365-licentie — er komt geen nieuwe Azure-resource bij. Wel: een nieuwe Entra
API-permission-configuratie, die volgens CLAUDE.md's regel "Azure Entra setup — verify/configure via
scripts, nooit handmatig" via `scripts/azure/Configure-EntraApp.ps1` moet lopen, niet via handmatige
Portal-clicks. Een nieuwe delegated Graph-permission + een OBO-token-exchange is een nieuwe
aanvalsoppervlakte (tokenlekken, scope-creep) en vereist een expliciete CISO security-review vóór
productie, conform de Security Gate.

#### Optie B — Client-side Graph-scope

Blazor MSAL vraagt een extra scope aan (`https://graph.microsoft.com/Mail.Send`) naast de bestaande
`api://{clientId}/Admin.Access` (`BlazorAdmin/Program.cs:33`). Ofwel roept Blazor zelf rechtstreeks
`graph.microsoft.com/v1.0/me/sendMail` aan met dat token, ofwel geeft het dat Graph-token door aan de
Function App die het doorzet.

**Beoordeling tegen "server is de waarheid" (root-`CLAUDE.md`, Layer 5 leidend):** dit is een
architecturale uitzondering, geen gelijkwaardig alternatief. CLAUDE.md's vijf-lagen-model stelt
expliciet dat de Blazor WASM-client door een aanvaller gemodificeerd kan worden en dat
databescherming daarom nooit op de client mag leunen. Optie B laat Blazor zélf rechtstreeks tegen
Microsoft Graph praten (of een Graph-token doorgeven) buiten `EasyAuthHelper.RequireAdmin()` om.
Concreet risico: een gemanipuleerde WASM-build kan met een geldig Graph-token e-mail versturen als de
ingelogde gebruiker zonder dat de Function App ooit een rol- of AVG-check (opt-out-lijst, logging)
heeft uitgevoerd — de admin-rolcheck beschermt vandaag `/api/beheer/*`, maar niet een rechtstreekse
Blazor→Graph-aanroep. Zelfs "token doorgeven aan de Function App" verandert dit niet wezenlijk: het
token is al buiten de controle van de server verkregen op basis van een client-side scope-aanvraag.

**Advies:** Optie A (server-side OBO) past bij de bestaande architectuur en wordt aanbevolen. Optie B
wordt afgeraden tenzij een toekomstige sessie een concrete, technische reden vindt waarom OBO niet
haalbaar is (bijvoorbeeld als de spike hierboven uitwijst dat het ruwe token niet doorkomt). Mocht dat
gebeuren, is het tussenalternatief niet Optie B maar een hybride: Blazor vraagt het Graph-scope aan
en stuurt dat token als **aparte** header (niet als vervanging van de bestaande API-Bearer-token) naar
de Function App, die het uitsluitend server-side gebruikt als OBO-assertion (`AcquireTokenOnBehalfOf`
met het client-side-verkregen token als assertion). Dat blijft binnen "server is de waarheid" omdat de
Function App nog steeds zelf de Graph-call, de rolcheck en de opt-out-/logging-stappen uitvoert
vóórdat hij het token gebruikt — Blazor levert alleen het token aan, beslist niets.

### 3.7 Wat blijft bewust ongewijzigd (samenvatting)

- `planner.EmailVerwerking` + de volledige AI-statusmachine.
- `EmailSanitizer`, `OntvangerParser`, `EmailTemplateService` — hergebruikt, niet herschreven.
- `IEmailGraphService`/`EmailGraphService` als interne Graph-adapter voor de AI-pipeline.
- De systeem-mailbox (`GraphMailbox`, app-only) als afzender voor alle bestaande automatische flows.

---

## 4. Gefaseerd migratieplan (strangler pattern)

### Fase 0 — Fundament (geen bestaande call-site migreert)

- Nieuwe mappen/bestanden: `Verzending/`, `Ontvangers/`, `Logging/` + interfaces en implementaties.
- Nieuwe tabel `dbo.EmailLog` + `sp_CleanupEmailLog`, toegevoegd via `Database/Script.PostDeployment1.sql`
  conform de bestaande DB-migratieregels (idempotent, `IF NOT EXISTS`-guards zoals elders in dat
  script, en — conform eerdere lessen uit dit project — twee keer lokaal uitvoeren om idempotentie te
  bewijzen vóór de PR).
- Nieuwe unit tests voor `EmailVerzendService`/`OntvangerResolutieService` (nieuw gedrag, niets
  bestaands om te breken).
- **Risico:** laag — niets bestaands wordt aangeraakt.
- **Verificatie:** `dotnet test FunctionApp.Tests/FunctionApp.Tests.csproj` groen; geen regressie
  mogelijk omdat geen bestaande call-site wijzigt.

### Fase 1 — Migreer de handmatige doorstuur-actie (opvolger van #765)

- `AdminTeambegeleidingFunction.Doorsturen` gebruikt `IEmailVerzendService` in plaats van
  rechtstreeks `new EmailGraphService(...)` + `EmailProcessingRepository.InsertTeambegeleidingDoorsturenAuditAsync`.
- De `OntvangerParser`-aanroep verplaatst naar `OntvangerResolutieService`; het parse-gedrag zelf
  verandert niet, dus `FunctionApp.Tests/Utilities/OntvangerParserTests.cs` (13 tests, §1.9) blijft
  ongewijzigd groen.
- Nieuwe rij komt in `dbo.EmailLog` in plaats van de synthetische `MessageId`-rij in
  `planner.EmailVerwerking`.
- `AdminEmailLogFunction`/`AdminEmailLogRepository` breidt uit met de `UNION` (§3.4) zodat de Admin
  GUI-pagina niets mist.
- **Risico:** middel — dit is de enige bestaande call-site met productieverkeer (recent gemerged,
  #765).
- **Verificatie:** bestaande/nieuwe tests voor deze flow + handmatige smoke test via
  `Teambegeleiding.razor` in de lokale verificatielus (root-CLAUDE.md, Stap 2) + controleer dat de
  Email-log-pagina de nieuwe rij toont met `Richting=Uitgaand`, `Oorsprong=TeambegeleidingDoorsturen`.

### Fase 2 — Generiek endpoint + Blazor-component (nog geen nieuw scherm)

- `AdminEmailFunction.cs` (`POST /api/beheer/email/send`) + `EmailComposer.razor`, maar eerst alleen
  gebruikt door `Teambegeleiding.razor` zelf — die migreert van zijn eigen inline
  ontvangersveld/verstuur-logica naar het component. Dat bewijst dat het component werkt vóórdat een
  nieuw scherm het gebruikt.
- **Risico:** middel — `Teambegeleiding.razor`'s bestaande UI-gedrag (Herstel-knop, Kopieer-knop,
  `OnbekendeAdressen`-waarschuwing) moet exact behouden blijven in het component.
- **Verificatie:** browser-rendercheck (root-CLAUDE.md, Stap 2.h) op `/teambegeleiding`, alle
  bestaande knoppen/gedrag handmatig doorlopen vóór en na de migratie vergelijken.

### Fase 3 — Afzenderstrategie (OBO), pas ná de spike uit §3.6

- Alleen starten als de tokenforwarding-vraag (§3.6) beantwoord is.
- Entra-configuratie via `scripts/azure/Configure-EntraApp.ps1` (nieuwe permission + secret), CISO
  security-review, de bestaande 3-user-test (root-CLAUDE.md, defense-in-depth) uitgebreid met een 4e
  scenario: "ingelogde gebruiker kiest 'verzenden als mezelf', Mail.Send-consent nog niet gegeven" →
  moet een nette foutmelding geven, geen crash.
- `EmailComposer.razor` krijgt de "Van"-dropdown, alleen zichtbaar/bruikbaar als de backend meldt dat
  OBO geconfigureerd is (feature-detection via een nieuwe instelling, geen hardcoded aanname).
- **Risico:** hoog — nieuwe auth-mechaniek, nieuwe Entra-permissions, potentiële escalatie conform
  root-CLAUDE.md ("AVG/CISO-blokkade die codekeuze vereist").
- **Verificatie:** volledige 4-scenario-test + Security Gate groen + browser-rendercheck.

### Fase 4 — Opruimen

- Verwijder `InsertTeambegeleidingDoorsturenAuditAsync` (dode code na Fase 1).
- `VerzoekType.TeambegeleidingDoorsturen` blijft als enum-waarde bestaan voor historische rijen tot
  de 90-dagenregel ze opruimt (`sp_CleanupEmailVerwerking`) — geen actieve migratie nodig, wel een
  aantekening in de code dat er geen nieuwe rijen meer bijkomen.
- Update `docs/EMAIL-VERWERKING.md` §"Doorsturen naar de coach" om te verwijzen naar dit document
  in plaats van de nu-historische synthetische-rij-uitleg te herhalen.
- Update `CHANGELOG.md`, `docs/API.md` (voeg ook de vandaag ontbrekende `email-log`/
  `uitgesloten-emails`/`templates`/nieuwe `email/send`-routes toe, zie §1.10),
  `docs/api-standaarden/openapi.yaml` (+ hersynchroniseer `openapi.json`).

---

## 5. Open vragen / CISO+DPO-aandachtspunten

1. **OBO-tokenforwarding (§3.6)** — vereist een spike vóór Fase 3 start.
2. **Consent-beleid van de tenant** voor `Mail.Send` (delegated) — geen technische blocker, wel een
   organisatorische stap die de tenantbeheerder moet zetten.
3. **`dbo.EmailLog.AfzenderAdres` is een nieuw persoonsgegeven-veld** dat vandaag nergens bestaat (de
   systeem-mailbox-flow heeft dit niet nodig). Moet vanaf dag één in de 30-dagen-anonimisering
   (`sp_CleanupEmailLog`) zitten, niet achteraf toegevoegd — een kolom die later "vergeten" wordt in
   de cleanup-SP is precies het soort AVG-gat dat maanden onopgemerkt kan blijven (zie de bestaande
   les over gemaskeerde SQL-fouten bij deploys in het projectgeheugen).
4. **Wat als een gebruiker "verzenden als mezelf" kiest maar geen bruikbare mailbox heeft** (bijv. een
   gedeeld/functioneel account zonder eigen postvak)? Het deployment-model van dit project
   (root-`CLAUDE.md`, "Deployment-model") gaat uit van een klein aantal individuele Entra-gebruikers
   per club, dus dit scenario is waarschijnlijk zeldzaam — maar de UI moet een duidelijke
   foutmelding geven in plaats van een crash of stille mislukking.
5. **Sent Items-gedrag verandert.** Bij Mail.Send (delegated) staat de verstuurde mail in de Sent
   Items van de gebruiker zelf, niet meer (alleen) in de gedeelde systeem-mailbox. De veldplanner/
   coördinator ziet de mail dus niet meer automatisch terug tenzij een BCC daar expliciet naartoe
   blijft gaan — het bestaande `plannerEmailAdres`-BCC-patroon (zie `docs/EMAIL-VERWERKING.md`,
   sectie "Doorsturen naar de coach") moet voor de `IngelogdeGebruiker`-afzenderstrategie behouden
   blijven, anders verliest de club overzicht over uitgaande communicatie.
6. **`dbo.EmailLog` is nieuw** en moet, net als elke andere tabel, bij codereview op de
   multi-club-invarianten uit root-`CLAUDE.md` gecontroleerd worden (ClubCode-discriminator, geen
   club-specifieke fallback-strings).
7. **De bestaande documentatiegap in `docs/API.md`/openapi (§1.10)** moet worden meegenomen zodra
   Fase 2 het nieuwe `email/send`-endpoint toevoegt — anders groeit de gap in plaats van dat hij
   wordt gedicht.

---

## 6. Vervolgstappen

Implementatie loopt via losse child-issues per fase (§4), aan te maken onder epic-issue **#777**
zodra de eigenaar opdracht geeft om te starten. Dit document zelf implementeert niets — het is de
vastgelegde basis waarop die child-issues geschreven worden, zodat een toekomstige sessie de analyse
in §1-2 niet hoeft te herhalen.
