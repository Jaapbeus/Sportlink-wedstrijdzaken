# Handleiding: Teambegeleiding export uit Sportlink Club

> **Voor wie is dit document?** Voor de beheerder van de vereniging die deze lijst bijhoudt. Je
> hebt hier geen technische kennis voor nodig. Helemaal onderaan staat een blok **Technische
> achtergrond**; dat hoef je niet te lezen om de import uit te voeren.

Deze handleiding legt stap voor stap uit hoe je de lijst met teambegeleiders (trainers, leiders, coaches) exporteert uit Sportlink Club en importeert in de beheeromgeving.

De import werkt hetzelfde, ongeacht welke database jouw club gebruikt.

---

## Wat heb je nodig?

- Toegang tot [club.sportlink.com](https://club.sportlink.com) met een beheerdersaccount
- Je gebruikersnaam en wachtwoord voor Sportlink

---

## Stap 1 — Inloggen op Sportlink Club

1. Ga naar [https://club.sportlink.com/member/search](https://club.sportlink.com/member/search)
2. Je wordt doorgestuurd naar de loginpagina
3. Vul je **e-mailadres** en **wachtwoord** in
4. Klik op **Inloggen**
5. Als er een verificatiecode gevraagd wordt, vul deze dan in (je ontvangt die via de authenticator-app)

> Als je al ingelogd bent, ga je direct naar stap 2.

---

## Stap 2 — Filter instellen: alleen teambegeleiding

We filteren zodat alleen begeleiders zichtbaar zijn en geen spelers.

1. Ga naar **Personen**

2. Kies **Teams**

3. Klik bij **Bondsteam** op **alles selecteren**

   > Deze stap wordt gemakkelijk overgeslagen. Zonder deze selectie komen ook lokale (niet-bonds)teams
   > in de export terecht, waardoor de lijst ruis bevat.

4. Klik bij **Rol binnen het team** op **alles selecteren** — nu staan alle rollen aangevinkt

5. Verwijder het vinkje bij de volgende vier rollen door er één voor één op te klikken:

   - **Teamspeler / Aanvaller**
   - **Teamspeler / Keeper**
   - **Teamspeler / Middenvelder**
   - **Teamspeler / Verdediger**

   > Controleer dat deze vier rollen **niet** aangevinkt zijn. Alle andere rollen zoals trainer, leider en coach mogen aangevinkt blijven.

6. Klik op de knop **Zoeken**

7. Wacht even — dit duurt soms 5 tot 10 seconden. Je ziet daarna het resultaat verschijnen met het aantal gevonden personen.

---

## Stap 3 — De lijst exporteren

1. Kijk in de grijze balk direct boven de lijst met resultaten. Aan de rechterkant van die balk staan een paar kleine icoontjes.

2. Klik op het **exporteer-icoontje** — dit ziet eruit als een tabel met een pijl naar beneden.

3. Er verschijnt een klein venster. Klik daarin op **Download**.

4. Het bestand wordt nu gedownload naar je **Downloadmap**.

---

## Stap 4 — Bestand importeren

Er zijn twee manieren om het bestand te importeren. **Optie A is de eenvoudigste** en vereist geen
technische kennis.

> **Let op — geldt voor beide opties:** een import **vervangt de bestaande teambegeleiding van de club
> volledig**. Alle eerder geïmporteerde rijen worden eerst verwijderd, daarna wordt de nieuwe lijst
> ingelezen. Er wordt niets samengevoegd. Is je export onvolledig (bijv. Bondsteam niet geselecteerd in
> stap 2), importeer dan simpelweg een nieuwe, complete export — die overschrijft de foutieve lijst weer.

### Optie A — via de Admin GUI (aanbevolen)

1. Open de Admin GUI en ga naar **Instellingen → Teambegeleiding importeren**
   (of klik de kaart "Teambegeleiding importeren" op de Instellingen-pagina zelf)
2. Kies het gedownloade bestand
3. Controleer de voorbeeldweergave en bevestig de import

#### Wat er met de gegevens gebeurt

Je browser leest het bestand in en toont een voorbeeld van de eerste vijf rijen, zodat je kunt
controleren of je het juiste bestand hebt. Klik je daarna op importeren, dan wordt **de volledige
inhoud van de CSV naar de server gestuurd** — beveiligd, en alleen vanuit jouw ingelogde sessie —
en daar meteen in de database verwerkt. De persoonsgegevens verlaten dus wél je browser; dat is
inherent aan een import.

Wat er daarna staat, en wat niet:

| | |
|---|---|
| **Wordt bewaard** | De begeleidersgegevens zelf (team, leeftijdscategorie, teamrol, naam, e-mailadres, telefoonnummer) in de database van je club |
| **Wordt bewaard** | Eén regel in het importlogboek: wie er wanneer heeft geïmporteerd, de bestandsnaam en het aantal rijen |
| **Wordt níet bewaard** | Het CSV-bestand zelf — dat wordt nergens op de server opgeslagen |
| **Wordt níet bewaard** | De inhoud van de CSV in logbestanden; de applicatie logt bewust alleen het aantal rijen en de duur |

### Optie B — via het PowerShell-script (alleen bij een SQL Server-database)

> **Let op:** dit script werkt uitsluitend als jouw installatie op SQL Server draait. Draait je
> club op Postgres — wat de standaard is — gebruik dan Optie A. Weet je het niet zeker, gebruik dan
> Optie A; die werkt altijd.

1. Open **PowerShell** (zoek via het Startmenu op "PowerShell")

2. Navigeer naar de projectmap (vervang `<PROJECTMAP>` door het pad naar jouw lokale repo):

   ```powershell
   cd <PROJECTMAP>
   ```

3. Voer het importscript uit:

   ```powershell
   .\exports\import-teambegeleiding-to-sql.ps1
   ```

4. Het script importeert het bestand automatisch en laat aan het einde zien hoeveel personen verwerkt zijn.

> **Let op:** het bestand bevat persoonsgegevens van clubleden. Na de import kun je de CSV verwijderen door het script uit te voeren met `-DeleteCsvAfterImport $true`.

---

## Controleren of het gelukt is

**Bij Optie A** verschijnt onder het importvak een groene melding:
*"Geïmporteerd: [aantal] begeleiders succesvol geladen."* Staan er waarschuwingen onder —
bijvoorbeeld over overgeslagen dubbele rijen — lees die dan even door; de import is dan wel gelukt.

**Bij Optie B** toont PowerShell een samenvatting:

```
Klaar!
  Geïmporteerd : [aantal] personen
  Tabel        : avg.Teambegeleiding
  Duur         : [duur] ms
  Datum        : [datum]
```

Klopt het aantal met wat je in Sportlink Club hebt gezien? Dan is alles goed gegaan.

---

## Hoe vaak moet dit?

Deze export wordt **wekelijks** uitgevoerd — kies een vast moment dat past bij jouw club. Zo is de lijst altijd actueel met nieuwe leden, gewijzigde rollen en vertrokken begeleiders.

---

## Problemen?

| Probleem | Mogelijke oorzaak | Oplossing |
|---|---|---|
| Veel minder personen gevonden dan verwacht | Filter niet goed ingesteld | Herhaal stap 2 en controleer of de 4 Teamspeler-rollen uitgevinkt zijn |
| Lijst bevat teams die geen bondsteam zijn | **Bondsteam** niet op *alles selecteren* gezet (stap 2.3) | Herhaal stap 2 mét die selectie en importeer opnieuw — de nieuwe import vervangt de foutieve lijst volledig |
| Geen exportknop zichtbaar | Onvoldoende rechten | Vraag een beheerder om de export uit te voeren |
| Script geeft een fout | Geen bestand gevonden | Controleer of de download in stap 3 geslaagd is en het bestand in de Downloadmap staat |
| Verificatiecode werkt niet | Code verlopen | Wacht tot de authenticator-app een nieuwe code toont en probeer opnieuw |
| Waarschuwing "exacte duplicaat-rij(en) overgeslagen" | Sportlink-export bevat dezelfde persoon met exact dezelfde rol twee keer | Geen actie nodig — de import slaat deze duplicaten automatisch over, de rest van de lijst is correct geïmporteerd |
| Foutmelding "Een of meer rijen overschrijden de maximale kolomlengte" met een rij/kolom-lijst | Een veld in de CSV (bijv. een teamnaam of e-mailadres) is langer dan de databasekolom toestaat | Kort de genoemde velden in en importeer opnieuw — de vorige geldige import is niet gewijzigd |

---

## Technische achtergrond (niet nodig om de import uit te voeren)

> **Postgres-tier (#824, epic #815).** Deze handleiding is tier-neutraal: de stappen hierboven
> (Sportlink-export + upload via **Instellingen → Teambegeleiding importeren**) werken identiek
> op beide databasevarianten. Sinds #913 heeft de Postgres-tier dezelfde flexibele
> CSV-kolomherkenning (aliassen, dedup, validatie — `FunctionApp.Postgres/Admin/
> AdminTeambegeleidingFunction.cs`) als de SQL Server-tier, boven op het AVG-gevoelige
> database-interactiedeel uit #824 zelf (`avg.teambegeleiding`/`avg.importlog` via
> `Database.Postgres/TeambegeleidingImporter.cs`: atomische delete-vóór-COPY-import,
> ClubCode-gescoped staleness-check, `syncenabled`-gevalideerde clubselectie). Getest tegen een
> lokale Postgres-devcontainer, uitsluitend met fictieve testdata.
>
> **#1131/#1132 (beide tiers atomisch en per-club geserialiseerd).** De SQL Server-import
> (`FunctionApp/Admin/AdminTeambegeleidingFunction.cs`) valideert de kolomlengtes van élke rij
> vóórdat de club-scoped DELETE draait, en voert DELETE + inserts + de audit-rij in `avg.ImportLog`
> uit in één transactie met rollback bij elke fout — een te lange waarde (bijv. een teamnaam van
> meer dan 100 tekens) levert een 400 met een foutmelding per rij/kolom op, en laat de vorige
> geldige import ongemoeid. De Postgres-import serialiseert vervangingen per club met een
> `pg_advisory_xact_lock` vóór de DELETE, zodat twee overlappende imports voor dezelfde club nooit
> allebei kunnen committen (de tweede wacht en vervangt daarna de eerste volledig, in plaats van de
> twee batches samen te voegen).

**Waar de CSV langskomt (Optie A).** `BlazorAdmin/Pages/Teambegeleiding.razor` leest het bestand
met `OpenReadStream` (max. 5 MB) in `_csvContent` en maakt daar client-side alleen een voorbeeld
van vijf rijen mee. Bij bevestigen gaat `_csvContent` ongewijzigd als JSON-veld `csvContent` naar
`POST /api/beheer/teambegeleiding/import`. De Function deserialiseert die body, parseert de CSV
server-side (`ParseCsv`) en geeft de genormaliseerde rijen door aan
`Database.Postgres.TeambegeleidingImporter.ImportAsync`, dat in één transactie de bestaande rijen
van de club verwijdert, de nieuwe rijen via binaire `COPY` in `avg.teambegeleiding` laadt en één
auditrij in `avg.importlog` schrijft (aantal rijen, bestandsnaam, importeerder, duur, clubcode).
Het bestand zelf wordt niet naar schijf geschreven, en de logregel bevat expliciet geen
persoonsgegevens.
