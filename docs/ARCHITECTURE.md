# Architectuurprincipes — Sportlink Wedstrijdzaken

Dit document beschrijft de architectuurregels die gelden voor alle bijdragers aan dit project. Ze zijn niet optioneel: de Security Gate en codereview blokkeren PRs die hiervan afwijken.

---

## Inhoudsopgave

1. [Tijdzones — UTC in database, lokale tijd in GUI](#1-tijdzones--utc-in-database-lokale-tijd-in-gui)
2. [Multi-club isolatie — ClubCode discriminator](#2-multi-club-isolatie--clubcode-discriminator)
3. [Secrets en club-specifieke waarden](#3-secrets-en-club-specifieke-waarden)
4. [Geen club-specifieke strings in code](#4-geen-club-specifieke-strings-in-code)
5. [AVG / GDPR](#5-avg--gdpr)
6. [Lagen synchroon houden](#6-lagen-synchroon-houden)

---

## 1. Tijdzones — UTC in database, lokale tijd in GUI

**Alle drie lagen moeten correct zijn. Een fout in één laag stapelt offsets op.**

### Regel

| Laag | Verplicht | Waarom |
|---|---|---|
| **SQL Server** | `GETUTCDATE()` — nooit `GETDATE()` | `GETDATE()` retourneert lokale servertijd (CEST = UTC+2 in de zomer). Als de API dit daarna als UTC markeert, telt Blazor er opnieuw +2u bij op → tijdstip 2u in de toekomst |
| **FunctionApp API** | `DateTime.SpecifyKind(dt, DateTimeKind.Utc)` na elke SQL-read | SQL Server levert `DateTimeKind.Unspecified`. Zonder SpecifyKind serialiseert System.Text.Json zonder `Z`-suffix; clients kunnen dit als lokale tijd interpreteren |
| **Blazor WASM** | `.ToLocalTime()` vóór elke `.ToString()` | UTC-tijden tonen zonder conversie geeft tijden die 1–2u achter lijken voor Nederlandse gebruikers |

### Voorbeeld — correct

```csharp
// FunctionApp — lezen uit SQL:
DateTime? lastSync = reader["LastSyncTimestamp"] != DBNull.Value
    ? DateTime.SpecifyKind(Convert.ToDateTime(reader["LastSyncTimestamp"]), DateTimeKind.Utc)
    : null;
// → JSON: "2026-05-21T13:35:00Z"

// FunctionApp — schrijven naar SQL:
"UPDATE [dbo].[AppSettings] SET [LastSyncTimestamp] = GETUTCDATE()"

// Blazor WASM — weergave:
@status.LastSyncTimestamp.Value.ToLocalTime().ToString("dd-MM-yyyy HH:mm")
// → "21-05-2026 15:35" (CEST, correct voor NL-gebruiker)
```

### Voorbeeld — fout patroon

```csharp
// FOUT: slaat lokale servertijd op
"UPDATE ... SET [mta_modified] = GETDATE()"
// CEST 14:39 → opgeslagen als "14:39" → API markeert als UTC → Blazor +2u → "16:39" (in de toekomst!)

// FOUT: toont rauw UTC in Blazor
@model.Timestamp.ToString("HH:mm")
// → "13:35" (UTC) terwijl lokale tijd 15:35 is
```

### Verificatie bij codereview

- [ ] Elke `INSERT`/`UPDATE` in C# die een `DateTime`-kolom schrijft: `GETUTCDATE()` of `DateTime.UtcNow`?
- [ ] Elke DateTime-waarde gelezen uit SQL: `DateTime.SpecifyKind(..., DateTimeKind.Utc)` aanwezig?
- [ ] JSON-response body: heeft elke datetime een `Z`-suffix? (Controleer via browser DevTools → Network)
- [ ] Elke DateTime-weergave in Blazor: `.ToLocalTime()` aanwezig vóór `.ToString()`?

> **Incident-referentie:** Op 2026-05-21 toonde het Dashboard 'Laatste sync' als tijdstip 2u in de toekomst.
> Oorzaak: `GETDATE()` in `Utilities.cs` en 5 andere bestanden. Fix in PR #246.

---

## 2. Multi-club isolatie — ClubCode discriminator

Elke databasetabel met club-specifieke data heeft een `ClubCode`-kolom. Queries filteren altijd op de `ClubCode` uit `dbo.AppSettings`.

```sql
-- Correct
SELECT * FROM [dbo].[TeamVoorkeurTijden]
WHERE [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings])

-- Fout: hardcoded clubwaarde
SELECT * FROM [dbo].[TeamVoorkeurTijden]
WHERE [ClubCode] = 'ABC'  -- nooit!
```

Nieuwe tabellen zonder `ClubCode`-kolom worden **geweigerd in codereview**.

---

## 3. Secrets en club-specifieke waarden

Productie-configuratie wordt **nooit** in git opgeslagen. Het CI-systeem genereert de club-specifieke config automatisch vanuit templates en GitHub Variables/Secrets.

| Bestand | In git? | Reden |
|---|---|---|
| `BlazorAdmin/wwwroot/appsettings.Production.template.json` | ✓ | Bevat alleen `{{PLACEHOLDER}}` tokens |
| `BlazorAdmin/wwwroot/appsettings.Production.json` | ✗ | Gegenereerd door CI, bevat Tenant/Client ID |
| `FunctionApp/local.settings.json` | ✗ | Bevat `SqlConnectionString` en andere secrets |
| `FunctionApp/local.settings.template.json` | ✓ | Template zonder waarden |

Zie [SETUP.md](../SETUP.md) voor hoe je GitHub Variables instelt bij het opzetten van een eigen instantie.

---

## 4. Geen club-specifieke strings in code

Fallback-waarden (`?? "..."`) in C# mogen **nooit** een clubnaam, domeinnaam, persoonsnaam, plaatsnaam of adres bevatten.

```csharp
// Correct — faalt snel bij ontbrekende configuratie
var clubCode = GetSetting("clubCode")
    ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

// Fout — maskeert misconfiguratie en breekt andere clubs
var clubCode = GetSetting("clubCode") ?? "ABC";
```

Als een verplichte instelling ontbreekt in `dbo.AppSettings` → `InvalidOperationException`, nooit een stille fallback met een echte waarde.

---

## 5. AVG / GDPR

- `exports/*.csv` en `exports/*.xlsx` bevatten persoonsgegevens en mogen **nooit** gecommit worden
- E-mailadressen van leden uitsluitend via **BCC** bij communicatie met derden
- Persoonsgegevens in de database zijn beschermd via retentiebeleid (`planner.sp_CleanupEmailVerwerking`)
- Logging van persoonsgegevens is verboden — geen namen of e-mailadressen in `ILogger`-output

De Security Gate in CI blokkeert automatisch bij detectie van PII-patronen. Zie [SECURITY.md](../SECURITY.md).

---

## 6. Lagen synchroon houden

Database-schema, API-endpoint en Blazor GUI worden **altijd in dezelfde commit** bijgewerkt.

- Nieuw database-veld → bijbehorend API-veld en Blazor-weergave in dezelfde PR
- Nieuw enum of templatesleutel in code → GUI-optie in dezelfde commit
- Nooit een GUI die verwijst naar een API-veld dat nog niet bestaat, en andersom

---

*Vragen of afwijkingen? Open een [GitHub Issue](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues) of start een discussie.*
