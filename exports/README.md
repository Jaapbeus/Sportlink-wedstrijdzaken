# exports/ — Teambegeleiding import

Deze map bevat scripts voor het importeren van de **teambegeleiding export** vanuit Sportlink Club naar de lokale SQL Server database. De geïmporteerde data wordt gebruikt door de planner om automatisch de juiste contactpersonen (leider, trainer) te bereiken bij wedstrijdwijzigingen.

## Waarom SQL en niet een CSV in git?

De export van Sportlink bevat persoonsgegevens (namen, e-mailadressen, telefoonnummers). Om te voldoen aan de AVG/GDPR:

- `exports/*.csv` worden **nooit** naar git gepusht — dit staat geblokkeerd in `.gitignore`
- De data wordt opgeslagen in de beveiligde lokale SQL Server (`avg.Teambegeleiding`)
- De CSV is alleen tijdelijk aanwezig op de lokale machine van de beheerder

---

## Hoe de data wordt gebruikt: wedstrijd verplaatsverzoeken

Wanneer een tegenstander vraagt om een wedstrijd te verplaatsen, stuurt de planner automatisch een antwoord-e-mail met de leider en trainer van het betreffende team in **BCC**.

**Waarom BCC en niet CC of TO?**  
BCC is hier niet alleen etiquette maar ook **AVG-conform vereist**. Artikel 5(1)(c) schrijft dataminimalisatie voor: persoonsgegevens van clubleden mogen niet onnodig gedeeld worden met derden (de tegenstander). Via BCC ontvangen leider en trainer een kopie van de e-mail en kunnen zelf contact opnemen — zonder dat de tegenstander hun gegevens ziet.

**SQL die de planner gebruikt:**
```sql
SELECT Naam, Emailadres
FROM   [avg].[Teambegeleiding]
WHERE  Team      = @team
  AND  Emailadres IS NOT NULL
```

---

## Workflow

### Stap 1 — CSV exporteren vanuit Sportlink Club

#### Handmatig downloaden (universeel, werkt voor alle clubs)

1. Ga naar [club.sportlink.com](https://club.sportlink.com) en log in met je verenigingsaccount
2. Navigeer naar **Leden → Ledenlijst** (of ga direct naar `/member/search`)
3. Klik op de filter **Teams**
4. Klik op **Rol binnen team**
5. Klik op **Alles geselecteerd** — nu staan alle rollen aangevinkt
6. Vink de volgende vier opties **uit** (dit zijn de spelers):
   - `Teamspeler / Aanvaller`
   - `Teamspeler / Keeper`
   - `Teamspeler / Middenvelder`
   - `Teamspeler / Verdediger`
7. Klik op **Zoeken** — dit duurt even (grotere clubs: 10–30 seconden)
8. Het resultaat toont alleen begeleidingsleden (leiders, trainers, verzorgers, managers)
9. Klik op het kleine **"Exporteer tabel"** icoon rechts boven de resultatenlijst (pijl-omlaag icoon)
10. Klik in de popup op **Download**
11. De CSV wordt opgeslagen in je standaard downloadmap

> **Tip:** de bestandsnaam die Sportlink meegeeft bevat het aantal gevonden personen, bijv. `Teams 420 personen gevonden.csv`. Dit is normaal.

---

### Stap 2 — CSV importeren naar SQL

```powershell
# Vanuit de projectroot — detecteert automatisch BegeleidingTeams.csv of teambegeleiding.csv
.\exports\import-teambegeleiding-to-sql.ps1

# Met verwijdering van de CSV na succesvolle import (aanbevolen voor AVG)
.\exports\import-teambegeleiding-to-sql.ps1 -DeleteCsvAfterImport $true

# Met een CSV op een andere locatie
.\exports\import-teambegeleiding-to-sql.ps1 -CsvPath "C:\pad\naar\BegeleidingTeams.csv"
```

Het script:
1. Leest `SqlConnectionString` uit `FunctionApp/local.settings.json`
2. Detecteert automatisch welke kolommen aanwezig zijn in de CSV (zie aliassen hieronder)
3. Valideert dat de verplichte kolommen beschikbaar zijn — geeft duidelijke fout als iets ontbreekt
4. Leegt `avg.Teambegeleiding` volledig (TRUNCATE) en importeert alle rijen opnieuw
5. Schrijft een auditregel naar `avg.ImportLog` (datum, aantal rijen, Windows-gebruikersnaam, duur)

**Controleren na import:**
```sql
SELECT COUNT(*)  FROM avg.Teambegeleiding
SELECT TOP 1 *   FROM avg.ImportLog ORDER BY ImportDatum DESC
```

---

## Verplichte CSV-kolommen

Het script herkent kolomnamen **hoofdletterongevoelig** en accepteert meerdere varianten. De volgende kolommen zijn **verplicht** aanwezig in de CSV:

| Veld in database        | Geaccepteerde kolomnamen in CSV                               |
|-------------------------|---------------------------------------------------------------|
| `Team`                  | `Team`, `Teamnaam`, `Team naam`                               |
| `Teamrol`               | `Teamrol`, `Rol`, `Rol in team`, `Rol team`                   |
| `Naam` *(samengesteld)* | `Roepnaam` **en** `Achternaam` (beide verplicht)              |
| `Emailadres`            | `E-mailadres`, `Email`, `E-mail`, `Emailadres`, `Mailadres`   |

> De `Naam`-kolom wordt samengesteld als: **Roepnaam + Tussenvoegsel + Achternaam**  
> Voorbeeld: `Nico` + `van` + `Ginkel` → `Nico van Ginkel`

### Optionele kolommen (worden meegenomen als aanwezig)

| Veld in database         | Geaccepteerde kolomnamen in CSV                                |
|--------------------------|----------------------------------------------------------------|
| `LeeftijdscategorieTeam` | `Leeftijdscategorie team`, `Leeftijdscategorie`                |
| Tussenvoegsel (voor Naam)| `Tussenvoegsel(s)`, `Tussenvoegsel`, `Infix`                   |
| `Telefoonnummer`         | `Mobiel nummer` *(voorkeur)* of `Telefoonnummer`, `Telefoon`   |

> **Telefoonnummer-logica:** als `Mobiel nummer` gevuld is, wordt dat gebruikt. Anders `Telefoonnummer`. Als geen van beide aanwezig is, wordt het veld `NULL`.

### Wat doet het script bij ontbrekende verplichte kolommen?

```
  FOUT: de volgende vereiste kolommen zijn niet gevonden in de CSV:
    - Emailadres  (verwacht één van: E-mailadres, Email, E-mail, Emailadres, Mailadres)

  Aanwezige CSV-kolommen:
    * Team
    * Teamrol
    * Naam
    ...
```

---

## Database tabellen

### `avg.Teambegeleiding` — contactpersonen per team

| Kolom                    | Type           | Omschrijving                                           |
|--------------------------|----------------|--------------------------------------------------------|
| `Id`                     | INT IDENTITY   | Primaire sleutel                                       |
| `Team`                   | NVARCHAR(100)  | Teamnaam zoals in Sportlink (bijv. `JO15-1`)           |
| `LeeftijdscategorieTeam` | NVARCHAR(50)   | Leeftijdsgroep (bijv. `Onder 15`, `Senioren`)          |
| `Teamrol`                | NVARCHAR(100)  | `Technische staf`, `Lokale staf`, `Overige staf`, etc. |
| `Naam`                   | NVARCHAR(300)  | Volledige naam: Roepnaam [Tussenvoegsel] Achternaam    |
| `Emailadres`             | NVARCHAR(200)  | E-mailadres van de persoon                             |
| `Telefoonnummer`         | NVARCHAR(50)   | Mobiel nummer (voorkeur) of vaste lijn                 |
| `mta_imported`           | DATETIME       | Tijdstip van de laatste import                         |

### `avg.ImportLog` — audittrail

| Kolom              | Type           | Omschrijving                           |
|--------------------|----------------|----------------------------------------|
| `Id`               | INT IDENTITY   | Primaire sleutel                       |
| `ImportDatum`      | DATETIME       | Tijdstip van import                    |
| `AantalRijen`      | INT            | Aantal geïmporteerde personen          |
| `CsvBestand`       | NVARCHAR(500)  | Volledig pad naar de gebruikte CSV     |
| `ImporterendeDoor` | NVARCHAR(200)  | Windows-gebruikersnaam (`SYSTEM_USER`) |
| `Duur_ms`          | INT            | Duur van de import in milliseconden    |

---

## Andere clubs: aanpassen voor jouw vereniging

Deze repository is ontworpen om herbruikbaar te zijn voor meerdere clubs. Aanpassingen per club:

**1. Verbindingsstring**  
Pas `SqlConnectionString` aan in `FunctionApp/local.settings.json`:
```json
"SqlConnectionString": "Server=JOUW-SERVER;Database=SportlinkSqlDb;Integrated Security=True;TrustServerCertificate=True;"
```

**2. CSV-bestandsnaam**  
Het script detecteert automatisch `BegeleidingTeams.csv` en `teambegeleiding.csv`. Andere namen geef je mee via `-CsvPath`.

**3. Kolomnamen aanpassen**  
Als jouw Sportlink-export andere kolomnamen heeft, voeg ze toe aan de `$columnAliases`-map bovenin `import-teambegeleiding-to-sql.ps1`:
```powershell
$columnAliases = [ordered]@{
    "Team"       = @("Team", "Teamnaam", "Team naam", "Jouw kolomnaam")
    "Emailadres" = @("E-mailadres", "Email", "Jouw variant")
    # ...
}
```

**4. Teamrol-waarden controleren**  
De Sportlink-export gebruikt standaard `Lokale staf` en `Technische staf` voor begeleidingsleden. Controleer de waarden in jouw export:
```sql
SELECT DISTINCT Teamrol, COUNT(*) AS Aantal
FROM avg.Teambegeleiding
GROUP BY Teamrol
ORDER BY Aantal DESC
```


---

## Git-regels voor deze map

Wijzigingen in `exports/` gaan **altijd rechtstreeks naar `main`** — nooit via een feature branch of pull request:

- Dit zijn operationele updates, geen codewijzigingen
- **Laat open PR's en development branches volledig met rust** bij het uitvoeren van een export-update
- `exports/*.csv` en `exports/*.xlsx` zijn uitgesloten van git via `.gitignore` — commit deze nooit

---

## AVG-bewaartermijn

`avg.Teambegeleiding` hanteert een bewaartermijn van **1 jaar** vanaf de laatste import (`mta_imported`).

**Hoe het werkt:**
- Het importscript doet een TRUNCATE + volledige herinsert. Bij elke import krijgen alle actieve begeleiders een verse `mta_imported`.
- Personen die niet meer actief zijn, verdwijnen automatisch bij de volgende import via de TRUNCATE.
- De `CleanupTeambegeleiding` Azure Function (maandelijks, 1e van de maand 04:00 UTC) verwijdert als vangnet alle rijen ouder dan 1 jaar — dit beschermt tegen het scenario waarbij het importscript een langere tijd niet draait.

**Aanbeveling:** voer het importscript minimaal **1× per seizoen** uit (seizoensstart ≈ augustus). Dit garandeert dat de data actueel is én dat de bewaartermijn correct verloopt.

---

## AVG/GDPR-regels

- De CSV mag alleen **tijdelijk lokaal** staan op de machine van de beheerder
- Gebruik `-DeleteCsvAfterImport $true` om de CSV direct na import te verwijderen
- Beperk `SELECT`-rechten op `avg.Teambegeleiding` tot bevoegde SQL-gebruikers en rollen
- Overweeg SQL Server **TDE** (Transparent Data Encryption) voor versleuteling van data at rest
- E-mailadressen van leden worden uitsluitend via **BCC** gedeeld bij wedstrijdcommunicatie — nooit in TO of CC naar externe partijen
- Raadpleeg bij twijfel het privacybeleid van de vereniging of de Functionaris Gegevensbescherming (FG)
