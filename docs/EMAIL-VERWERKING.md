# Email-verwerking — gedrag en templates

Dit document beschrijft wanneer de emailprocessor een antwoord verstuurt, welke template wordt gebruikt, en wanneer een email handmatig door de coördinator moet worden afgehandeld.

---

## Verwerkingsstroom (overzicht)

```
Inkomend email (ongelezen in Graph-mailbox)
        │
        ▼
┌─ Voorfilters ─────────────────────────────────────────────────────────────────┐
│  1. Van eigen mailbox?            → overslaan (mark as read)                  │
│  2. Afzender uitgesloten?         → overslaan (mark as read)                  │
│  3. Verwerking al definitief af?  → overslaan (mark as read, idempotent)      │
│     Niet-definitieve status?      → rij hergebruiken, opnieuw proberen (§1d)  │
└───────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
   AI-classificatie FASE 1 (GPT-4o-mini)
        │
        ├─ BuitenScope ──→ label "Geen AI antwoord" + mark as read → STOP
        │
        └─ Overige types: DB INSERT + reply-detectie
                │
                ▼
        ┌─ Reply-detectie (#323) ────────────────────────────────────────────────┐
        │  Is email reply op eerder beantwoord bericht (zelfde ConversationId)?  │
        │  Ja:                                                                    │
        │    → UpdateReplyStatus (IsReplyOpOnsAntwoord=1, ReplyOpVerwerkingId)   │
        │    → DetecteerCorrectieAsync: is dit een correctie op classificatie?   │
        │       → Correctie: INSERT ClassificatieCorrectie (pending validatie)   │
        └────────────────────────────────────────────────────────────────────────┘
                │
                ▼
        ┌─ Few-shot herclassificatie (#323) ─────────────────────────────────────┐
        │  Zijn er gevalideerde leermomenten (ClassificatieCorrectie)?           │
        │  Ja: herclassificeer met few-shot voorbeelden in system prompt         │
        │  Nee: gebruik classificatie uit FASE 1                                  │
        └────────────────────────────────────────────────────────────────────────┘
                │
                ▼
        ├─ BeschikbaarheidCheck  ──→ PlannerService → template (zie §2)
        ├─ HerplanVerzoek        ──→ PlannerService → template (zie §2)
        ├─ TeamContactOpvragen   ──→ GetTeamleiderContactAsync → StuurTeamContactDoorAsync (BCC coördinator, Reply-To afzender) → auto-reply "doorgestuurd"
        └─ Bevestiging           ──→ template "Bedankt voor je bevestiging"
                │
                ▼
        ┌─ Reply-policy (#572) — ReplyPolicy.Bepaal ──────────────────────────────┐
        │  BeschikbaarheidCheck, planning mogelijk        → GEEN antwoord         │
        │  BeschikbaarheidCheck, planning niet mogelijk   → antwoord met reden    │
        │  Multidatum, gemengde uitkomst                  → antwoord per datum    │
        │  Andere verzoektypes                            → altijd antwoord       │
        └────────────────────────────────────────────────────────────────────────┘
                │
                ▼
        Antwoord versturen
        (Review-mode: naar coördinator; Live-mode: naar afzender)
```

### Zelflerend classificatiesysteem (#323)

Wanneer een afzender repliet op een AI-antwoord en het verzoek was verkeerd geclassificeerd:

1. **Detectie**: `DetecteerReplyOpOnsAntwoordAsync` koppelt de reply aan de oorspronkelijke verwerking via `ConversationId`.
2. **Correctie**: `DetecteerCorrectieAsync` vraagt de AI of de reply aangeeft dat de classificatie onjuist was.
3. **Opslaan**: Een nieuw `ClassificatieCorrectie`-record wordt aangemaakt (status: *te beoordelen*).
4. **Validatie**: De beheerder valideert of wijst af via `/leermomenten` in de Admin GUI.
5. **Leren**: Bij de volgende email worden gevalideerde leermomenten als few-shot voorbeelden in de system prompt meegegeven, waardoor de AI hetzelfde type fout niet herhaalt.

**Beheer via Admin GUI:** `/leermomenten` — toont pending/validated/rejected correcties met valideer/afwijzen knoppen.

### Teamherkenning (#692)

Welk team een bericht betreft, wordt niet meer per plek met eigen tekstregels bepaald. Er is één
vertaallaag die een schrijfwijze uit een e-mail herleidt tot een team uit `dbo.Teams`. Die laag
staat standaard uit en is per deployment aan te zetten met de app setting `TeamResolverMode`
(`off` | `shadow` | `on`).

Bij een aanduiding die écht dubbelzinnig is — "13-1" kan JO13-1 of MO13-1 zijn — wordt niet gegokt:
er volgt óf een keuze uit een korte kandidatenlijst, óf de vraag wordt teruggelegd.

Volledige onderbouwing, uitrolstappen en de logregels om op te zoeken:
[ARCHITECTUUR-TEAMRESOLUTIE.md](ARCHITECTUUR-TEAMRESOLUTIE.md).

---

## 1. Wanneer wordt er GEEN AI-antwoord verstuurd?

### 1a. Label "Handmatige planning" — planning is mogelijk (#572)

**Functionele eis (eigenaar, 2026-07-25):** een automatisch antwoord is er om een blokkade te
melden. Kan de wedstrijd gewoon ingepland worden, dan volgt géén automatische mail — de
coördinator plant handmatig in en koppelt zelf terug.

| Situatie | Automatisch antwoord? |
|---|---|
| Één datum gevraagd, planning **mogelijk** | ❌ Nee — label `Handmatige planning`, status `GeenAntwoordNodig` |
| Één datum gevraagd, planning **niet mogelijk** | ✓ Ja — met duidelijke reden (bezet, teamconflict, geen venster) |
| Meerdere datums, **gemengde** uitkomst (≥1 wel, ≥1 niet) | ✓ Ja — per datum staat in het antwoord wat wel en niet kan |
| Meerdere datums, **alle** datums mogelijk | ❌ Nee — zelfde redenering als één datum |
| Meerdere datums, **geen enkele** datum mogelijk | ✓ Ja |
| Wedstrijd staat al in Sportlink | ✓ Ja — informatief, de aanvrager moet dit weten |
| Team/tegenstander onbekend | ✓ Ja — niet planbaar |
| Herplanverzoek, teamcontact, bevestiging | ✓ Ja — altijd; deze types hebben geen "wel/niet planbaar"-uitkomst |

De beslissing zit in `FunctionApp/Email/ReplyPolicy.cs` (puur, zonder DB of Graph) en is
volledig gedekt door `FunctionApp.Tests/Email/ReplyPolicyTests.cs`.

Onderdrukte antwoorden zijn zichtbaar in de Admin GUI onder **Instellingen → Email verwerking
(laatste 24u) → Handmatige planning**, en in de mailbox aan het Outlook-label.
Er wordt niets stil weggegooid: het bericht wordt normaal verwerkt en gelogd in
`planner.EmailVerwerking` met status `GeenAntwoordNodig`.

**Review-mode blijft leidend:** staat `EmailReviewMode=true`, dan gaat er nooit een mail uit —
die check komt vóór de reply-policy.

In review-mode wordt het voorgestelde antwoord wél opgebouwd en opgeslagen (#712). De rij in
`planner.EmailVerwerking` krijgt status `Review`, met het voorstel in `AntwoordEmail` en
`VerstuurdNaar` leeg. Voorheen werd het antwoord in deze modus helemaal niet gebouwd en bleef de
status op `Verwerkt` staan — dezelfde waarde als een mislukte verzending — waardoor er niets te
reviewen viel.

> **Let op:** het voorstel is nog niet in de Admin GUI zichtbaar. `AdminEmailLogRepository` geeft
> `AntwoordEmail` bewust nooit terug (AVG: de body kan persoonsgegevens bevatten), dus het voorstel
> is nu alleen in de database te bekijken. Een admin-only endpoint hiervoor is een aparte afweging.

### 1b. Label "Geen AI antwoord" in Outlook

Dit label wordt geplaatst wanneer de AI de email classificeert als **BuitenScope**. Er wordt dan geen reply verstuurd. De coördinator handelt de email zelf af.

De AI classificeert een email als BuitenScope als:
- Het verzoek gaat **niet** over veldbeschikbaarheid of het herplannen van een wedstrijd
- Er worden **meerdere verschillende teams** genoemd zonder duidelijk verband
- De AI-classificatie levert geen herkend type op (technische fallback)

Voorbeelden die als BuitenScope worden aangemerkt:
- Vragen over contributie, kleedkamers, sleutels, of andere niet-planningszaken
- Emails met discussie over meerdere teams in één bericht

Emails met **meerdere datums voor hetzelfde team** zijn géén BuitenScope — die worden verwerkt als `BeschikbaarheidCheck` met een multi-datum antwoord.

### 1c. Stille skip — geen label, geen DB-entry

Deze emails worden stil overgeslagen (mark as read, verder niets):

| Reden | Conditie |
|---|---|
| Eigen mailbox | Afzender = de mailbox zelf (voorkomt loops) |
| Uitgesloten adres | Afzender staat in `dbo.UitgeslotenEmailAdressen` |
| Al afgerond | Het MessageId staat in `planner.EmailVerwerking` **met een definitieve eindstatus** (idempotent) |

> Twee correcties op een eerdere versie van deze tabel:
> - Een filter "van intern domein → overslaan" bestaat niet in de code. `EmailBatchFilterService`
>   kent alleen de eigen mailbox en de uitsluitingslijst (#708).
> - "Al verwerkt" was te ruim: het bestaan van een rij is niet genoeg meer. Zie §1d — een rij met
>   een niet-definitieve status wordt juist opnieuw verwerkt (#712).

### 1d. Opnieuw proberen en opgeven (#712)

Een bericht wordt niet meer als afgehandeld beschouwd zodra er een rij bestaat, maar pas als de
verwerking een **definitieve** eindstatus heeft (`AntwoordVerstuurd`, `GeenAntwoordNodig`,
`BuitenScope`) of `VerstuurdNaar` gevuld is. Staat er een niet-definitieve status, dan wordt de
bestaande rij hergebruikt en de verwerking opnieuw uitgevoerd.

`planner.EmailVerwerking.Pogingen` houdt bij hoe vaak dat is gebeurd. Na drie pogingen geeft de
verwerking op: status `Fout` met de melding dat er is opgegeven, bericht als gelezen gemarkeerd, en
een foutregel in de log. Dat is nodig omdat de poll de tien **oudste** ongelezen berichten pakt —
zonder die grens zouden tien blijvend falende berichten alle nieuwe post tegenhouden en bij elke
poll opnieuw AI-kosten maken.

Bij een afgebroken AI-classificatie door een quotalimiet wordt géén poging geteld: er is dan niets
geprobeerd wat aan het bericht zelf ligt.

---

## 2. Welke template wordt verstuurd?

### Classificatie: BeschikbaarheidCheck

Iemand vraagt of een datum/tijd/veld beschikbaar is.

#### Template A — Beschikbaar

**Wanneer:** Er is een veld beschikbaar op de gevraagde datum en tijd.

> **Sinds #572 wordt deze template in de e-mailflow niet meer automatisch verstuurd.**
> Is planning mogelijk, dan gaat er geen mail uit (zie §1a) en plant de coördinator handmatig in.
> De template blijft in gebruik voor multidatum-antwoorden met gemengde uitkomst (Template D),
> voor de e-mailtester in de Admin GUI, en als DB-override via `dbo.EmailTemplateInstellingen`.

```
Goedemiddag {voornaam},

Op {datum} is {veld} beschikbaar om {aanvangstijd}. De wedstrijd eindigt om {eindtijd}.

[optioneel: "Er zijn op deze dag nog diverse andere mogelijkheden."]
[optioneel: waarschuwingen, bijv. minder lichtminuten]

Met vriendelijke groet,
{afzenderNaam}
[Geautomatiseerd antwoord namens {coordinatorNaam}]
```

#### Template B — Niet beschikbaar, alternatieven beschikbaar

**Wanneer:** De gevraagde tijd is bezet, maar er zijn andere vensters beschikbaar.

```
Goedemiddag {voornaam},

Op {datum} om {aanvangstijd} is helaas geen ruimte.

[Beschikbare mogelijkheden:]     ← als voorkeurstijd bekend is
[Op {datum} zijn de volgende mogelijkheden:]   ← zonder voorkeurstijd
  - {veld}: {van}–{tot} [opmerking]
  - ...

Met vriendelijke groet, ...
```

#### Template C — Niet beschikbaar, geen alternatieven

**Wanneer:** Er is geen beschikbaarheid op de gevraagde datum.

```
Goedemiddag {voornaam},

Op {datum} is helaas geen veld beschikbaar.
[optioneel: reden]

Met vriendelijke groet, ...
```

#### Template D — Multi-datum

**Wanneer:** De email vraagt naar meerdere datums voor hetzelfde team (bijv. "kan het 18 of 25 mei?").

```
Goedemiddag {voornaam},

{datum 1}:
  {beschikbaarheidstatus datum 1}

{datum 2}:
  {beschikbaarheidstatus datum 2}

Laat weten welke optie(s) de voorkeur hebben, dan plannen we het in.

Met vriendelijke groet, ...
```

#### Template E — Wedstrijd al ingepland

**Wanneer:** De PlannerService detecteert dat de gevraagde wedstrijd al in het programma staat.

```
Goedemiddag {voornaam},

De wedstrijd {naam} staat al ingepland op {datum} om {tijd} op {veld}.

Met vriendelijke groet, ...
```

#### Template F — Team onbekend

**Wanneer:** De afzender noemt een tegenstander maar het bijbehorende team van de club kan niet worden herleid.

```
Goedemiddag {voornaam},

We kunnen de wedstrijd van {tegenstander} niet vinden in ons programma.
Tegen welk team van onze club zou deze wedstrijd zijn?
Dan kunnen we de beschikbaarheid voor je controleren.

Met vriendelijke groet, ...
```

---

### Classificatie: HerplanVerzoek

Iemand wil een bestaande wedstrijd verplaatsen.

#### Template M — Herplan in overleg (te laat + alternatieven beschikbaar)

**Wanneer:** Het herplanverzoek is te laat ingediend (binnen de deadline), maar er zijn toch alternatieven beschikbaar op dezelfde speeldag. In dat geval kan het verzoek alleen in overleg met de begeleiders van het team worden behandeld.

**Verzendlogica:**
- **Primaire ontvanger:** de externe afzender (trainer/begeleider andere club)
- **BCC:** alle niet-trainer begeleiders van het betreffende team uit `avg.Teambegeleiding`; als alleen trainers beschikbaar zijn, gaan die als BCC
- **Geen begeleiders in DB:** email gaat alleen naar de coördinator-mailbox, met een noot dat de Teambegeleiding-CSV geïmporteerd moet worden
- **Review-mode:** email naar de reviewer; BCC-informatie wordt in de review-header getoond (namen + adressen) zonder werkelijke BCC te versturen

**Template configureerbaar:** via Admin → E-mailtemplates → key `HerplanInOverleg`.

Beschikbare placeholders: `{{aanhef}}`, `{{voornaam}}`, `{{wedstrijd}}`, `{{datum}}`, `{{tijd}}`, `{{veld}}`, `{{team}}`, `{{deadlineDagen}}`, `{{dagenTotWedstrijd}}`, `{{alternatieven}}`, `{{bccOpmerking}}`.

```
Goedemiddag {voornaam},

De wedstrijd {naam} staat gepland op {datum} om {tijd} op {veld}. Dat is over {N} dag(en).

Een herplanverzoek moet minimaal {deadlineDagen} dagen voor de wedstrijd worden ingediend.
Omdat de wedstrijd al binnen die termijn valt, kan dit verzoek niet automatisch worden verwerkt.

We zien dat er die dag nog ruimte is:
  - {veld} om {tijd} (eindigt {eindtijd})
  - ...

Dit verzoek kan alleen in overleg met de begeleiders van {team} worden behandeld.
[We hebben de begeleiders op de hoogte gesteld en als BCC toegevoegd aan dit bericht.]

Neem contact op met de coördinator om dit verder te bespreken.

Met vriendelijke groet, ...
```

---

#### Template G — Te laat ingediend

**Wanneer:** Het herplanverzoek is te laat ingediend (binnen de deadline) **en er zijn géén alternatieven** beschikbaar op de betreffende speeldag.

```
Goedemiddag {voornaam},

De wedstrijd {naam} staat gepland op {datum} om {tijd} op {veld}.
Dat is over {N} dag(en).

Volgens onze richtlijn moet een herplanverzoek minimaal {deadlineDagen} dagen
voor de wedstrijd worden ingediend. Omdat de wedstrijd al binnen die termijn
valt, kunnen we het verzoek niet meer automatisch verwerken.

Neem voor uitzonderingen rechtstreeks contact op met de coördinator.

Met vriendelijke groet, ...
```

Als de wedstrijd niet gevonden kan worden:

```
Je herplanverzoek is helaas te laat ingediend.
Een herplanverzoek moet minimaal {deadlineDagen} dagen voor de wedstrijd worden ingediend.
```

#### Template H — Gewenste datum opgegeven

**Wanneer:** De afzender vraagt expliciet om een specifieke nieuwe datum (bijv. "kan het op 25 mei?").

```
Goedemiddag {voornaam},

De wedstrijd {naam} staat momenteel gepland op {datum} om {tijd} op {veld}.

Op {gewensteDatum} zijn de volgende mogelijkheden:
  - {veld}: {van}–{tot}
  - ...

[of: "Op {gewensteDatum} is {veld} beschikbaar om {tijd} (eindigt {eindtijd})."]
[of: "Helaas is er op {gewensteDatum} geen ruimte beschikbaar."]

Met vriendelijke groet, ...
```

#### Template I — Alternatieven op huidige dag

**Wanneer:** Geen gewenste datum opgegeven; de processor zoekt eerdere en latere mogelijkheden op dezelfde speeldag.

Richtingdetectie op basis van trefwoorden in onderwerp + body:
- "vervroegen", "eerder", "naar voren" → eerdere mogelijkheden eerst
- "verlaten", "later", "naar achter" → latere mogelijkheden eerst
- Geen trefwoorden → beide richtingen

```
Goedemiddag {voornaam},

De wedstrijd {naam} staat gepland op {datum} om {tijd} op {veld}.

Eerdere mogelijkheden:
  - {veld} om {tijd} (eindigt {eindtijd})
  - ...

Latere mogelijkheden:
  - {veld} om {tijd} (eindigt {eindtijd})
  - ...

Laat weten welke optie de voorkeur heeft.

Met vriendelijke groet, ...
```

---

### Classificatie: Bevestiging

#### Template J — Bevestiging ontvangen

**Wanneer:** De afzender bevestigt een eerder voorstel ("ja", "akkoord", "dat is goed", etc.).

```
Goedemiddag {voornaam},

Bedankt voor je bevestiging. Het verzoek is geregistreerd en wordt
door de coördinator verwerkt.

Met vriendelijke groet, ...
```

---

### Ontbrekende gegevens

#### Template L — Leeftijdscategorie of teamnaam onbekend

**Wanneer:** De email bevat geen teamnaam en geen leeftijdscategorie. Zonder deze informatie kan de speelduur niet worden bepaald en is een beschikbaarheidsbeoordeling niet mogelijk.

De conditie: noch `teamNaam` noch `leeftijdsCategorie` is aanwezig in de AI-classificatie.

Voorbeeldcase: onderwerp "Morgenavond", body "Kunnen wij morgenavond een oefenwedstrijd spelen?" — geen team, geen categorie.

```
Goedemiddag {voornaam},

Om de veldplanning te beoordelen missen we nog de volgende informatie:
- Leeftijdscategorie (bijv. JO13, MO15, senioren)

Kun je dit aanvullen? Dan kijken we wat er mogelijk is.

Met vriendelijke groet, ...
```

Als meerdere velden ontbreken worden ze allemaal opgesomd:
```
- Leeftijdscategorie (bijv. JO13, MO15, senioren)
- Teamnaam (bijv. [ClubCode] JO13-1)
```

---

### Fout tijdens verwerking

#### Template K — Technische fout

**Wanneer:** Er treedt een onverwachte fout op tijdens classificatie of plannerverwerking.

```
Goedemiddag {voornaam},

Er is een fout opgetreden bij het verwerken van je verzoek.
De coördinator is op de hoogte gesteld en neemt zo snel mogelijk contact op.

Met vriendelijke groet, ...
```

---

## 3. Overzichtstabel — alle templates

| # | Classificatie | Situatie | Template |
|---|---|---|---|
| A | BeschikbaarheidCheck | Veld beschikbaar op gevraagde tijd | Beschikbaarheidsbevestiging met veld + tijden |
| B | BeschikbaarheidCheck | Gevraagde tijd bezet, alternatieven beschikbaar | Lijst met beschikbare vensters |
| C | BeschikbaarheidCheck | Geen beschikbaarheid op de datum | "Helaas geen veld beschikbaar" |
| D | BeschikbaarheidCheck | Meerdere datums gevraagd (zelfde team) | Per-datum sectie + keuzevraag |
| E | BeschikbaarheidCheck | Wedstrijd staat al ingepland | "Staat al ingepland op datum om tijd" |
| F | BeschikbaarheidCheck | Team van de club niet herleidbaar uit verzoek | "Tegen welk team van onze club zou dit zijn?" |
| G | HerplanVerzoek | Verzoek te laat én geen alternatieven | Uitleg richtlijn + contact coördinator |
| M | HerplanVerzoek | Verzoek te laat maar alternatieven beschikbaar | Alternatieven tonen + BCC naar team-begeleiders |
| H | HerplanVerzoek | Gewenste herplandatum opgegeven | Beschikbaarheid op gewenste datum |
| I | HerplanVerzoek | Geen gewenste datum, alternatieven gezocht | Eerdere/latere mogelijkheden op speeldag |
| J | Bevestiging | Afzender bevestigt eerder voorstel | "Bedankt voor je bevestiging" |
| K | (alle) | Technische fout tijdens verwerking | "Fout opgetreden, coördinator op hoogte" |
| L | BeschikbaarheidCheck | Geen teamnaam én geen leeftijdscategorie | Vraag om ontbrekende informatie |
| N | TeamContactOpvragen | "Wie is de trainer/coach van [team]?" | Auto-reply "uw vraag is doorgestuurd" + coach ontvangt vraag (BCC coördinator, Reply-To = afzender) |
| — | BuitenScope | Email gaat niet over planning | Geen antwoord — label "Geen AI antwoord" |

---

## 4. Handtekening en aanhef

**Aanhef** is tijdsgebonden (tijdzone Nederland):
- vóór 12:00 → "Goedemorgen"
- 12:00–18:00 → "Goedemiddag"
- na 18:00 → "Goedenavond"

**Handtekening:**
```
Met vriendelijke groet,

{AfzenderNaam}
[Geautomatiseerd antwoord namens {CoördinatorNaam}]
{CoördinatorFunctie}
```

**Review-mode prefix** (als `EmailReviewMode=true`):
Boven elk antwoord verschijnt een blok met het originele adres, onderwerp, classificatietype en template-key — zodat de coördinator kan zien wat het systeem heeft bepaald vóór doorsturing.
