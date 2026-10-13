# Verzoek van betrokkene — inzage, rectificatie en wissing per tabel

> **Voor wie?** De beheerder van een clubinstallatie die een verzoek krijgt van iemand wiens gegevens
> in de applicatie staan (AVG art. 15 inzage, art. 16 rectificatie, art. 17 wissing, art. 21 bezwaar).
> Opgesteld bij issue #1701. Het feedbackdeel staat uitgebreider in [FEEDBACK.md](FEEDBACK.md) §9; dit
> document dekt de hele database.

Alles hieronder werkt op beide databasevarianten. Per tabel staat de SQL Server-naam én de Postgres-naam
(Postgres: schema `dbo` heet `public`, alle namen in kleine letters). Filter **altijd** op de eigen
clubcode: één database kan meerdere clubs bevatten (onder andere de democlub `ALLSTARS`).

---

## 1. Werkwijze

1. **Registreer het verzoek** met datum, soort verzoek en afhandeling, maar zonder de gegevens zelf
   opnieuw op te schrijven. Reageer binnen één maand (art. 12 lid 3).
2. **Stel de identiteit van de verzoeker vast** voordat je gegevens deelt: een inzageverzoek van een
   onbekend adres mag niet zelf een lek worden.
3. **Bepaal de zoeksleutels.** De applicatie kent drie soorten betrokkenen, elk met een eigen sleutel:

   | Betrokkene | Zoeksleutel |
   |---|---|
   | Teambegeleider (trainer, leider, coach) | e-mailadres, naam |
   | Afzender of ontvanger van e-mail aan de club | e-mailadres |
   | Gebruiker van de beheertoepassing (rol `admin` of `user`) | Entra object-ID (te vinden in de eigen tenant), weergavenaam, e-mailadres/UPN |
   | Scheidsrechter, official of contactpersoon in wedstrijddata | naam, e-mailadres, telefoonnummer |

4. **Zoek per tabel** (§2) met de sleutel, uitsluitend binnen de eigen clubcode, en exporteer niets naar
   een bestand dat buiten de afhandeling blijft bestaan. Lever het resultaat beveiligd aan de verzoeker.
5. **Wis of corrigeer** volgens de kolom *Wissing*. Een gegeven dat uit Sportlink komt, keert bij de
   volgende synchronisatie of import terug zolang het in Sportlink staat: corrigeer het dáár eerst.
6. **Controleer de termijn**: veel gegevens verdwijnen al automatisch (kolom *Automatische termijn*);
   noem dat in het antwoord.

Databasetoegang: SQL Server via een eigen beheerdersaccount (wachtwoord via `SQLCMDPASSWORD`, nooit als
argument); Postgres via de SQL-editor van het databasedashboard met een beheerdersaccount. Een
AI-agent voert deze stappen nooit zelf uit tegen productie.

---

## 2. Tabellen met persoonsgegevens

### Teambegeleiding

| Tabel | Persoonsgegevens | Zoeksleutel | Inzage en wissing | Automatische termijn |
|---|---|---|---|---|
| `avg.Teambegeleiding` / `avg.teambegeleiding` | naam, e-mailadres, telefoonnummer, teamrol, functie, team | `Emailadres`, `Naam` | **Inzage:** `SELECT` op de sleutel en `ClubCode`. Ook zichtbaar op het scherm *Teambegeleiding*. **Rectificatie/wissing:** corrigeer of verwijder de persoon in Sportlink en importeer een nieuwe, volledige export ([ADMIN-TEAMBEGELEIDING-IMPORT.md](ADMIN-TEAMBEGELEIDING-IMPORT.md)); de import vervangt alle rijen van de club. Moet het direct: `DELETE` op de sleutel en `ClubCode`, en zorg dat de volgende export de persoon niet meer bevat. | 1 jaar na de laatste import (vangnet) |
| `avg.ImportLog` / `avg.importlog` | `ImporterendeDoor` (weergavenaam van wie importeerde), `CsvBestand` (bestandsnaam) | `ImporterendeDoor` | **Inzage:** `SELECT` op de sleutel. **Wissing:** `UPDATE ... SET ImporterendeDoor = NULL, CsvBestand = NULL` voor de rijen van de verzoeker. | anonimiseren na 90 dagen, verwijderen na 1 jaar |
| `avg.TeambegeleidingInzageLog` / `avg.teambegeleidinginzagelog` (#1701) | `InzienDoorObjectId` (pseudoniem) en `InzienDoorNaam` van wie contactgegevens opvroeg; `Team`, `AantalRijen`, tijdstip. **Geen** gegevens van de ingeziene begeleiders | inziener: `InzienDoorObjectId`; ingeziene begeleider: `Team` (de teams uit `avg.Teambegeleiding` van die persoon) | **"Wie heeft mijn gegevens ingezien?"** — zoek de teams van de begeleider op in `avg.Teambegeleiding` en vraag daarna de inzageregels voor die teams en `ClubCode` op (tijdstip, inziener, aantal kaarten). Let op: een regel noemt het team, niet de persoon; vóór een import kan de samenstelling anders zijn geweest. **Wissing door een inziener:** dit is een verantwoordingslog; wis niet vóór de termijn, tenzij de afweging (art. 17 lid 3) anders uitvalt. | 12 maanden |

### E-mail

| Tabel | Persoonsgegevens | Zoeksleutel | Inzage en wissing | Automatische termijn |
|---|---|---|---|---|
| `planner.EmailVerwerking` / `planner.emailverwerking` | afzender, onderwerp, berichttekst, antwoord, ontvangers (`VerstuurdNaar`), foutmelding; ook de audit-rij van een handmatig doorgestuurde teambegeleiding-vraag (`VerzoekType = TeambegeleidingDoorsturen`: aanvrager, ontvangers, team in het onderwerp) | `Afzender`, `VerstuurdNaar` (bevat het adres) | **Inzage:** `SELECT` op de sleutel en `ClubCode`; het scherm *E-maillog* toont gemaskeerde adressen. **Wissing:** `DELETE` op de sleutel en `ClubCode`. Verwijder eerst verwijzende rijen in `planner.ClassificatieCorrectie` met herkomst `Reply` (geen `ON DELETE CASCADE`). | anonimiseren na 30 dagen, verwijderen na 90 dagen |
| `planner.ClassificatieCorrectie` / `planner.classificatiecorrectie` | samenvattingen van een bericht en de correctie; bij een admin-leermoment `AangemaaktDoor` (object-ID) en `AangemaaktDoorNaam` | `AangemaaktDoor`; voor reply-rijen via de verwijzing naar `EmailVerwerking` | **Inzage:** scherm *AI-leermomenten* of `SELECT`. **Wissing:** een admin-leermoment met de knop **Verwijderen** (alleen eigen club); een reply-rij met `DELETE`. | herkomst `Reply`: anonimiseren na 30, verwijderen na 90 dagen; admin-leermoment: geen (handmatig verwijderbaar) |
| `planner.OnbekendeTeamTekst` / `planner.onbekendeteamtekst` | gesaneerde voorbeeldtekst van een onbekende teamschrijfwijze (restrisico: een voornaam met een cijfer) | `VoorbeeldTekst` | **Inzage:** de lijst met onbekende teamteksten op het scherm *Teamaliassen* of `SELECT`. **Wissing:** `DELETE` op de rij (het scherm kan alleen de status wijzigen). | 90 dagen na laatst gezien |
| `planner.EmailTrace` / `planner.emailtrace` | geen: de opslag bevat alleen vaste codes, tellingen en een vormkenmerk (SECURITY.md Laag 6) | — | Niets te doen; noem in het antwoord dat de beslissingstrace geen persoonsgegevens bevat. | permanent |
| `dbo.UitgeslotenEmailAdressen` / `public.uitgeslotenemailadressen` | e-mailadres, omschrijving | `EmailAdres` | **Inzage:** blok *Uitgesloten e-mailadressen* op *App instellingen*. **Let op bij wissing:** dit is de bezwaarlijst (art. 21). Wis je het adres, dan krijgt de persoon weer automatische antwoorden. Leg dat uit en verwijder alleen op uitdrukkelijk verzoek. | geen (moet blijven bestaan) |

### Gebruikers van de beheertoepassing

| Tabel | Persoonsgegevens | Zoeksleutel | Inzage en wissing | Automatische termijn |
|---|---|---|---|---|
| `avg.Feedback`, `avg.FeedbackTelemetrie`, `avg.FeedbackInzageLog` / `avg.feedback*` | melder (object-ID, naam), meldingstekst, technische context, inzagelog | `MelderObjectId`, `InzienDoorObjectId` | Volg [FEEDBACK.md](FEEDBACK.md) §9 (inzage via het feedbackoverzicht; wissing = identiteit op `NULL`, issue verwijderen als de tekst persoonsgegevens bevat). | identiteit: issue open + 24 maanden; telemetrie 90 dagen; inzagelog 24 maanden |
| `dbo.AppSettingsAudit` / `public.appsettingsaudit` | `GewijzigdDoor` (gebruikersnaam/UPN); `OudeWaarde`/`NieuweWaarde` kunnen e-mailadressen bevatten | `GewijzigdDoor`, of het adres in de waarden | **Inzage:** `SELECT`. **Wissing:** verantwoordingslog; wis niet vóór de termijn tenzij de afweging anders uitvalt. Een adres in een waarde kan worden vervangen door een placeholder. | instelbaar, standaard 730 dagen |
| `dbo.SportlinkMutationAudit` / `public.sportlinkmutationaudit` | `TriggerdDoor` (e-mailadres/UPN van de beheerder), testnotitie | `TriggerdDoor` | Als hierboven (verantwoordingslog). | instelbaar, standaard 365 dagen |
| `dbo.TeamAliassen` / `public.teamaliassen` | `AangemaaktDoor`/`BeoordeeldDoor` (object-ID) en de naammomentopnames | `AangemaaktDoor`, `BeoordeeldDoor` | **Inzage:** scherm *Teamaliassen* of `SELECT`. **Wissing:** zet de vier auditkolommen op `NULL` of verwijder de alias (dan verdwijnt het auditspoor mee). | geen (leeft zolang de alias bestaat) |
| `dbo.SportlinkExtensieRollen` / `public.sportlinkextensierollen` | `LaatstGekoppeldDoor` (UPN), `SportlinkAccountNaam` | beide kolommen | **Inzage:** `SELECT`. **Wissing:** koppeling opnieuw leggen door een ander (overschrijft beide kolommen), of de rij verwijderen (`DELETE` op `RolNaam` + `ClubCode`). Zet niet alleen `LaatstGekoppeldDoor` en `SportlinkAccountNaam` op `NULL`: beide tiers tonen een rol als gekoppeld zolang `LaatstGekoppeldOp` gevuld is, dus zet dan ook die kolom op `NULL`. Er is geen knop of endpoint om te ontkoppelen. | geen (actuele toestand, geen historie) |
| `dbo.SportlinkAutoLogin` / `public.sportlinkautologin` | versleutelde inloggegevens van een Sportlink-account per rol | `RolNaam` + `ClubCode` | **Inzage:** de waarden zijn versleuteld en worden nooit getoond; meld alleen dát er een koppeling is. **Wissing:** knop **Inloggegevens en refresh-token verwijderen** bij die rol op het scherm *Sportlink Web Extension*. | geen |
| `planner.GeplandeWedstrijden` / `planner.geplandewedstrijden` | `AangevraagdDoor` (vrije tekst), `Opmerking` | `AangevraagdDoor` | **Inzage:** `SELECT`. **Rectificatie/wissing:** `UPDATE ... SET AangevraagdDoor = NULL` (en de opmerking nalopen). | geen automatische termijn |
| `planner.HerplanVerzoeken` / `planner.herplanverzoeken` | `AangevraagdDoor` (vrije tekst), `Opmerking` | `AangevraagdDoor` | Als hierboven. | geen automatische termijn |

### Wedstrijddata uit Sportlink

| Tabel | Persoonsgegevens | Zoeksleutel | Inzage en wissing | Automatische termijn |
|---|---|---|---|---|
| `his.Matches` / `his.matches` (en `stg.*`) | scheidsrechter(s), kleedkamerscheidsrechter | naam | **Inzage:** `SELECT` op de naamkolommen en `ClubCode`. **Wissing:** de bron is Sportlink; zolang de wedstrijd in het synchronisatievenster valt, overschrijft de volgende synchronisatie een lokale wijziging. Corrigeer in Sportlink; voor gespeelde wedstrijden buiten het venster kun je de kolommen lokaal op `NULL` zetten. | op het moment van schrijven geen; zie issue #1698 |
| `his.MatchDetails` / `his.matchdetails` | scheidsrechters, verenigingsscheidsrechter, officials, rijder, contactvelden van teams en accommodatie (telefoon, e-mail) | naam, e-mailadres, telefoonnummer | Als hierboven. | op het moment van schrijven geen; zie issue #1698 |

### Instellingen

| Tabel | Persoonsgegevens | Zoeksleutel | Inzage en wissing | Automatische termijn |
|---|---|---|---|---|
| `dbo.AppSettings` / `public.appsettings` | naam en e-mailadres van de coördinator/afzender, voettekst | `CoordinatorNaam`, `PlannerEmailAdres`, `PlannerAfzenderNaam` | **Inzage/rectificatie:** scherm *App instellingen* (wijzigingen komen in `AppSettingsAudit`). | geen (configuratie) |
| `dbo.EmailTemplateInstellingen` / `public.emailtemplateinstellingen` | een naam in een sjabloontekst (bijvoorbeeld een ondertekening) | de naam in `BodyTemplate` | **Inzage/rectificatie:** scherm *E-mailtemplates*. | geen (configuratie) |

---

## 3. Buiten de database

* **Mailbox van de club.** Inkomende en verzonden berichten staan ook in de mailbox zelf (Microsoft 365);
  de bewaartermijnen van de applicatie gelden daar niet. Doorzoek en wis daar apart.
* **GitHub.** Alleen gepubliceerde feedback; zie [FEEDBACK.md](FEEDBACK.md) §9.
* **Applicatielogs.** De applicatie logt geen persoonsgegevens (SECURITY.md Laag 5); er is niets per
  persoon te doorzoeken.
* **Sportlink.** De bron van teambegeleiding en wedstrijddata. Een verzoek dat daar thuishoort, stuur je
  door naar wie Sportlink voor de club beheert.
* **Back-ups.** Een gewiste rij kan nog in een database-back-up staan tot die volgens de bewaartermijn
  van het platform verloopt. Meld dat eerlijk; zet een back-up niet terug zonder de wissing opnieuw uit
  te voeren.

---

*Laatste verificatie: v3.13.4.24 — 2026-10-12*
