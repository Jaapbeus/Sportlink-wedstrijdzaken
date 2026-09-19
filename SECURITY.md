# Security protocol

Dit document beschrijft hoe dit project omgaat met security en de AVG/GDPR. Het is bedoeld voor bijdragers, beheerders en externe partijen die willen weten welke maatregelen er zijn getroffen.

**Kernboodschap:** deze applicatie verwerkt persoonsgegevens van clubleden. We nemen dat serieus. Er zijn meerdere onafhankelijke beveiligingslagen die voorkomen dat gevoelige data in git of online belandt — zowel automatisch (hooks, GitHub Actions, branch protection) als procedureel (protocol voor elke bijdrager).

---

## Een kwetsbaarheid melden

Heb je een beveiligingsprobleem gevonden? Maak geen publiek GitHub Issue aan, maar neem direct contact op via een [privébericht op GitHub](../../security/advisories/new). We streven naar een reactie binnen 48 uur en coördineren disclosure in overleg.

---

## Kernregel: bij twijfel gaat er niets naar git — én niet naar GitHub

**Een gefaalde of onduidelijke security check betekent: STOP.**  
Geen commit, geen push, geen merge, geen issue aanmaken — totdat de oorzaak volledig is onderzocht en opgelost.

Dit is geen aanbeveling. Dit is een harde eis zonder uitzonderingen.

---

## GitHub issues, PR's en comments: even publiek als de code

> **Kritieke regel — meerdere keren geschonden (issues #209, #237, #312 e.a.).**

Een publieke repository maakt **alles** permanent zichtbaar: code, bestanden, issues, issue-comments, PR-titels, PR-bodies, review-comments. Git-history en GitHub-caches zijn niet zomaar te wissen. Externe crawlers (Google, archive.org, security.txt-scanners) indexeren publieke repo's binnen minuten.

### Absoluut verboden in issues, PR's, en comments

| Type | Vervang door |
|---|---|
| Azure resource naam (Function App, SWA, Storage, App Insights) | `func-[clubcode]-sportlink`, `swa-[clubcode]-sportlink`, etc. |
| Azure SWA-URL (uniek subdomain van azurestaticapps.net) | `[swa-url].azurestaticapps.net` |
| Azure Tenant ID (GUID van de Entra ID tenant) | `[TENANT_ID]` |
| Azure Client ID (GUID van de App Registration) | `[CLIENT_ID]` |
| SQL server- of databasenaam | `[sql-servernaam]`, `[database-naam]` |
| Club-domein of e-maildomein | `[club-domein]` |
| Clubnaam of clubcode in technische context | `[clubcode]` |
| E-mailadres van een lid of medewerker | `gebruiker@[club-domein]` |
| Abonnement-ID of resource-group naam | `[resource-group]` |
| Beheerders-loginname | `[beheerder]` |

> **Nooit echte waarden als "voorbeeld" gebruiken** — ook niet om aan te tonen wat fout is. Gebruik
> abstracte beschrijvingen (`een GUID van 8-4-4-4-12 tekens`) in plaats van de echte waarde.

### Hoe een security bevinding rapporteren

Beschrijf het **type** probleem en het **bestand + regelnummer** — nooit de echte waarde:

```
FOUT (lekt resource naam, ook als 'voorbeeld'):
  "parameter functionAppName heeft default 'func-[clubnaam]-sportlink'"

GOED (beschrijft het probleem zonder enige waarde):
  "infrastructure/main.bicep regel 24: parameter functionAppName heeft een hardcoded
  clubnaam als default-waarde. Dit schendt het multi-club principe."
```

### Controleplicht vóór elk gh-commando dat naar GitHub schrijft

Vóór `gh issue create`, `gh issue comment`, `gh pr create`, `gh pr comment`:

```
□ Bevat de tekst een echte resource naam? → vervang door [clubcode] placeholder
□ Bevat de tekst een URL met club-specifieke subdomeinen? → vervang
□ Bevat de tekst een UUID/GUID die een Azure-resource identificeert? → vervang
□ Bevat de tekst een e-mailadres van een lid of medewerker? → vervang
□ Bevat de tekst een database- of servernaam? → vervang
□ Alle checks groen? → pas dan publiceren
```

**Eén twijfel = niet publiceren. Gebruik placeholders en sla de echte waarde op in memory (nooit publiek).**

#### Tweede controle: exploiteerbaarheid — óók als er géén waarde in de tekst staat

De checklist hierboven is **waarde-georiënteerd**: elke vraag luidt "staat er een echte X in de
tekst?". Een tekst kan die zes vragen met zes keer nee beantwoorden en tóch een vindaanwijzing zijn.

Dat is geen theorie. Bij de ronde van september is precies dat gebeurd. Een verzamelissue beschreef
van een bevinding die nog **niet** verholpen was: in welke soort omgeving het spoor te vinden zou
zijn, hoe groot het was, en over welke periode het liep. Geen enkele echte waarde — dus zes keer
"nee" op de checklist hierboven — en toch genoeg om gericht te gaan zoeken voor wie ooit toegang
tot die omgeving krijgt. Het oorspronkelijke reviewissue hield het bewust bij het codepad; de
uitkomst van het besloten onderzoek daarna belandde alsnog publiek.

> **Dit voorbeeld is bewust abstract gehouden zolang de bijbehorende remediatie nog loopt.** Het
> concreet maken zou hier dezelfde fout herhalen, in een bestand dat na merge permanent in de
> git-historie staat — en git-historie is niet met een muisklik te redigeren zoals een
> issue-revisie. Zodra het risico weg is mag het alsnog concreet, precies volgens de vuistregel
> hieronder.

Loop daarom vóór publicatie ook deze drie langs:

```
□ Gaat de tekst over een kwetsbaarheid die nog NIET verholpen is?
  → dan alleen de klasse en het codepad benoemen; status, omvang en tijdvenster horen
    in de besloten notitie
□ Vertelt de tekst iemand WAAR hij moet zoeken — een logstore, een omgeving, een tijdvak,
  een bestandslocatie, een zoekfilter?
  → weglaten, ook als de vindplaats zelf afgeschermd is
□ Is een genoemde credential al geroteerd/ingetrokken?
  → zo nee: publiceer geen enkel detail dat het zoeken vergemakkelijkt, ook geen aantallen
```

**Vuistregel:** een publieke issue beschrijft *wat er in de code mis was en waar het gerepareerd
is*. De bevestigde impact van een nog openstaande bevinding — is het echt gelekt, hoe vaak, waar,
wanneer — is besloten tot het moment dat het risico weg is. Daarna mag het alsnog publiek, want dan
is het historie in plaats van een aanwijzing.

**Verzamelissues zijn hier de risicoplek.** Een issue dat acties bundelt zodat ze opvraagbaar zijn
zonder chatsessie is nuttig — maar het trekt bevindingen uit besloten onderzoek naar een publieke
plek. Zo'n issue mag **verwijzen** naar de besloten notitie; het mag de inhoud ervan niet
herhalen.

**Deze controle geldt óók voor bestanden in een commit, niet alleen voor issue- en PR-tekst.** Het
kopje hierboven zegt "vóór elk gh-commando", en juist daardoor is deze sectie bij het schrijven
ervan zelf de mist in gegaan: de exploiteerbare details werden uit een issue gehaald en vervolgens
ter illustratie in dit bestand gezet. In een bestand is de fout duurder — een issue-revisie
verwijdert de eigenaar in de browser, maar git-historie vereist een history-rewrite met tijdelijk
force-push (zie "Uitzondering: history-rewrite na een leak"). Loop de drie vragen hierboven dus ook
langs vóór een commit die over een openstaande bevinding gaat, inclusief documentatie en
commit-berichten.

- **Actions-logs en build-artefacten van een publieke repository zijn óók publiek.** GitHub drukt
  step-`env:`-waarden en ingevulde `${{ }}`-expressies letterlijk in de joblog af, en maskeert
  alleen secrets. Club-identificerende configuratie (Function App-naam en -URL, SWA-hostname,
  Entra tenant-/client-ID, post-logout-URL) hoort daarom in **GitHub Secrets**, niet in Variables —
  zie `docs/DEVELOPER-SETUP.md` §9.2 (#1204).

### De feedbackwidget publiceert óók naar diezelfde publieke repository (#1205)

De FEEDBACK-knop in de Admin GUI maakt een GitHub-issue aan in deze repository. Alles hierboven
geldt daar onverkort voor — met één verschil: de tekst wordt niet door een ontwikkelaar getypt maar
door een clubbeheerder, in vrije tekst, vaak zonder besef dat GitHub openbaar op internet staat.

**De regexdetectie in `Planner.Shared/Feedback/FeedbackCore.cs` (`BevatPii`) is een vangnet, geen
anonimisering.** Ze draait twee keer — op de verzamelde invoer vóór elke AI-aanroep, en op de
uiteindelijke titel + body vlak vóór de GitHub-write — maar herkent uitsluitend **e-mailadressen en
Nederlandse telefoonnummers**. Expliciet restrisico, dat door geen enkele laag in de code wordt
afgedekt:

| Niet herkend | Waarom het erdoor glipt |
|---|---|
| Namen van personen | Niet van gewone woorden te onderscheiden met een patroon |
| Adressen, woonplaatsen, postcodes | Idem; een postcodepatroon zou vooral vals alarm geven |
| Geboortedata en leeftijden | Een datum is op zichzelf niet identificerend |
| Lidnummers, relatiecodes, andere ID's | Vormvrij en clubafhankelijk |
| Wachtwoorden, tokens, API-sleutels, connectiestrings | Geen vaste vorm; de gitleaks-regels gelden voor bestanden in git, niet voor deze invoer |
| Buitenlandse telefoonnummers | De regex dekt alleen het Nederlandse formaat |

Daarom is de publicatiegrens sinds #1205 **een bewuste handeling van de beheerder**, niet een
automatische controle: `POST /api/feedback/preview` stelt de exacte titel + body samen en geeft die
terug zonder iets aan te maken, de widget toont die letterlijk met de waarschuwing dat GitHub
openbaar op internet staat, en pas een expliciete bevestiging leidt tot `POST /api/feedback/submit`.
De bevestiging stuurt de getoonde AI-velden terug zodat er exact gepubliceerd wordt wat er op het
scherm stond — een tweede AI-aanroep zou andere tekst opleveren en het voorbeeld tot een gok maken.
Dat is veilig omdat beide endpoints achter `RequireAdmin` zitten en dezelfde beheerder via het veld
`Beschrijving` sowieso al willekeurige tekst in de body krijgt. De client wordt op dat punt
desondanks niet vertrouwd: de teruggestuurde velden gaan door **dezelfde sanitizer en dezelfde
lengte- en aantalgrenzen** als alle andere tekst in de body (samenvatting afgekapt, maximaal vijf
acceptatiecriteria), en beide PII-gates draaien onverkort op de uiteindelijke, samengestelde body.

**Regel bij wijzigingen aan dit pad:** maak nooit een route die publiceert zonder dat de beheerder
de uiteindelijke tekst heeft gezien, en presenteer de PII-gate in geen enkel scherm of document als
een garantie dat er geen persoonsgegevens meer in staan.

---

## Achtergrond: wat er in dit project op het spel staat

Deze repository koppelt aan Sportlink Club en verwerkt persoonsgegevens van leden van voetbalverenigingen: namen, e-mailadressen, telefoonnummers en geboortedatums van trainers, leiders en overige stafleden. Dit zijn **gewone persoonsgegevens** in de zin van de AVG (artikel 4 lid 1) — geen bijzondere categorie. Dat maakt ze niet vrijblijvend: ze zijn direct herleidbaar tot een persoon, en twee aspecten verhogen het risico:

- **Gegevens van minderjarigen.** Bij jeugdteams gaat het om kinderen. Overweging 38 van de AVG bepaalt dat kinderen specifieke bescherming verdienen. Dat maakt hun gegevens géén bijzondere categorie, maar het weegt wel zwaarder mee in elke risicobeoordeling — ook bij een datalek.
- **Vrije tekst in e-mails en feedback.** Een afmelding kan een blessure of ziekte noemen; dat is dan wél een gezondheidsgegeven. Dit is niet vooraf te filteren. Daarom de harde regel: **inhoud van berichten nooit loggen, nooit in een issue plakken, nooit publiceren.**

**Bijzondere categorieën (AVG artikel 9)** zijn gegevens over ras of etnische afkomst, politieke opvattingen, religieuze of levensbeschouwelijke overtuigingen, vakbondslidmaatschap, genetische en biometrische gegevens, gezondheid, en seksueel gedrag of seksuele gerichtheid — deze worden **door het ontwerp heen niet verwerkt**; er is geen veld, tabel of scherm voor.
**Strafrechtelijke gegevens (AVG artikel 10)** zijn gegevens over strafbare feiten, veroordelingen en daarmee samenhangende veiligheidsmaatregelen — ook die worden **niet verwerkt**.

Een datalek in deze repository kan betekenen:
- Persoonsgegevens van tientallen tot honderden clubleden komen openbaar op internet
- Reputatieschade voor de vereniging en de betrokken personen
- Een meldings- en documentatieplicht voor de vereniging (zie hieronder)
- In het uiterste geval een boete: AVG artikel 83 lid 5 kent boetes tot € 20 miljoen of 4% van de wereldwijde jaaromzet, waarbij het hoogste bedrag geldt. Voor een vereniging weegt de Autoriteit Persoonsgegevens proportionaliteit mee — omvang, ernst en de getroffen maatregelen tellen.

Elke beveiligingsmaatregel in dit document is er om dit te voorkomen.

### Datalek: risicogestuurde triage

Een datalek is niet automatisch een melding. Wat er moet gebeuren hangt af van het risico voor de betrokkenen. Werk deze stappen in volgorde af:

1. **Feiten vastleggen.** Wat is er gebeurd, wanneer is het ontdekt, welke gegevens zijn geraakt, hoeveel betrokkenen, wat is de oorzaak, en welke containment is al uitgevoerd (secret ingetrokken, repository privé gezet, toegang geblokkeerd).
2. **Risico voor betrokkenen beoordelen.** Aard en gevoeligheid van de gegevens, omvang, herleidbaarheid tot personen, kwetsbare groepen (minderjarigen), en de mogelijke gevolgen — van ongewenste benadering tot identiteitsfraude.
3. **Verantwoordelijke aanwijzen.** De vereniging is verwerkingsverantwoordelijke. Het bestuur of de AVG-contactpersoon neemt het besluit over melden, en dat besluit wordt vastgelegd — ook als de uitkomst "niet melden" is.
4. **Melding aan de Autoriteit Persoonsgegevens (artikel 33).** Zonder onredelijke vertraging en waar mogelijk binnen 72 uur na kennisname, tenzij het niet waarschijnlijk is dat de inbreuk een risico voor de betrokkenen inhoudt. Wordt er later dan 72 uur gemeld, leg dan de reden van de vertraging vast.
5. **Betrokkenen informeren (artikel 34).** Zonder onredelijke vertraging wanneer de inbreuk waarschijnlijk een **hoog** risico voor hen inhoudt. Artikel 34 lid 3 kent drie uitzonderingen: de gegevens zijn onbegrijpelijk gemaakt voor onbevoegden (bijvoorbeeld versleuteld), het hoge risico is inmiddels weggenomen door maatregelen achteraf, of individueel informeren vergt een onevenredige inspanning — dan volgt een openbare mededeling.
6. **Verwerkersrol.** Wie de installatie namens de club beheert — een externe ontwikkelaar of een hostende partij — is verwerker en meldt niet zelf aan de Autoriteit Persoonsgegevens, maar informeert de vereniging zonder onredelijke vertraging (artikel 33 lid 2). Leg dit vast in de verwerkersovereenkomst.
7. **Register bijhouden.** Documenteer elke inbreuk — ook een niet-gemelde — met de feiten, de gevolgen en de getroffen maatregelen (artikel 33 lid 5). Dat register is wat de toezichthouder opvraagt als hij controleert of de afweging klopte.

Bronnen: [AVG (Verordening (EU) 2016/679)](https://eur-lex.europa.eu/eli/reg/2016/679) en de [EDPB Guidelines 9/2022 over datalekmelding](https://www.edpb.europa.eu/documents/guideline/guidelines-92022-on-personal-data-breach-notification-under-gdpr_en).

---

## Beveiligingslagen: meerdere controles, elk onafhankelijk

De beveiliging werkt in lagen. Elke laag is een onafhankelijke blokkade. Als één laag faalt, vangen de andere op — maar alle lagen moeten werken.

### Laag 1 — Lokale git hooks (op de ontwikkelmachine)

Bij elke `git commit` en `git push` draaien automatisch:
- **PII-scan**: zoekt naar telefoonnummers, e-mailadressen en ledencodes in de staged bestanden
- **Gitleaks** (indien geïnstalleerd): diepere scan op wachtwoorden en tokens

Instellen (eenmalig per machine):
```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

Controleer daarna dat de hooks daadwerkelijk draaien:
```bash
ls -l .githooks/pre-commit .githooks/pre-push   # beide moeten uitvoerbaar zijn (x-bit)
git commit --allow-empty -m "hooktest"          # verwacht: de scanmelding van de pre-commit-hook
```
Ontbreekt de x-bit, dan slaat Git de hook **stilzwijgend** over en draait er geen enkele scan —
een laag die faalt zonder signaal. Herstellen:
`git update-index --chmod=+x .githooks/pre-commit .githooks/pre-push`.

Gitleaks installeren (optioneel maar sterk aanbevolen):
- Windows: `winget install gitleaks`
- macOS: `brew install gitleaks`

### Laag 2 — GitHub Actions (in de cloud, bij elke push)

Bij elke push naar elke branch en bij elke pull request naar `main` of `develop`:

| Check | Wat wordt gecontroleerd | Blokkeert merge? |
|---|---|---|
| **Secret Detection (gitleaks)** | Wachtwoorden, tokens, API-sleutels in code én volledige git-geschiedenis | ✅ Ja |
| **PII File Detection** | CSV- en Excel-bestanden met mogelijke persoonsgegevens | ✅ Ja |
| **PII Pattern Scan** | Nederlandse telefoonnummers, persoonlijke e-mailadressen, ledencodes | ✅ Ja |
| **PII in Documentatie (CHANGELOG/docs)** | E-mailadressen in `CHANGELOG.md`, `docs/` en recente commit-berichten | ✅ Ja |
| **Club-infrastructuur patrooncheck** | Azure-resourcenamen, hostnames, tenant-/client-ID's en andere club-identificerende waarden in getrackte bestanden — de check die regel 4a hierboven afdwingt | ✅ Ja |
| **Dependency Vulnerability Scan** | Bekende kwetsbaarheden in NuGet-pakketten (HIGH/CRITICAL), inclusief transitieve dependencies | ✅ Ja |
| **Security Gate** | Faalt als één van de bovenstaande verplichte checks faalt | ✅ Ja |

De **Security Gate** is de finale poortwachter: hij hangt via `needs:` af van precies de zes jobs
hierboven, en zolang hij rood is, is merge naar `main` geblokkeerd.

Een fork kan de infrastructuur-patrooncheck uitbreiden met eigen reguliere expressies via het
optionele GitHub Secret `CLUB_EXTRA_PATTERNS` (newline-gescheiden) — nuttig voor waarden die alleen
jouw club identificeren.

**Op welke events de Security Scan draait (#1202):** `push` naar élke branch, én `pull_request`
naar `main` en naar `develop` — die twee branches staan letterlijk zo in de `on:`-sectie van
`.github/workflows/security-scan.yml`. Beide branches hebben branch protection die de check
`Security Gate — blokkeert merge bij fout` verplicht stelt, dus documentatie, workflow-trigger en
branch protection zeggen bewust alle drie hetzelfde. **De `pull_request`-trigger is niet optioneel
naast de `push`-trigger:** een PR uit een fork levert géén push-event in deze repository op, dus
zonder die trigger zou de verplichte check daar nooit verschijnen (de PR kan dan niet mergen) en
zou de merge-context nooit upstream gescand worden. Wijzigt de branch-strategie of een doelbranch,
werk dan de trigger en deze alinea in dezelfde PR bij.

**Dependency Vulnerability Scan — dekking (#1126):** een kale `.csproj` is voor Trivy geen
ondersteund NuGet-manifest. De job genereert daarom zelf per project een `packages.lock.json`
(`dotnet restore -p:RestorePackagesWithLockFile=true`, inclusief transitieve pakketten) vóórdat
Trivy scant — dit bestand wordt **nooit gecommit** (zie `.gitignore`; Dependabot onderhoudt hier
geen lock-bestanden en een gecommit exemplaar zou stilzwijgend uit de pas lopen met de echte
restore). Twee harde guards bewaken dat de scan nooit meer stilzwijgend leeg draait: vóór Trivy
(alle verwachte lock-bestanden aanwezig en gevuld met `dependencies`) en ná Trivy (de JSON-output
bevat minstens één daadwerkelijk gescand `nuget`-manifest). Zonder deze guards vond de job eerder
`Number of language-specific files num=0` en was de gate groen zonder ooit een pakket te scannen.

### Laag 3 — .gitignore (passieve blokkade)

Bepaalde bestandstypen worden nooit getrackt door git, ongeacht wat er gedaan wordt:
- `*.csv`, `*.xlsx`, `*.xls` — overal in de repo, niet alleen in `exports/` (#978). Enige
  uitzonderingen: seed-bestanden onder `scripts/migrations/` en testfixtures onder een
  `*.Tests/`-project — geen van beide bevat ledendata.
- `**/local.settings.json` — lokale verbindingsstrings van élk projectpad, dus ook
  `FunctionApp.Postgres/local.settings.json` (de productietier)
- `.env` en `.env.*` — environment-bestanden, met uitzondering van `.env.template` en `.env.example`

### Laag 4 — Database (data at rest)

Persoonsgegevens worden opgeslagen in de database van de actieve tier, nooit in bestanden:
`avg.teambegeleiding` en `avg.importlog` op Postgres (de tier die in productie draait, gehost),
`avg.Teambegeleiding` en `avg.ImportLog` op SQL Server. Het `avg`-schema is bedoeld voor
AVG-beschermde data; de toegang moet beperkt zijn tot bevoegde gebruikers.

**Row-Level Security op de Postgres-tier (#1198) — verplicht op élke tabel.** Een gehost
Postgres-platform als Supabase genereert voor elke tabel in het `public`-schema automatisch een
PostgREST-REST-endpoint, bereikbaar met de bewust publieke anon-key, **ongeacht of deze applicatie
die API ooit gebruikt**. Zonder RLS is zo'n tabel dus extern leesbaar, schrijfbaar en verwijderbaar
voor iedereen die de project-URL kent. Migratie
`Database.Postgres/migrations/021_enable_row_level_security.sql` zet RLS aan op alle bestaande
tabellen; `022` en `023` trekken daarnaast de overbodige rechten van de platformrollen `anon` en
`authenticated` in.

Twee regels die hieruit volgen:

1. **Elke nieuwe tabel krijgt in dezelfde migratie een
   `ALTER TABLE <schema>.<tabel> ENABLE ROW LEVEL SECURITY;`.** CI dwingt dit af tegen een levende
   database (`scripts/ci/check-rls-enabled.sh` en `scripts/ci/check-splinter-lints.sh` in de job
   `fresh-db-postgres`).
2. **Bewust geen policies.** De applicatie verbindt via één rol die tabeleigenaar is; die omzeilt
   RLS hoe dan ook. RLS is hier uitsluitend de schakelaar die de PostgREST-rollen buitensluit —
   geen per-rij-autorisatie. Voeg dus geen policies toe zonder expliciete aanleiding.

Controleer bij een gehost platform periodiek het beveiligingsadvies van de provider: configuratie
die je in een dashboard wijzigt, laat geen diff in git achter en is daardoor onzichtbaar voor
codereview.

### Laag 5 — Azure Function logs / Application Insights (AVG #210)

Persoonsgegevens mogen **nooit** in logs of Application Insights terechtkomen.

**Wat wordt NIET gelogd:**
- E-mailadressen (afzender, ontvanger)
- Onderwerpregels van emails
- Emailinhoud, AI-classificatieresultaten
- Sportlink-request-URL's (bevatten de clientId als queryparameter) — log het endpoint en de
  wedstrijdcode, nooit de volledige URL. De CI-job `PII Pattern Scan (AVG/GDPR)` blokkeert een
  logtemplate met een URL-placeholder (#1200).
- Exception-teksten in CI-uitvoer. De uitvoer van een GitHub Actions-job van een publieke
  repository is zelf publiek, en GitHub maskeert alleen de exacte, volledige waarde van een
  secret — niet een deelstring ervan in een foutmelding (een databasefout noemt host, poort of
  gebruikersnaam). Meld daar het exceptietype en de stap, nooit `ex.Message`; zie
  `Database.Postgres/MigratieFoutRapportage.cs`. Dezelfde CI-job blokkeert een
  `Console.Error.WriteLine` met een geïnterpoleerde exception (#1225).

**Wat WEL wordt gelogd:**
- MessageId (technische Graph API identifier, geen PII)
- VerwerkingId (intern rowId)
- Status en foutmeldingen zonder PII

**Application Insights retentie:** stel in op **30 dagen** via Azure Portal → Application Insights → Usage and estimated costs → Data retention. Standaard is 90 dagen.

---

### Laag 6 — Automatische AVG-retentie (AVG #208)

`planner.EmailVerwerking` bevat emailinhoud en afzendergegevens van clubleden.

| Fase | Wanneer | Actie |
|---|---|---|
| Anonimiseren | 30–90 dagen na ontvangst | Afzender/Onderwerp → `[geanonimiseerd]`, EmailBody/AntwoordEmail/PlannerResponse/GeextraheerdeData → NULL |
| Verwijderen | > 90 dagen na ontvangst | Hele rij verwijderd |

De cleanup wordt wekelijks (zondagochtend 03:00 UTC) uitgevoerd door `CleanupEmailVerwerkingFunction`. De stored procedure `planner.sp_CleanupEmailVerwerking` is idempotent.

`avg.Teambegeleiding` bevat persoonsgegevens van teambegeleiders. De rijen van de club worden bij
elke import volledig vervangen (club-scoped DELETE + insert, nooit een TRUNCATE — dat zou andere
clubs' rijen ook wissen; #1131/#1132 maakten dit atomisch per import en, op de Postgres-tier,
geserialiseerd per club).

`avg.ImportLog` legt per import vast wie hem uitvoerde (`ImporterendeDoor`, een Entra-gebruikersnaam)
en welk bestand daarbij hoorde (`CsvBestand`, kan herleidbare informatie bevatten). Beide zijn
persoonsgegevens.

Daarnaast geldt voor beide tabellen een automatische retentie:

| Tabel | Fase | Wanneer | Actie |
|---|---|---|---|
| `avg.Teambegeleiding` | Verwijderen | > 1 jaar na de import (`mta_imported` / `ImportDatum`) | Hele rij verwijderd |
| `avg.ImportLog` | Anonimiseren | > 90 dagen na de import | `ImporterendeDoor` en `CsvBestand` → NULL |
| `avg.ImportLog` | Verwijderen | > 1 jaar na de import | Hele rij verwijderd |

De cleanup draait maandelijks (1e van de maand, 04:00 UTC) via `CleanupTeambegeleidingFunction`, op
beide tiers en met dezelfde termijnen; de Postgres-variant gebruikt
`PostgresCleanupProcedures.CleanupTeambegeleidingAsync` / `CleanupImportLogAsync`, de SQL
Server-variant de stored procedures `avg.sp_CleanupTeambegeleiding` en `avg.sp_CleanupImportLog`.
Beide zijn idempotent.

Importeer alleen aan het begin van een nieuw seizoen. Het importscript waarschuwt als de data ouder
is dan 90 dagen.

`dbo.AppSettingsAudit` bevat een auditlog van elke instellingenwijziging (#781). `GewijzigdDoor` is
een Entra-gebruikersnaam/UPN; `OudeWaarde`/`NieuweWaarde` kunnen e-mailadressen bevatten (bijv. bij
`GraphMailbox` of `EmailReviewRecipient`). Sinds #1003 wordt `GewijzigdDoor` uitsluitend server-side
uit de door Easy Auth gevalideerde claim van de aanroeper bepaald — nooit uit de request-body of een
querystring-parameter — zodat een beheerder de attributie niet zelf kan kiezen.

| Fase | Wanneer | Actie |
|---|---|---|
| Verwijderen | > bewaartermijn (default 730 dagen / 24 maanden) na de wijziging | Hele rij verwijderd |

Bewust géén anonimiseer-fase: het doel van dit log ís "wie heeft wat gewijzigd", dus een
tussentijdse anonimisering van `GewijzigdDoor` zou die traceerbaarheid ondermijnen zonder het
AVG-risico wezenlijk te verkleinen — de tabel is toch al uitsluitend inzichtelijk voor beheerders
via SQL. De bewaartermijn van 730 dagen is een **gedocumenteerd uitgangspunt, geen definitief
beleid** — de repo-eigenaar kan dit aanpassen via `dbo.AppSettings.AppSettingsAuditBewaarDagen`
zonder redeploy. De cleanup wordt maandelijks (1e van de maand, 04:30 UTC) uitgevoerd door
`CleanupAppSettingsAuditFunction`. De stored procedure `dbo.sp_CleanupAppSettingsAudit` is
idempotent.

### Sportlink-mutatie-audit (`SportlinkMutationAudit`, #1114)

`dbo.SportlinkMutationAudit` / `public.sportlinkmutationaudit` legt bij elke Sportlink-mutatiepoging
vanuit deze app (kleedkamers, veld, wijzigingsverzoek goed-/afkeuren, epic #986) een rij vast.
`TriggerdDoor` is het e-mailadres/UPN van de beheerder die de actie triggerde — server-side bepaald
uit de Easy Auth-claim, nooit uit client-input — en daarmee een persoonsgegeven.

| Fase | Wanneer | Actie |
|---|---|---|
| Verwijderen | > bewaartermijn (default 365 dagen / één seizoen plus marge) na de poging | Hele rij verwijderd |

Zelfde enkele-fase-aanpak en dezelfde redenering als `AppSettingsAudit` hierboven. De default van
365 dagen is een **gedocumenteerd uitgangspunt, geen definitief beleid** — korter dan de 730 van
`AppSettingsAudit` omdat dit log per mutatie groeit en Sportlinks eigen log de mutatie óók bewaart.
De eigenaar (DPO-rol) stelt de termijn vast via `AppSettings.SportlinkMutationAuditBewaarDagen`
(Postgres: `appsettings.sportlinkmutationauditbewaardagen`, migratie 017) zonder redeploy. De
cleanup draait maandelijks (1e van de maand, 04:45 UTC) via `CleanupSportlinkMutationAuditFunction`
op beide tiers; `dbo.sp_CleanupSportlinkMutationAudit` is idempotent.


### Sportlink-extensierollen (`SportlinkExtensieRollen`) — bewust geen bewaartermijn (#1122)

`laatstgekoppelddoor` (UPN van de beheerder) en `sportlinkaccountnaam` in deze tabel zijn
persoonsgegevens, maar de tabel is een **actuele-toestand-record** (één rij per rol per club, bij
elke registratie overschreven), geen groeiend log. Het gegeven "wie heeft deze koppeling voor het
laatst gelegd" is nodig zolang de koppeling bestaat — een bewaartermijn zou het precies dan
verwijderen. Dataminimalisatie is gewaarborgd door de vorm (geen historie); geen opschoning nodig.
Herzien zodra er een "koppeling verwijderen"-functie komt: dan hoort de rij mee te verdwijnen.

### Dry-run van de Sportlink Web Extension is fail-safe (#1122)

De club-instelling `sportlinkDryRun` wordt op alle plekken gelezen als "alles behalve een expliciete
`0` is dry-run". Tot #1122 gebruikte de mutatieclient `== "1"`, waardoor een nog niet geladen
instellingencache (`null`) een bevestigde mutatie écht liet versturen terwijl het statuspaneel
"dry-run aan" toonde. Beide polariteiten zijn nu gelijk; de bewaking hiervan zit in de
statussectie (`SportlinkExtensieHealthFunction`) en de client-registratie in `Program.cs`.

---

## Wat te doen bij een gefaalde check

### Secret Detection gefaald (gitleaks)

Een wachtwoord, token of sleutel is gevonden in code of git-geschiedenis.

**Direct handelen — geen uitstel:**

1. **Roteer het secret onmiddellijk** — ga er van uit dat het al gezien is, ook als de push net gebeurd is

   | Secret type | Waar roteren |
   |---|---|
   | GitHub PAT | github.com → Settings → Developer settings → Personal access tokens |
   | Azure credentials | Azure Portal → App registrations of Key Vault |
   | Sportlink wachtwoord | club.sportlink.com → Accountinstellingen |
   | Database wachtwoord (Postgres-tier) | Dashboard van je provider (bijv. Supabase → Project Settings → Database → Reset database password). Werk daarna **zowel** het GitHub Secret `POSTGRES_CONNECTION_STRING` **als** de gelijknamige Function App-instelling bij |
| Database wachtwoord (SQL Server-tier) | SQL Server Management Studio → Security → Logins; daarna het Secret `SQL_CONNECTION_STRING` en de Function App-instelling `SqlConnectionString` bijwerken |
   | 1Password TOTP-seed | Verwijder en herregistreer 2FA in de betreffende applicatie |

2. Verwijder het secret uit de code en vervang door een omgevingsvariabele of Key Vault-referentie

3. Als het secret al in git-geschiedenis staat (al gepusht):
   - De git-geschiedenis moet herschreven worden (`git filter-branch` of BFG Repo Cleaner)
   - Of de repository moet als gecompromitteerd worden beschouwd en opnieuw worden opgezet
   - Neem altijd contact op met de repo-eigenaar — dit is niet iets om zelf stil op te lossen

4. Maak een nieuw secret aan en sla het op in een wachtwoordmanager (bijv. 1Password, Bitwarden) of **Azure Key Vault** — nooit in plain text op schijf of in git

**Wat nooit mag:** een secret in plain text in code, commentaar, commit-bericht, of documentatie plaatsen — ook niet tijdelijk.

### PII File Detection gefaald

Een CSV- of Excel-bestand staat in de repository.

1. Verwijder het bestand uit git-tracking (maar bewaar het lokaal):
   ```bash
   git rm --cached exports/bestandsnaam.csv
   ```
2. Voeg het toe aan `.gitignore`
3. Als het bestand al gepusht is: de bestandsinhoud staat in de git-geschiedenis en is zichtbaar voor iedereen met toegang tot de repo. Zie stap 3 hierboven.
4. Sla persoonsgegevens uitsluitend op in de beveiligde SQL-database (`avg.Teambegeleiding`)

### PII Pattern Scan gefaald

Persoonsgegevens zijn gevonden in getrackte bestanden (telefoonnummer, e-mailadres, ledencode).

1. Open het genoemde bestand en zoek de exacte waarde
2. Vervang door een placeholder: `<TELEFOONNUMMER>`, `<EMAIL>`, `<LEDENCODE>`
3. Als het al gepusht is: zie "als het secret al in git-geschiedenis staat" hierboven
4. Controleer of vergelijkbare waarden ook in andere bestanden staan

### Check gefaald maar reden onduidelijk

Als een check faalt en de oorzaak niet direct duidelijk is:
- **Ga niet verder** — niet committen, niet pushen, geen merge
- Bekijk de volledige logs via GitHub Actions (niet alleen de samenvatting)
- Vraag om hulp — onduidelijkheid = stop

---

## Wat nooit in git mag

| Categorie | Voorbeelden | Alternatief |
|---|---|---|
| Wachtwoorden | Sportlink, database, Azure | 1Password of Azure Key Vault |
| Tokens en sleutels | GitHub PAT, Azure credentials, API keys | GitHub Secrets of Key Vault |
| Persoonsgegevens | Namen, e-mails, telefoonnummers, geboortedatums | SQL-database (`avg` schema) |
| Databestanden | `*.csv`, `*.xlsx` met ledendata | Lokaal of in SQL-database |
| Verbindingsstrings met credentials | `Server=...;Password=...` | `local.settings.json` (in .gitignore) |
| Lokale paden met gebruikersnaam | `C:\Users\<naam>\...` | Relatieve paden of omgevingsvariabelen |
| TOTP-seeds | `otpauth://totp/...?secret=...` | Alleen in authenticator-app of 1Password |

Bij twijfel: het gaat niet in git.

---

## GitHub branch protection instellen (eenmalig, verplicht)

Om te garanderen dat de Security Gate altijd actief is en niet omzeild kan worden:

1. Ga naar de repository op GitHub
2. **Settings → Branches → Add branch protection rule**
3. Branch name pattern: `main` (herhaal deze stappen voor `develop`)
4. Vink aan:
   - ✅ **Require a pull request before merging**
   - ✅ **Require status checks to pass before merging**
   - ✅ Zoek op en voeg toe: **`Security Gate — blokkeert merge bij fout`**
   - ✅ **Require branches to be up to date before merging**
   - ✅ **Do not allow bypassing the above settings**
5. Laat onder *"Rules applied to everyone including administrators"* **"Allow force pushes" UIT** —
   ook de variant *"Specify who can force push"* met één persoon erin. Zie hieronder waarom dat
   laatste juist de gevaarlijke stand is.
6. Sla op

Hierna kan een merge met een rode Security Gate niet doorgaan, en is een directe push naar `main`
of `develop` geblokkeerd door de verplichte status check — ook voor repo-eigenaren, mits
`enforce_admins` aan staat **en** force pushes uit staan.

### Force pushes verifiëren — niet met één API (#654)

> **Waarom dit een eigen kopje heeft.** Bij #654 leken twee GitHub-API's elkaar tegen te spreken over
> deze instelling op `main`. Er is meer dan een dag aan uitgezocht wélke API loog. Geen van beide:
> ze beantwoorden een verschillende vraag, en niemand had het derde veld opgevraagd. Ondertussen kon
> één account de productiegeschiedenis herschrijven zonder dat een controle dat zichtbaar maakte.

De UI kent drie standen, en twee API-velden dekken die samen pas volledig:

| UI-stand | REST `allow_force_pushes.enabled` | GraphQL `allowsForcePushes` | GraphQL `bypassForcePushAllowances` |
|---|---|---|---|
| Uit (gewenst) | `false` | `false` | `0` |
| Aan → *Everyone* | `true` | `true` | `0` |
| Aan → *Specify who can force push* | `true` | **`false`** ⚠️ | **> 0** |

De onderste rij is de valkuil: **GraphQL `allowsForcePushes` meldt daar `false`** terwijl force
pushen wél kan. Wie alleen dat veld leest, concludeert onterecht "geblokkeerd" — een fout-negatief,
precies de gevaarlijke kant op. De allowlist is **niet** via de REST-API op te vragen; er is geen
veld en geen sub-endpoint voor. (`/protection/restrictions` lijkt het, maar is de gewone
push-restrictie — een andere instelling.)

Controleer daarom altijd **beide** GraphQL-velden:

```bash
gh api graphql -f query='
{ repository(owner: "OWNER", name: "REPO") {
    branchProtectionRules(first: 50) { nodes {
      pattern
      allowsForcePushes
      bypassForcePushAllowances(first: 100) {
        totalCount
        nodes { actor { __typename ... on User { login } ... on Team { slug } ... on App { slug } } }
      }
    } } } }'
```

**Interpretatie:** force pushen is mogelijk als `allowsForcePushes == true` (iedereen met
push-rechten) **óf** `totalCount > 0` (de genoemde actors). Alleen als beide leeg/false zijn kan
niemand het. Let op `pattern`: meerdere regels kunnen dezelfde branch raken, en een verweesde regel
(0 matching refs) telt niet mee — `matchingRefs { totalCount }` verraadt die.

Rulesets zijn een **onafhankelijke tweede bron** en vervangen deze check niet:

```bash
gh api /repos/OWNER/REPO/rules/branches/main   # zoek naar de rule 'non_fast_forward'
```

Dit endpoint dekt géén klassieke branch protection — het geeft een lege lijst terug op een branch die
wél beschermd is. Beide checks zijn dus nodig.

**Uitzetten doe je via GraphQL, niet via de REST-PUT:**

```bash
# 1. regel-id ophalen
gh api graphql -f query='{ repository(owner:"OWNER",name:"REPO"){ branchProtectionRules(first:50){ nodes{ id pattern } } } }'

# 2. uitzetten EN de allowlist expliciet legen
gh api graphql -f query='
mutation($id: ID!) {
  updateBranchProtectionRule(input: {
    branchProtectionRuleId: $id, allowsForcePushes: false, bypassForcePushActorIds: []
  }) { branchProtectionRule { allowsForcePushes bypassForcePushAllowances(first:10){ totalCount } } }
}' -F id=BPR_xxxxx
```

Leeg de allowlist **expliciet**: of `allowsForcePushes: false` hem zelf opruimt is nergens
gedocumenteerd. De GraphQL-mutatie is een *gedeeltelijke* update — weggelaten velden blijven staan.

> ⚠️ **Gebruik `PUT /repos/{o}/{r}/branches/{b}/protection` hier niet.** Dat endpoint is een
> **volledige vervanging** met vier verplichte, nullable velden. Een PUT met alleen
> `allow_force_pushes: false` faalt (422); zet je de rest op `null` om hem geldig te maken, dan wis je
> `required_status_checks`, `enforce_admins`, `required_pull_request_reviews` en `restrictions` — de
> Security Gate-verplichting op `main` verdwijnt dan zonder waarschuwing en zonder dat de
> responsestatus daar iets over zegt.

### Uitzondering: history-rewrite na een leak

`scripts/security/Clean-GitHistory.ps1` (git-history scrubben na een gepusht secret of PII) eindigt
met `git push --force-with-lease --all`, en daar zit `main` bij. Met force pushes uit is die stap
geblokkeerd — dat is de bedoeling.

Doet dat scenario zich voor, dan zet een beheerder force pushes op `main` **tijdelijk** aan, voert de
scrub uit, en zet ze **direct daarna weer uit** met de mutatie hierboven. Dat is geen omweg maar het
punt van deze instelling: een history-rewrite op productie moet een bewuste, zichtbare handeling zijn
en nooit iets wat per ongeluk kan gebeuren. Een repo-beheerder wordt hierdoor dus nergens
buitengesloten.

---

## Codereview-checklist: geleerde kwetsbaarheidspatronen (securitybatch #1003–#1011, 2026-09-05)

Een CISO-review vond acht kwetsbaarheden die stuk voor stuk een **patroon** zijn, geen incident.
Regressietests per fix voorkomen dat precies díe bug terugkomt; deze checklist voorkomt dat
hetzelfde soort fout ergens anders opnieuw wordt geïntroduceerd. Loop deze door bij codereview
wanneer nieuwe code in een van deze categorieën valt:

| # | Patroon | Regel | Voorbeeld van de fix |
|---|---|---|---|
| 1 | **Identiteits-/auditvelden** | Wie-heeft-dit-gedaan komt altijd uit de server-gevalideerde Easy Auth-claim, nooit uit request-body of querystring. Een client mag nooit zelf claimen wie hij is. | `EasyAuthHelper.GetAuditActor()` (#1003) |
| 2 | **Databaseverbindingen (TLS)** | Een niet-lokale (productie/staging) connectiestring moet certificaat + hostnaam valideren (`VerifyFull`/`verify-full`). Geen normalisatiestap mag dat stilzwijgend afzwakken naar `Require`, ook niet als de aanroeper het expliciet anders vroeg. | `PostgresConnectionStringNormalizer` (#1004) |
| 3 | **PII-/secretscans vóór een externe call** | Een privacy- of secretcheck controleert de **volledige, uiteindelijke** payload (alle velden, inclusief AI-output) **vlak vóór** de externe aanroep — nooit een deelverzameling, en nooit vóór de payload nog aangevuld wordt. | Feedback-PII-gate (#1006) |
| 4 | **Server-side HTTP-aanroepen naar een (deels) door de gebruiker bepaalde URL** | `AllowAutoRedirect=false` + iedere hop opnieuw valideren; resolve zelf en verbind met dat exacte IP (voorkomt DNS-rebinding); weiger private/loopback/link-local/CGNAT-bestemmingen en niet-standaardpoorten. Een allowlist die alleen de eerste URL checkt is geen SSRF-bescherming. | `SsrfProtection` (#1007) |
| 5 | **Publieke foutrapportage** | Nooit vrije `ex.Message`/stacktrace/inner-exceptietekst in een publiek issue/comment. Alleen een vaste allowlist van velden (categorie, exceptietype, fingerprint, tijdstip). Denylist-sanitizers missen per definitie nieuwe foutvormen. | `GitHubIssueReporter` (#1008) |
| 6 | **CI-workflows met productiecredentials** | Een `pull_request`-workflow draait met PR-inhoud (YAML én scripts) — geef zo'n workflow nooit bruikbare productiesecrets. Een privileged stap hoort in een `workflow_run`/protected-environment-context die zijn eigen YAML en bestanden van een vertrouwde ref (main) haalt. | `pre-release-db-check.yml` (#1009) |
| 7 | **Dynamische waarden in gegenereerde HTML** | Alles wat in HTML-tekst of een attribuut terechtkomt wordt context-specifiek geëncodeerd (tekst vs. attribuut), ook als de bron "intern" (de eigen database) lijkt. String-interpolatie zonder encoding is per definitie een injectierisico. | `PlannerHtmlGenerator` (#1010) |
| 8 | **Externe GitHub Actions** | Altijd pinnen op een geverifieerde volledige commit-SHA (met de bedoelde versie als commentaar), nooit op een tag of branch (`@v3`, `@master`) — die zijn wijzigbaar door de upstream-maintainer zonder review hier. | Alle `.github/workflows/*.yml` (#1011) |

**Bij een nieuwe PR:** als de wijziging in een van deze acht categorieën valt, verifieer expliciet
dat het bovenstaande patroon gevolgd wordt — niet alleen dat de functionaliteit werkt. Zie de
individuele issues (#1003, #1004, #1006, #1007, #1008, #1009, #1010, #1011) voor de volledige
analyse, reproductie en acceptatiecriteria per bevinding.

---

## Gedeelde verantwoordelijkheid

Iedereen die aan deze repository werkt — mens of AI-assistent — is verantwoordelijk voor het naleven van dit protocol.

**Voor Claude Code geldt specifiek:**
1. Na elke `git push`: CI-status controleren via `gh pr checks <nr>` of `gh run list` vóór wordt gemeld dat iets klaar is
2. Als een check faalt of de status onduidelijk is: direct stoppen en de gebruiker informeren — nooit stilzwijgend doorgaan
3. Persoonsgegevens, wachtwoorden en tokens worden nooit in bestanden geschreven, ook niet tijdelijk of in commentaar
4. Bij elke twijfel of iets gevoelig is: behandel het als gevoelig en vraag eerst
5. Nooit een merge of push bevestigen zonder geverifieerde groene Security Gate
