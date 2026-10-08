# Feedback — voor alle gebruikers, met overzicht, technische context en bewaartermijnen

Gezaghebbend document voor de feedbackwidget (FEEDBACK-knop) en het feedbackoverzicht in de Admin GUI.
Opgeleverd met issue #764 (deelissues #1476 backend, #1477 widget, #1478 overzicht). De
verwerkingsgrondslag, bewaartermijnen en het verwerkingsregister staan in §7 en §8.

> **Eigenaarsbesluit 2026-10-03** (zie de reactie op #764), dat dit document uitwerkt:
> iedere ingelogde gebruiker (rol `admin` én `user`) mag feedback geven · de melder wordt bewaard als
> Entra object-ID met de naam als momentopname, en komt nooit in het publieke GitHub-issue ·
> bewaartermijn: zolang het issue open is plus 24 maanden na sluiting · een beheerder publiceert
> direct, een gewone gebruiker pas na een klik van een beheerder · inzagelog op het overzicht ·
> technische context standaard aan, zichtbaar vóór verzending en uit te zetten.

---

## 1. Wie mag wat

| Actie | Endpoint | Rol | Poort |
|---|---|---|---|
| Volledigheid controleren | `POST /api/feedback/validate` | `admin` of `user` | `AdminEndpoint.ExecuteAuthenticatedAsync` |
| Voorbeeld van het issue | `POST /api/feedback/preview` | `admin` of `user` | idem |
| Melding indienen | `POST /api/feedback/submit` | `admin` of `user` | idem |
| Overzicht, detail, inzagelog | `GET /api/beheer/feedback`, `/{id}`, `/inzagelog` | alleen `admin` | `AdminEndpoint.ExecuteAsync` |
| Publiceren vanuit het overzicht | `POST /api/beheer/feedback/{id}/publiceer` | alleen `admin` | idem |

De melder (object-ID, naam, rol) wordt **uitsluitend uit het Easy Auth-principal** gehaald, nooit uit
de requestbody. Een client kan zich dus niet voordoen als een ander of als beheerder.

## 2. Publicatiebeleid

```
melding ──► PII-gates ──► AI-structurering ──► opslag in avg.Feedback ──┬─ rol admin ──► direct GitHub-issue
(admin of user)                                 (status: wacht-op-       │
                                                 publicatie)             └─ rol user  ──► wacht; beheerder klikt
                                                                                           "Publiceer" in /feedback
```

* De tekst die uiteindelijk naar GitHub gaat (titel + body) wordt **bij het indienen** samengesteld,
  door beide PII-gates gehaald en bewaard (`avg.Feedback.Onderwerp` + `IssueBody`). De beheerder die
  later publiceert ziet **exact die tekst** in het overzicht; de AI draait niet opnieuw. De PII-gate
  draait bij het publiceren nogmaals (de regex kan sinds het indienen zijn aangescherpt).
* Een beheerder doorloopt de bestaande stappen (controleren → overzicht → voorbeeld → publiceren, #1205).
  Een gewone gebruiker doorloopt controleren → overzicht → versturen en ziet daarna *"Je melding is
  opgeslagen"* met een meldingsnummer — **geen** link naar GitHub.
* Publiceren is idempotent bewaakt: `ClaimPublicatieAsync` zet de status atomair van
  `wacht-op-publicatie`/`github-mislukt` naar `publiceren`; een tweede klik maakt geen tweede issue.
* Faalt GitHub, dan blijft de melding bewaard met status `github-mislukt` en is ze opnieuw te
  publiceren. Faalt de database bij een beheerder, dan loopt de publicatie toch door (zijn melding
  sneuvelt niet op een gepauzeerde gratis database); bij een gewone gebruiker is opslaan verplicht.
* Alle uitgaande GitHub-aanroepen (publiceren, statuscontrole door de retentietimer) lopen via de
  tier-eigen `EgressGuard.ExternalIntegrationsAllowed()` (#857).

## 3. Wat er in het publieke issue staat — en wat niet

| Wel | Nooit |
|---|---|
| Type, pagina (route zonder querystring), app-versie, browser, tijdstip | Naam, object-ID, e-mailadres of rol van de melder |
| De beschrijving in eigen woorden + antwoorden op aanvulvragen | Technische context (console-fouten, mislukte aanroepen, navigatiespoor) |
| AI-samenvatting en acceptatiecriteria | Het `FeedbackId` of een andere sleutel naar de melder |

## 4. Datamodel (beide tiers, schema `avg` — persoonsgegevens)

SQL Server: `Database/avg/Tables/Feedback*.sql` + idempotent in `Database/Script.PostDeployment1.sql`;
Postgres: migratie `035_avg_feedback.sql` (lowercase). Row-Level Security staat aan zonder policies
(#1198, geborgd door `check-rls-enabled.sh`).

**`avg.Feedback`** — één rij per melding

| Kolom | Inhoud |
|---|---|
| `FeedbackId` (uniek), `ClubCode`, `Type` (Fout/Verzoek/Vraag) | sleutel, scoping, soort |
| `Onderwerp`, `IssueBody` | de te publiceren titel en body (AI + gates, geen identiteit) |
| `Beschrijving`, `VragenAntwoorden` | eigen woorden van de melder |
| `MelderObjectId`, `MelderNaam` | **persoonsgegevens**: Entra `oid` (pseudoniem) en weergavenaam als momentopname. `NULL` na anonimisering. Geen e-mailadres. |
| `MelderRol` | `admin` of `user` (uit het principal) |
| `Pagina`, `AppVersie` | route zonder querystring, versie |
| `IssueNummer`, `IssueUrl`, `Status` | `wacht-op-publicatie` → `publiceren` → `gepubliceerd` / `github-mislukt` |
| `IssueGeslotenOpUtc`, `IssueStatusGecontroleerdOpUtc` | door de retentietimer uit GitHub gelezen |
| `IsGeanonimiseerd`, `GeanonimiseerdOpUtc` | zie §6 |
| `mta_inserted`, `mta_modified` | UTC |

**`avg.FeedbackTelemetrie`** — technische context per bron (`omgeving`, `console`, `netwerk`,
`navigatie`), al geredigeerd; `ON DELETE CASCADE` vanaf `avg.Feedback`; 90 dagen.

**`avg.FeedbackInzageLog`** — wie (object-ID + naam-momentopname) bekeek welke melding: `Actie`
(`lijst` · `detail` · `publiceer`), `FeedbackId`, `Filter`, tijdstip. Zonder foreign key; 24 maanden.

## 5. Technische context en redactie (DPO-voorwaarde)

De widget verzamelt, **standaard aan**, de laatste vijf **console-fouten** (`wwwroot/js/feedback-telemetry.js`),
**mislukte API-aanroepen** (methode, pad zonder querystring, statuscode, tijd, correlatie-id),
het **navigatiespoor** (routes) en **browser/schermbreedte**. Vóór verzending kan de gebruiker onder
*"Technische gegevens meesturen"* zien wat er precies wordt meegestuurd en het vinkje uitzetten
(dan gaat er niets mee). Het paneel toont letterlijk dezelfde tekst die de server bewaart
(`FeedbackTelemetrie.NaarTekst()`).

Er worden nooit schermafbeeldingen, formulierinhoud, cookies of `localStorage` verzameld.

**Redactie — op twee plekken, uit één bron** (`Planner.Shared/Feedback/FeedbackRedactie.cs`, in de
browser gelinkt als bronbestand, #1461): eerst in de browser (wat het paneel toont is al geredigeerd),
daarna opnieuw op de server (een client wordt niet vertrouwd). Idempotent. **Een nieuwe contextbron
loopt altijd door `FeedbackRedactie`/`FeedbackTelemetrieSaneerder`** (`Planner.Shared/Feedback/`); een
tweede set regex-regels voor redactie of saneren elders is dezelfde fout als #692 (teamnormalisatie) en
een architectuurschending.

| Wat | Wordt |
|---|---|
| e-mailadressen | `[e-mail]` |
| GUID's | `[id]` |
| JWT's en `Bearer …` | `[token]` |
| lange token-achtige reeksen (≥ 32 tekens) | `[token]` |
| Nederlandse telefoonnummers | `[telefoon]` |
| cijferreeksen van 7+ cijfers (lid-, rekening-, BSN-achtig) | `[nummer]` |
| querystring en fragment achter een pad of URL | weggeknipt |
| numerieke padsegmenten (`/teams/12345`) | `/teams/{id}` |
| waarde achter sleutels als `naam`, `name`, `displayName`, `email`, `password`, `token`, `code`, … | `[verborgen]` |
| de weergavenaam van de melder (en naamdelen ≥ 3 tekens) in vrije tekst | `[naam]` |

Daarnaast: maximaal vijf items per soort, 300 tekens per item, de route van de pagina zonder
querystring, en een regex-timeout (bij pathologische invoer volgt `[niet te redigeren]` — liever
niets dan ongeredigeerd).

**Restrisico (expliciet):** een naam van een *ander* in vrije tekst wordt niet herkend — dat is met
een patroon niet te onderscheiden van gewone woorden. Daarom: de technische context gaat nooit het
publieke issue in, is alleen voor beheerders zichtbaar (met inzagelog) en verdwijnt na 90 dagen. De
geredigeerde context gaat wél mee in de AI-structureerprompt (de oorzaak concreet benoemen); het
PII-gate-pad op de uiteindelijke titel/body blijft onverkort draaien.

## 6. Bewaartermijnen en de retentietimer

| Gegeven | Termijn | Mechanisme |
|---|---|---|
| Identiteit van de melder (`MelderObjectId`, `MelderNaam`) | zolang het issue open is **+ 24 maanden na sluiting**; daarna `NULL` + `IsGeanonimiseerd` | timer `CleanupFeedback` |
| Een melding die nooit is gepubliceerd | identiteit 24 maanden na aanmaak (anders houdt een vergeten melding de identiteit eeuwig vast) | idem |
| Meldingstekst (`Onderwerp`, `Beschrijving`, `IssueBody`, issue-verwijzing) | onbeperkt — staat na publicatie al openbaar | — |
| Technische context | 90 dagen | idem |
| Inzagelog | 24 maanden | idem |

**Timer `CleanupFeedback`** (beide tiers, dagelijks 05:15 UTC; code: `Planner.Endpoints/Feedback/FeedbackEndpointCore.VoerRetentieTimerAsync`
→ `Planner.Shared/Feedback/FeedbackRetentieCore`):

1. **Eerst** de issuestatus synchroniseren: voor maximaal 200 gepubliceerde, nog niet geanonimiseerde
   meldingen (oudst-gecontroleerd eerst) leest de timer via de GitHub API of het issue gesloten is en
   legt `IssueGeslotenOpUtc` vast (of wist het bij een heropend issue). Dat gebeurt uitsluitend via
   `EgressGuard`; faalt een controle of is GitHub niet geconfigureerd, dan wordt er **niets** op een
   gok geanonimiseerd — alleen op wat al bekend was.
2. **Dan** de retentie: SQL Server `avg.sp_CleanupFeedback`, Postgres
   `PostgresCleanupProcedures.CleanupFeedbackAsync` (zelfde regels; tijdgrenzen in C#/parameters).
   Resultaat: uitsluitend tellingen in het log, nooit inhoud.

De termijnen staan als constanten in `Planner.Shared/Feedback/FeedbackRetentie.cs`. Verandert de
eigenaar een termijn, dan is dat één plek plus dit document en het verwerkingsregister.

## 7. Grondslag en DPIA-notitie

* **Grondslag:** art. 6 lid 1 sub f AVG — gerechtvaardigd belang: de beheerder van de applicatie moet
  een melding kunnen navragen bij de melder en misbruik kunnen herleiden. Eigenaarsbesluit
  2026-10-03. Afweging: de melder is een vrijwilliger/medewerker met een eigen account; er wordt
  alleen een pseudoniem (object-ID) en de weergavenaam bewaard, geen e-mailadres; de identiteit
  verdwijnt vanzelf; de tekst op GitHub bevat geen identiteit.
* **Pseudoniem, geen anonimisering** (art. 4 lid 5): de beheerder kan via de eigen tenant een object-ID
  bij een persoon vinden. Verdwijnt het account, dan wordt het object-ID een wees en is de melding
  feitelijk geanonimiseerd.
* **Pre-DPIA-notitie (art. 35 / verantwoordingsplicht art. 5 lid 2):** geen DPIA verplicht — geen
  grootschalige verwerking van bijzondere categorieën, geen systematische monitoring van openbare
  ruimten, geen profilering met rechtsgevolgen. De technische context is beperkt tot de laatste
  vijf items per soort, geredigeerd, optioneel en na 90 dagen gewist. Heroverweeg bij uitbreiding
  van de verzamelde gegevens (bijv. sessie-opnames) — dat is dan wél een DPIA-vraag.
* **Geen export:** het overzicht heeft bewust geen CSV-/Excel-export; dat zou een ongecontroleerde
  kopie buiten bewaartermijn en inzagelog om maken.

## 8. Verwerkingsregister (AVG art. 30)

| Veld | Inhoud |
|---|---|
| Verwerking | Feedbackmeldingen van gebruikers van de beheertoepassing, inclusief technische context en inzagelog |
| Verwerkingsverantwoordelijke | De club die deze installatie beheert (eigen fork en eigen Azure-resources) |
| Doel | Meldingen behandelen, navraag bij de melder, de applicatie verbeteren, verantwoording over wie meldingen inzag |
| Grondslag | Art. 6 lid 1 sub f — gerechtvaardigd belang (afweging in §7) |
| Betrokkenen | Gebruikers met een account (rol `admin` of `user`) van de beheertoepassing |
| Categorieën persoonsgegevens | Entra object-ID (pseudoniem), weergavenaam (momentopname), vrije tekst van de melding, technische context (geredigeerd), bij inzage: object-ID + naam van de beheerder |
| Bijzondere categorieën | Geen verzameld. Vrije tekst kan er toevallig een noemen; de PII-gate en het voorbeeld voor publicatie zijn daar het vangnet |
| Ontvangers | GitHub, Inc. (alleen de gepubliceerde tekst, zonder identiteit en zonder technische context) · Microsoft Azure (hosting, database) · OpenAI (tekst van de melding voor structurering; geen identiteit, geredigeerde context) — alle drie verwerkers |
| Doorgifte buiten de EER | Ja (GitHub, OpenAI, mogelijk Microsoft) op basis van de standaardcontractbepalingen van de leverancier |
| Bewaartermijnen | zie §6: identiteit open + 24 mnd na sluiting · tekst onbeperkt · technische context 90 dagen · inzagelog 24 mnd |
| Beveiliging | Entra single tenant · rol server-side afgedwongen (`admin` voor overzicht; melder uit het principal) · schema `avg` met RLS · redactie client- én server-side · PII-gate vóór AI en vóór publicatie · geen export · inzagelog · geen identiteit in publieke tekst |

Bij een nieuwe club-installatie: neem deze rij over in het eigen register en pas grondslag en termijnen
aan als de afweging anders uitvalt.

## 9. Verzoeken van betrokkenen

**Inzage/rectificatie:** zoek de melding(en) van de betrokkene in het overzicht (de naam is zichtbaar
zolang de melding niet is geanonimiseerd).

**Wissing (art. 17)** van een melding met een bestaand publiek issue:

1. Reageer binnen één maand (art. 12 lid 3).
2. Zet `MelderObjectId` + `MelderNaam` op `NULL` voor de rijen van deze persoon
   (`IsGeanonimiseerd = 1`); daarna is de melding geen persoonsgegeven over de verzoeker meer. De
   tekst zelf blijft.
3. Bevat de issue-body geen persoonsgegevens (normaal, de gate blokkeert e-mail en telefoon): leg uit
   dat de issuetekst buiten art. 17 valt.
4. Bevat de body wél persoonsgegevens: **verwijder het issue** (sluiten of bewerken volstaat niet —
   GitHub bewaart de bewerkingsgeschiedenis).
5. Art. 17 lid 2 is een inspanningsverplichting voor kopieën bij derden: dien zo nodig een
   verwijderverzoek in bij de zoekmachine en meld eerlijk dat volledige verwijdering niet te
   garanderen is.
6. Leg het verzoek vast (datum, aard, afhandeling) zonder de gewiste gegevens opnieuw op te schrijven.

## 10. Limieten

* `validate`/`preview` (betaalde AI-aanroep): maximaal 30 per 10 minuten **per gebruiker** (in-memory).
* `submit`: maximaal 3 meldingen per 10 minuten **per gebruiker** (gemeten in `avg.Feedback`, dus
  gedeeld over instances) en een vangnet van 30 per uur voor de hele club. Boven de limiet:
  HTTP 429 met een mensentaal-melding.
* **AI-dienst niet beschikbaar (#1487).** `validate`, `preview` en `submit` hebben alle drie de AI nodig
  (titel, body en PII-gates). Is `IChatClient` niet geregistreerd — lokaal door de EgressGuard (#857) of
  zonder API-sleutel — dan geven ze HTTP **503** `{ "error": "...", "aiBeschikbaar": false }` in plaats van
  een 500. Eén plek: `FeedbackEndpointCore.ControleerAiBeschikbaar`, op beide tiers aangeroepen vóór de
  invoerpoort (er wordt dus geen AI-limietslot verbruikt). De widget toont de servermelding; er wordt niets
  bewaard of verstuurd.

## 11. Code-overzicht

| Onderdeel | Plek |
|---|---|
| Kern: gates, AI-prompts, issuebody, redactie, retentie, opslagcontract | `Planner.Shared/Feedback/` |
| Endpoint-orkestratie (beleid, limieten, publicatie, inzagelog, timer) | `Planner.Endpoints/Feedback/` |
| Opslag per tier | `FunctionApp/Feedback/SqlFeedbackStore.cs`, `FunctionApp.Postgres/Feedback/PostgresFeedbackStore.cs` |
| Endpoints per tier | `Feedback/FeedbackFunction.cs`, `Admin/AdminFeedbackFunction.cs`, `Admin/CleanupFeedbackFunction.cs` |
| Widget en overzicht | `BlazorAdmin/Shared/FeedbackWidget.razor(.cs)`, `BlazorAdmin/Pages/Feedback.razor(.cs)`, `BlazorAdmin/Services/ClientTelemetryService.cs`, `BlazorAdmin/wwwroot/js/feedback-telemetry.js` |
| Tests | `Planner.Shared.Tests/Feedback/`, `Planner.Endpoints.Tests/Feedback/`, `FunctionApp.Postgres.Tests/Feedback/FeedbackStoreIntegrationTests.cs` (echte database), `FunctionApp.Tests/Feedback/SqlFeedbackStoreIntegrationTests.cs` (SQL Server, lokaal met `SQLSERVER_TEST_CONNECTION_STRING`), `BlazorAdmin.Tests/FeedbackWidgetTests.cs` |
