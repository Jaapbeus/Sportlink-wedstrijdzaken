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
│  2. Van intern domein?            → overslaan (mark as read)                  │
│  3. Afzender uitgesloten?         → overslaan (mark as read)                  │
│  4. MessageId al verwerkt?        → overslaan (mark as read, idempotent)      │
└───────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
   AI-classificatie (GPT-4o-mini)
        │
        ├─ BuitenScope ──→ label "Geen AI antwoord" + mark as read → STOP
        │
        ├─ BeschikbaarheidCheck ──→ PlannerService → template (zie §2)
        ├─ HerplanVerzoek        ──→ PlannerService → template (zie §2)
        └─ Bevestiging           ──→ template "Bedankt voor je bevestiging"
                │
                ▼
        Antwoord versturen
        (Review-mode: naar coördinator; Live-mode: naar afzender)
```

---

## 1. Wanneer wordt er GEEN AI-antwoord verstuurd?

### 1a. Label "Geen AI antwoord" in Outlook

Dit label wordt geplaatst wanneer de AI de email classificeert als **BuitenScope**. Er wordt dan geen reply verstuurd. De coördinator handelt de email zelf af.

De AI classificeert een email als BuitenScope als:
- Het verzoek gaat **niet** over veldbeschikbaarheid of het herplannen van een wedstrijd
- Er worden **meerdere verschillende teams** genoemd zonder duidelijk verband
- De AI-classificatie levert geen herkend type op (technische fallback)

Voorbeelden die als BuitenScope worden aangemerkt:
- Vragen over contributie, kleedkamers, sleutels, of andere niet-planningszaken
- Emails met discussie over meerdere teams in één bericht

Emails met **meerdere datums voor hetzelfde team** zijn géén BuitenScope — die worden verwerkt als `BeschikbaarheidCheck` met een multi-datum antwoord.

### 1b. Stille skip — geen label, geen DB-entry

Deze emails worden stil overgeslagen (mark as read, verder niets):

| Reden | Conditie |
|---|---|
| Eigen mailbox | Afzender = de mailbox zelf (voorkomt loops) |
| Intern domein | Afzender eindigt op het ingestelde interne domein (`AppSettings.InternDomein`) |
| Uitgesloten adres | Afzender staat in `dbo.UitgeslotenEmailAdressen` |
| Al verwerkt | MessageId staat al in `dbo.EmailVerwerking` (idempotent) |

---

## 2. Welke template wordt verstuurd?

### Classificatie: BeschikbaarheidCheck

Iemand vraagt of een datum/tijd/veld beschikbaar is.

#### Template A — Beschikbaar

**Wanneer:** Er is een veld beschikbaar op de gevraagde datum en tijd.

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

**Wanneer:** De afzender noemt een tegenstander maar het bijbehorende VRC-team kan niet worden herleid.

```
Goedemiddag {voornaam},

We kunnen de wedstrijd van {tegenstander} niet vinden in ons programma.
Tegen welk VRC-team zou deze wedstrijd zijn?
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
- **BCC:** alle niet-trainer begeleiders van het betreffende VRC-team uit `avg.Teambegeleiding`; als alleen trainers beschikbaar zijn, gaan die als BCC
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

De conditie: noch `teamNaam` (beginnend met "VRC") noch `leeftijdsCategorie` is aanwezig in de AI-classificatie.

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
- Teamnaam (bijv. VRC JO13-1)
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
| F | BeschikbaarheidCheck | VRC-team niet herleidbaar uit verzoek | "Tegen welk VRC-team zou dit zijn?" |
| G | HerplanVerzoek | Verzoek te laat én geen alternatieven | Uitleg richtlijn + contact coördinator |
| M | HerplanVerzoek | Verzoek te laat maar alternatieven beschikbaar | Alternatieven tonen + BCC naar team-begeleiders |
| H | HerplanVerzoek | Gewenste herplandatum opgegeven | Beschikbaarheid op gewenste datum |
| I | HerplanVerzoek | Geen gewenste datum, alternatieven gezocht | Eerdere/latere mogelijkheden op speeldag |
| J | Bevestiging | Afzender bevestigt eerder voorstel | "Bedankt voor je bevestiging" |
| K | (alle) | Technische fout tijdens verwerking | "Fout opgetreden, coördinator op hoogte" |
| L | BeschikbaarheidCheck | Geen teamnaam én geen leeftijdscategorie | Vraag om ontbrekende informatie |
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
