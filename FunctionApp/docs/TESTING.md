# Verificatie & zelfherstellende tests

## Test-App.ps1

`Test-App.ps1` in de repo-root is het centrale verificatie- en herstelscript.
Het controleert schema, build en runtime in één doorloop.

### Gebruik

```powershell
# Alleen checken (geen wijzigingen)
.\Test-App.ps1

# Automatisch herstellen waar mogelijk
.\Test-App.ps1 -Fix

# Alles tonen, ook successen
.\Test-App.ps1 -Verbose
.\Test-App.ps1 -Fix -Verbose
```

### Wat wordt gecontroleerd

| Sectie | Controle | -Fix |
|--------|----------|------|
| 1. DB-verbinding | `local.settings.json` aanwezig en geldig | nee |
| 2. Schema | Alle 8 tabellen én hun kolommen | ja — ALTER TABLE / CREATE TABLE |
| 3. Build | `dotnet build` FunctionApp + BlazorAdmin | nee |
| 4. API smoke | 10 endpoints op `:7094` | nee (2xx verwacht) |
| 5. Blazor pagina's | 7 routes op `:5242` | nee (geen Blazor-foutindicatoren) |

Secties 4 en 5 worden automatisch overgeslagen als de services niet draaien.

### Bewake tabellen

```
dbo.AppSettings              — ClubName, ClubCode, Accommodatie, GPS, ...
dbo.TeamVoorkeurTijden       — Id, TeamNaam, DagVanWeek, VoorkeurTijd, ...
dbo.VeldBeschikbaarheid      — Id, VeldNummer, DagVanWeek, BeschikbaarVanaf, ...
dbo.UitgeslotenEmailAdressen — Id, EmailAdres, Omschrijving, Actief, ClubCode
dbo.EmailTemplateInstellingen— Id, TemplateKey, Onderwerp, BodyTemplate, ...
dbo.AppSettingsAudit         — Id, GewijzigdDoor, Veld, OudeWaarde, ...
dbo.TeamRegels               — Id, TeamNaam, RegelType, ...
dbo.Velden                   — VeldNummer, VeldNaam, VeldType, ...
```

### Verplicht workflow

1. Wijziging in code, API-contract of database-schema?  
   → `.\Test-App.ps1 -Fix` uitvoeren
2. Alle checks groen (exit 0)?  
   → Pas dan committen
3. Voor volledige coverage (inclusief API + Blazor smoke tests):  
   → Eerst `.\Start-Debug.ps1`, wacht ~15s, daarna `.\Test-App.ps1`

### Exit codes

- `0` — alles in orde
- `1` — fouten gevonden (of niet automatisch te herstellen)

---

## Start-Debug.ps1

Start Azurite, FunctionApp en BlazorAdmin tegelijk in aparte vensters.

```powershell
.\Start-Debug.ps1
```

Poorten: Azurite :10000, FunctionApp :7094, BlazorAdmin :5242.

---

## Achtergrond: schema-drift

Het project gebruikt **SSDT** (SQL Server Database Project) voor declaratief schemabeheer.
De `.sql` bestanden in `Database/dbo/Tables/` definiëren de _target state_.

Lokaal wordt de live database **niet automatisch geüpdatet** bij een git pull.
`Test-App.ps1 -Fix` vervangt dit voor lokale ontwikkeling door:
- Ontbrekende tabellen aanmaken vanuit de `.sql` bestanden
- Ontbrekende kolommen toevoegen via `ALTER TABLE`

Voor productie-deploys: gebruik de SSDT publish-diff workflow of een migratiescript.
