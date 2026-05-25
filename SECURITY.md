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

| Type | Voorbeeld | Vervang door |
|---|---|---|
| Azure resource naam | `func-vrc-sportlink` | `func-[clubcode]-sportlink` |
| Azure SWA-URL | `lively-field-03c896603.7.azurestaticapps.net` | `[swa-url].azurestaticapps.net` |
| Azure Tenant ID | `74f2b2fe-a0af-4983-9520-ea3b2ac423fb` | `[TENANT_ID]` |
| Azure Client ID | `75802a92-b3cb-4e98-bd4c-3167ce17d3fe` | `[CLIENT_ID]` |
| SQL server/database naam | `myfreesqldbserver-vrc`, `myFreeDB` | `[sql-servernaam]`, `[database-naam]` |
| Club-domein | `vv-vrc.nl` | `[club-domein]` |
| Clubnaam | `VRC`, `vv-vrc` (buiten documentatiecontext) | `[clubnaam]` |
| E-mailadres lid/medewerker | `trainer@vv-vrc.nl` | `trainer@[club-domein]` |
| Abonnement-ID, resource-group naam | `myAppGroup`, subscription UUID | `[resource-group]` |

### Hoe een security bevinding rapporteren

Beschrijf het **type** probleem en het **bestand + regelnummer** — nooit de echte waarde:

```
FOUT (lekt resource naam):
  "param functionAppName string = 'func-vrc-sportlink'" staat in main.bicep regel 24

GOED (beschrijft het probleem zonder waarde):
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

---

## Achtergrond: wat er in dit project op het spel staat

Deze repository koppelt aan Sportlink Club en verwerkt persoonsgegevens van leden van voetbalverenigingen: namen, e-mailadressen, telefoonnummers en geboortedatums van trainers, leiders en overige stafleden. Dit zijn bijzondere gegevens onder de AVG/GDPR.

Een datalek in deze repository kan betekenen:
- Persoonsgegevens van tientallen tot honderden clubleden komen openbaar op internet
- De vereniging is meldplichtig bij de Autoriteit Persoonsgegevens (binnen 72 uur)
- Reputatieschade voor de vereniging en betrokken personen
- Mogelijk boetes tot 4% van de jaaromzet (AVG artikel 83)

Elke beveiligingsmaatregel in dit document is er om dit te voorkomen.

---

## Beveiligingslagen: meerdere controles, elk onafhankelijk

De beveiliging werkt in lagen. Elke laag is een onafhankelijke blokkade. Als één laag faalt, vangen de andere op — maar alle lagen moeten werken.

### Laag 1 — Lokale git hooks (op de ontwikkelmachine)

Bij elke `git commit` en `git push` draaien automatisch:
- **PII-scan**: zoekt naar telefoonnummers, e-mailadressen en ledencodesn in de staged bestanden
- **Gitleaks** (indien geïnstalleerd): diepere scan op wachtwoorden en tokens

Instellen (eenmalig per machine):
```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

Gitleaks installeren (optioneel maar sterk aanbevolen):
- Windows: `winget install gitleaks`
- macOS: `brew install gitleaks`

### Laag 2 — GitHub Actions (in de cloud, bij elke push)

Bij elke push naar elke branch en bij elke pull request naar `main` of een `v2/*`-branch:

| Check | Wat wordt gecontroleerd | Blokkeert merge? |
|---|---|---|
| **Secret Detection (gitleaks)** | Wachtwoorden, tokens, API-sleutels in code én volledige git-geschiedenis | ✅ Ja |
| **PII File Detection** | CSV- en Excel-bestanden met mogelijke persoonsgegevens | ✅ Ja |
| **PII Pattern Scan** | Nederlandse telefoonnummers, persoonlijke e-mailadressen, ledencodesn | ✅ Ja |
| **Dependency Vulnerability Scan** | Bekende kwetsbaarheden in packages (HIGH/CRITICAL) | Waarschuwing |
| **Security Gate** | Faalt als één van de bovenstaande verplichte checks faalt | ✅ Ja |

De **Security Gate** is de finale poortwachter. Zolang die rood is, is merge naar `main` geblokkeerd.

### Laag 3 — .gitignore (passieve blokkade)

Bepaalde bestandstypen worden nooit getrackt door git, ongeacht wat er gedaan wordt:
- `exports/*.csv` en `exports/*.xlsx` — Sportlink ledenexports
- `FunctionApp/local.settings.json` — lokale verbindingsstrings
- `*.env` — environment-bestanden

### Laag 4 — SQL-database (data at rest)

Persoonsgegevens worden opgeslagen in de lokale SQL Server (`avg.Teambegeleiding`), niet in bestanden. De `avg`-schema is bedoeld voor AVG-beschermde data en de toegang moet beperkt zijn tot bevoegde gebruikers.

### Laag 5 — Azure Function logs / Application Insights (AVG #210)

Persoonsgegevens mogen **nooit** in logs of Application Insights terechtkomen.

**Wat wordt NIET gelogd:**
- E-mailadressen (afzender, ontvanger)
- Onderwerpregels van emails
- Emailinhoud, AI-classificatieresultaten

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

`avg.Teambegeleiding` bevat persoonsgegevens van teambegeleiders. Er is geen automatische verwijdering — de tabel wordt bij elke import volledig vervangen (TRUNCATE + bulk insert). Importeer alleen aan het begin van een nieuw seizoen. Het importscript waarschuwt als de data ouder is dan 90 dagen.

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
   | Database wachtwoord | SQL Server Management Studio → Security → Logins |
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
3. Branch name pattern: `main` (herhaal deze stappen voor `v2/develop`)
4. Vink aan:
   - ✅ **Require a pull request before merging**
   - ✅ **Require status checks to pass before merging**
   - ✅ Zoek op en voeg toe: **`Security Gate — blokkeert merge bij fout`**
   - ✅ **Require branches to be up to date before merging**
   - ✅ **Do not allow bypassing the above settings**
5. Sla op

Hierna is directe push naar `main` (of `v2/develop`) en merge met een rode Security Gate technisch onmogelijk — ook voor repo-eigenaren.

---

## Gedeelde verantwoordelijkheid

Iedereen die aan deze repository werkt — mens of AI-assistent — is verantwoordelijk voor het naleven van dit protocol.

**Voor Claude Code geldt specifiek:**
1. Na elke `git push`: CI-status controleren via `gh pr checks <nr>` of `gh run list` vóór wordt gemeld dat iets klaar is
2. Als een check faalt of de status onduidelijk is: direct stoppen en de gebruiker informeren — nooit stilzwijgend doorgaan
3. Persoonsgegevens, wachtwoorden en tokens worden nooit in bestanden geschreven, ook niet tijdelijk of in commentaar
4. Bij elke twijfel of iets gevoelig is: behandel het als gevoelig en vraag eerst
5. Nooit een merge of push bevestigen zonder geverifieerde groene Security Gate
