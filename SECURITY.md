# Security protocol

## Regel: bij twijfel gaat er niets naar git

**Een gefaalde of onduidelijke security check betekent: STOP.**  
Geen commit, geen push, geen merge — totdat de oorzaak volledig is onderzocht en opgelost.

Dit is geen aanbeveling. Dit is een harde eis.

---

## Automatische security checks

Bij elke push en bij elke pull request naar `main` worden de volgende checks uitgevoerd:

| Check | Wat wordt gecontroleerd | Blokkeert merge? |
|---|---|---|
| **Secret Detection (gitleaks)** | Wachtwoorden, tokens, API-sleutels, connectiestrings in code en git-geschiedenis | ✅ Ja |
| **PII File Detection** | CSV- en Excel-bestanden die persoonsgegevens kunnen bevatten | ✅ Ja |
| **PII Pattern Scan** | Nederlandse telefoonnummers, persoonlijke e-mailadressen, ledencodesn in code | ✅ Ja |
| **Dependency Vulnerability Scan** | Bekende kwetsbaarheden in gebruikte packages (HIGH/CRITICAL) | Waarschuwing |
| **Security Gate** | Samenvatting: faalt als één van bovenstaande verplichte checks faalt | ✅ Ja |

De **Security Gate** is de poortwachter. Zolang die rood is, is merge geblokkeerd — ook als de andere checks groen zijn.

---

## Wat te doen bij een gefaalde check

### 1. Secret Detection gefaald (gitleaks)

Een wachtwoord, token of sleutel is gevonden in de code of git-geschiedenis.

**Direct te doen — geen uitstel:**
1. **Roteer het secret onmiddellijk** — ga er vanuit dat het al gezien is
   - GitHub PAT: github.com → Settings → Developer settings → Personal access tokens
   - Azure credentials: Azure Portal → App registrations of Key Vault
   - Sportlink wachtwoord: club.sportlink.com → Account instellingen
2. Verwijder het secret uit de code
3. Als het al in git-geschiedenis staat: neem contact op met de repo-eigenaar — de git-geschiedenis moet herschreven worden of de repo moet als gecompromitteerd worden beschouwd
4. Maak een nieuw secret aan en sla het op in 1Password of Azure Key Vault

**Nooit** een secret in plain text in code, comments, of documentatie plaatsen.

### 2. PII File Detection gefaald

Een CSV- of Excel-bestand staat in de repository.

1. Verwijder het bestand uit git tracking: `git rm --cached bestandsnaam.csv`
2. Voeg het toe aan `.gitignore`
3. Controleer of het bestand al gepusht is — als ja, neem contact op met de repo-eigenaar
4. Verwijder het lokale bestand of sla het op buiten de repository

### 3. PII Pattern Scan gefaald

Persoonsgegevens (telefoonnummer, e-mailadres, ledencode) zijn gevonden in getrackte bestanden.

1. Open het genoemde bestand en zoek de gevonden waarde
2. Verwijder of vervang het door een placeholder (bijv. `<TELEFOONNUMMER>`)
3. Controleer of het al gepusht is — als ja, zie stap 3 hierboven

### 4. Check gefaald maar reden onduidelijk

Als een check faalt en de oorzaak niet direct duidelijk is:
- **Ga niet verder** — niet committen, niet pushen
- Bekijk de volledige logs via GitHub Actions
- Vraag om hulp

---

## GitHub branch protection instellen (eenmalig)

Om te garanderen dat de Security Gate altijd actief is, stel je branch protection in op `main`:

1. Ga naar de repository op GitHub
2. Settings → Branches → Add rule
3. Branch name pattern: `main`
4. Vink aan:
   - ✅ **Require status checks to pass before merging**
   - ✅ Zoek op en selecteer: `Security Gate — blokkeert merge bij fout`
   - ✅ **Require branches to be up to date before merging**
   - ✅ **Do not allow bypassing the above settings**
5. Sla op

Hierna is merge naar `main` technisch onmogelijk zolang de Security Gate rood is — ook voor repo-eigenaren.

---

## Wat nooit in git mag

| Type | Voorbeelden |
|---|---|
| Wachtwoorden | Sportlink wachtwoord, database wachtwoorden |
| Tokens en sleutels | GitHub PAT, Azure credentials, API keys |
| Persoonsgegevens | Namen, e-mailadressen, telefoonnummers, geboortedatums |
| Databestanden | `*.csv`, `*.xlsx` met ledendata |
| Verbindingsstrings met credentials | `Server=...;Password=...` |
| Lokale paden met gebruikersnaam | `C:\Users\<naam>\...` |

Bij twijfel: het gaat niet in git.

---

## Voor Claude Code

Bij elke commit of push geldt:

1. **Altijd** de CI-status controleren na een push — nooit rapporteren dat iets klaar is zonder dit te doen
2. Als een check faalt of de status onduidelijk is: **direct melden aan de gebruiker**, nooit stilzwijgend doorgaan
3. Persoonsgegevens, wachtwoorden en tokens worden **nooit** in bestanden geschreven, ook niet tijdelijk
4. Bij twijfel over of iets gevoelig is: behandel het als gevoelig
