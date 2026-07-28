# Email-verwerking — gedrag en templates

Dit document beschrijft wanneer de emailprocessor een antwoord verstuurt, welke template wordt gebruikt, en wanneer een email handmatig door de coördinator moet worden afgehandeld.

> **De code is leidend.** Staat er iets in dit document dat je niet in `FunctionApp/Email/` of
> `FunctionApp/Processing/BerichtPipeline.cs` terugvindt, dan is dit document fout — meld het als
> issue. Een eerdere versie beweerde dat élke claim hier geverifieerd was; die garantie bleek zelf
> onwaar en is daarom weggehaald: ze verkleinde juist de kans dat een lezer nog controleerde.

---

## Verwerkingsstroom (overzicht)

De verwerking heeft twee fasen. Dat is geen implementatiedetail: in fase 1 blijft de database
slapen, zodat een inbox met alleen niet-planningsmail geen Azure SQL-kosten en geen wektijd kost.
De keerzijde is dat de idempotentie-check (§1d) pas in **fase 2** kan draaien — een bericht dat al
definitief is afgehandeld kost dus nog wél een AI-classificatie.

```
FASE 1 — Graph API + AI, database blijft slapen
────────────────────────────────────────────────────────────────────────────────
De 10 OUDSTE ongelezen berichten uit de inbox (Graph: top=10, orderby receivedDateTime)
        │
        ▼
┌─ Voorfilters (EmailBatchFilterService) ───────────────────────────────────────┐
│  1. Van eigen mailbox (GraphMailbox)?  → overslaan (mark as read)             │
│  2. Afzender uitgesloten (cache)?      → overslaan (mark as read)             │
│  Cold start: uitsluitingslijst eerst laden (fail-closed) en opnieuw filteren  │
└──────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
   AI-classificatie (BerichtAiService)
        │
        ├─ BuitenScope ──────→ label "Geen AI antwoord" + mark as read → STOP
        │                      (géén rij in planner.EmailVerwerking)
        ├─ Classificatie mislukt → onthouden voor fase 2 (pogingenteller, §1d)
        └─ Overige types ─────→ door naar fase 2
        │
        ▼
   Alles buiten scope én geen classificatiefouten? → STOP, database blijft slapen

FASE 2 — database wordt gewekt
────────────────────────────────────────────────────────────────────────────────
   WaitForDatabaseAsync + AppSettings laden
        │  niet bereikbaar → eenmalig noodmail naar de mailbox → STOP
        ▼
   Uitsluitingslijst verversen · mislukte classificaties vastleggen (pogingenteller)
        │
        ▼  per bericht (VerwerkEmailAsync)
┌──────────────────────────────────────────────────────────────────────────────┐
│  a. Hercheck uitsluitingslijst (verse DB-lijst)  → overslaan                  │
│  b. Idempotentie-besluit op de EINDSTATUS (§1d):                              │
│       definitief afgerond → overslaan · te vaak mislukt → opgeven             │
│       niet-definitief     → bestaande rij hergebruiken, poging +1             │
│     nieuw                 → INSERT planner.EmailVerwerking                    │
└──────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ Reply-detectie (#323) ──────────────────────────────────────────────────────┐
│  Reply op een eerder door ons beantwoord bericht (zelfde ConversationId)?     │
│    → UpdateReplyStatus (IsReplyOpOnsAntwoord=1, ReplyOpVerwerkingId)          │
│    → DetecteerCorrectieAsync: is dit een correctie op de classificatie?       │
│       → ja: INSERT ClassificatieCorrectie (status: te beoordelen)             │
└──────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ Few-shot herclassificatie (#323) ───────────────────────────────────────────┐
│  Zijn er gevalideerde leermomenten? → herclassificeer met few-shot voorbeelden │
│  Levert dat BuitenScope op → status BuitenScope + label, géén antwoord (#712)  │
└──────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
   status Geclassificeerd → VerwerkMetPlannerAsync → status Verwerkt
        │
        ├─ BeschikbaarheidCheck  ──→ PlannerService → template (zie §2)
        ├─ HerplanVerzoek        ──→ PlannerService → template (zie §2)
        ├─ TeamContactOpvragen   ──→ GetTeamleiderContactAsync (alleen: is er een coach?)
        └─ Bevestiging           ──→ vaste tekst, geen plannercheck
        │
        ▼
┌─ Review-mode? (EmailReviewMode=true) ────────────────────────────────────────┐
│  Er gaat NOOIT een mail uit. Het voorstel wordt opgebouwd en opgeslagen:      │
│  status Review, AntwoordEmail gevuld, VerstuurdNaar leeg.                     │
│  Bericht krijgt label "Geen AI antwoord" + mark as read. → STOP (§1a)         │
└──────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ Reply-policy (#572) — ReplyPolicy.Bepaal ───────────────────────────────────┐
│  BeschikbaarheidCheck, planning mogelijk        → GEEN antwoord               │
│  BeschikbaarheidCheck, planning niet mogelijk   → antwoord met reden          │
│  Multidatum, gemengde uitkomst                  → antwoord per datum          │
│  Andere verzoektypes                            → altijd antwoord             │
└──────────────────────────────────────────────────────────────────────────────┘
        │
        ├─ Onderdrukken → status GeenAntwoordNodig + label "Handmatige planning"
        │
        ▼
   Antwoord versturen naar de afzender (reply in dezelfde conversatie)
        │
        ▼  alleen NA een daadwerkelijk verstuurd antwoord:
        ├─ HerplanVerzoek      → interne notificatie naar de teamleider (#66)
        └─ TeamContactOpvragen → vraag doorsturen naar de coach (#168)
```

> **De twee vervolgacties onderaan zijn voorwaardelijk.** Ze draaien uitsluitend als het antwoord
> aan de afzender echt is verstuurd. In review-mode, bij een onderdrukt antwoord (§1a) of bij een
> Graph-verzendfout wordt de teamleider dus **niet** genotificeerd en gaat de begeleidingsvraag
> **niet** naar de coach. Beide acties falen bovendien stil (alleen een logregel): een fout hierin
> onderbreekt de hoofdverwerking niet.

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
vertaallaag die een schrijfwijze uit een e-mail herleidt tot een team uit `dbo.Teams`.

Sinds #700 is dat het **enige** pad: de oude regex-normalisatie en de stringheuristiek die "eigen
team" uit de vorm van de tekst raadde, zijn verwijderd. Er is ook géén schakelaar meer om de laag uit
te zetten — een instelling die de enige teamherkenning kan uitschakelen is geen veiligheidsventiel
maar een voetangel. (Een eerdere versie van dit document beschreef een app setting
`TeamResolverMode` met standen `off`/`shadow`/`on`; die bestaat niet meer.)

Welk van de twee genoemde teams het eigen team is, wordt bepaald door te kijken wélke in de teamlijst
staat — niet meer door te raden op spaties of clubprefix.

Bij een aanduiding die écht dubbelzinnig is — "13-1" kan JO13-1 of MO13-1 zijn — wordt niet gegokt:
er volgt óf een keuze uit een korte kandidatenlijst, óf de vraag wordt teruggelegd.

Is de teamlijst leeg (bijvoorbeeld direct na een deploy, vóór de eerste nachtelijke synchronisatie),
dan wordt hij eenmalig alsnog opgebouwd. Lukt dat niet, dan wordt er níet verwerkt — dat is beter dan
berichten koppelen zonder teamherkenning.

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
| Multidatum zonder resultaten | ✓ Ja — niet planbaar |
| Wedstrijd staat al in Sportlink | ✓ Ja — informatief, de aanvrager moet dit weten |
| Team/tegenstander onbekend | ✓ Ja — niet planbaar |
| Geen bruikbare datum uit het bericht te halen | ✓ Ja — vraag om een datum (zie Template P) |
| Plannerrespons niet leesbaar | ✓ Ja — fail-open: zwijgen zou onopgemerkt blijven |
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
reviewen viel. Zou de reply-policy het antwoord onderdrukken, dan wordt er in review-mode ook geen
voorstel gebouwd: de rij krijgt dan status `Review` zonder `AntwoordEmail`.

Het bericht krijgt in review-mode het label **"Geen AI antwoord"** (niet "Handmatige planning") en
wordt als gelezen gemarkeerd.

> **Let op:** het voorstel is nog niet in de Admin GUI zichtbaar. `AdminEmailLogRepository` geeft
> `AntwoordEmail` bewust nooit terug (AVG: de body kan persoonsgegevens bevatten), dus het voorstel
> is nu alleen in de database te bekijken. Een admin-only endpoint hiervoor is een aparte afweging.

### 1b. Label "Geen AI antwoord" in Outlook

Dit label wordt geplaatst wanneer de AI de email classificeert als **BuitenScope**. Er wordt dan geen reply verstuurd. De coördinator handelt de email zelf af.

De AI classificeert een email als BuitenScope als:
- Het verzoek gaat **niet** over veldbeschikbaarheid, het herplannen van een wedstrijd, of teambegeleiding
- Er worden **meerdere verschillende teams** genoemd zonder duidelijk verband
- De AI-classificatie levert geen herkend type op (technische fallback: onbekend type → `buiten_scope`)

Voorbeelden die als BuitenScope worden aangemerkt:
- Vragen over contributie, kleedkamers, sleutels, of andere niet-planningszaken
- Emails met discussie over meerdere teams in één bericht

Emails met **meerdere datums voor hetzelfde team** zijn géén BuitenScope — die worden verwerkt als `BeschikbaarheidCheck` met een multi-datum antwoord.

**Twee wegen naar dit label, met verschillend gevolg voor de database:**

| Wanneer | Rij in `planner.EmailVerwerking`? |
|---|---|
| BuitenScope bij de eerste classificatie (fase 1) | Nee — de database blijft slapen |
| BuitenScope pas ná few-shot herclassificatie (fase 2) | Ja — status `BuitenScope` (#712) |

Het tweede geval bestaat omdat de herclassificatie met leermomenten alsnog "buiten scope" kan
opleveren. Zonder die afhandeling ging er tóch een automatisch antwoord uit, puur afhankelijk van
of er gevalideerde leermomenten in de database stonden.

### 1c. Stille skip — geen antwoord, geen verdere verwerking

Deze emails worden stil overgeslagen (mark as read, verder niets):

| Reden | Conditie | Rij in de DB? |
|---|---|---|
| Eigen mailbox | Afzender = de mailbox zelf (`GraphMailbox`) — voorkomt loops | Nee |
| Uitgesloten adres | Afzender staat in `dbo.UitgeslotenEmailAdressen` (`Actief = 1`) | Nee |
| Al afgerond | Het MessageId staat in `planner.EmailVerwerking` **met een definitieve eindstatus** (idempotent) | Ja — die bestond al |

> Drie correcties op een eerdere versie van deze tabel:
> - Een filter "van intern domein → overslaan" bestaat niet in de code. `EmailBatchFilterService`
>   kent alleen de eigen mailbox en de uitsluitingslijst (#708).
> - "Al verwerkt" was te ruim: het bestaan van een rij is niet genoeg meer. Zie §1d — een rij met
>   een niet-definitieve status wordt juist opnieuw verwerkt (#712).
> - "Al afgerond" is géén voorfilter: die check zit in fase 2, ná de AI-classificatie. Zo'n bericht
>   kost dus nog wel een AI-call. De eerste twee filters draaien wél vóór de AI (#708).

De uitsluitingslijst wordt twee keer gecontroleerd: eerst tegen de proces-cache (fase 1, vóór de
AI), daarna nog eens tegen de verse DB-lijst (fase 2). De cache heeft een geldigheidsduur van vijftien
minuten; is die verlopen — of is de cache nog nooit gevuld, zoals bij een cold start — dan wordt de
lijst eerst uit de database geladen vóórdat er geclassificeerd wordt. Een adres dat de beheerder net
heeft uitgesloten kan dus nog maximaal die vijftien minuten een AI-call kosten; een antwoord krijgt
het niet, want de hercheck vóór het opslaan gebruikt altijd een verse lijst. Lukt dat laden niet,
dan stopt de verwerking (fail-closed, #423) — liever geen verwerking dan een AI-antwoord naar een
adres dat juist uitgesloten was.

### 1d. Opnieuw proberen en opgeven (#712)

Een bericht wordt niet meer als afgehandeld beschouwd zodra er een rij bestaat, maar pas als de
verwerking een **definitieve** eindstatus heeft (`AntwoordVerstuurd`, `GeenAntwoordNodig`,
`BuitenScope`) of `IsBeantwoord` gezet is. Staat er een niet-definitieve status, dan wordt de
bestaande rij hergebruikt en de verwerking opnieuw uitgevoerd.

> **`IsBeantwoord` en niet `VerstuurdNaar` (#718).** Tot deze wijziging was `VerstuurdNaar` de grens.
> Die kolom bevat een e-mailadres en wordt door de AVG-retentie na 30 dagen op `NULL` gezet, terwijl
> dezelfde kolom óók de replydetectie aanstuurde. Een reply op dag 31 — normaal bij een verzoek dat
> weken vooruit ligt — werd daardoor niet meer als reply herkend, dus werd er ook geen leermoment
> meer vastgelegd: het zelflerende deel werkte alleen binnen 30 dagen. `IsBeantwoord` is een boolean
> zonder persoonsgegeven en overleeft de anonimisering bewust; `VerstuurdNaar` blijft alleen als
> terugvalpad meedoen voor rijen van vóór de migratie.

`planner.EmailVerwerking.Pogingen` houdt bij hoe vaak dat is gebeurd. Na drie pogingen geeft de
verwerking op: status `Fout` met de melding dat er is opgegeven, bericht als gelezen gemarkeerd, en
een foutregel in de log. Dat is nodig omdat de poll de tien **oudste** ongelezen berichten pakt —
zonder die grens zouden tien blijvend falende berichten alle nieuwe post tegenhouden en bij elke
poll opnieuw AI-kosten maken.

Een mislukte AI-classificatie krijgt dezelfde teller: het bericht blijft ongelezen (het komt de
volgende poll terug), maar er wordt wel een rij aangemaakt of een poging bijgeteld. Zonder rij was
er geen teller en kwam zo'n bericht eeuwig terug, elke keer met een nieuwe AI-call.

Bij een afgebroken AI-classificatie door een quotalimiet wordt géén poging geteld: er is dan niets
geprobeerd wat aan het bericht zelf ligt.

#### Afgebroken tussen versturen en vastleggen (#716)

De volgorde was versturen → vastleggen → als gelezen markeren, dus de grens tegen een dubbel antwoord
werd geschreven nádat de mail al weg was. Bij een harde afbreking precies daartussen (functie-time-out
van tien minuten, host-recycle, scale-in) was het antwoord verstuurd terwijl er in de database niets
van te zien was — en stuurde de volgende poll een tweede antwoord.

Daarom wordt vlak vóór de verzendpoging `VerzendPogingOpUtc` gezet, en meteen weer gewist zodra het
versturen aantoonbaar mislukt. Wat overblijft is een ondubbelzinnig signaal:

| `VerzendPogingOpUtc` | `IsBeantwoord` | Betekenis | Volgende poll |
|---|---|---|---|
| leeg | 0 | nog niet aan versturen toe | verwerkt opnieuw |
| leeg | 0, na verzendfout | versturen is aantoonbaar mislukt | probeert opnieuw (#712) |
| **gevuld** | **0** | **verstuurd of misschien verstuurd, uitkomst onbekend** | **verstuurt niet; status `Review`** |
| gevuld of leeg | 1 | antwoord is vastgelegd | slaat over |

De onbekende uitkomst weegt zwaarder dan de pogingenteller: het is geen mislukte poging die je nog
eens mag proberen. Het bericht wordt als gelezen gemarkeerd zodat het de wachtrij niet bezet houdt, en
de coördinator ziet het met status `Review` terug in het email-log. Kan de intentie zelf niet worden
vastgelegd, dan wordt er niet verstuurd — een poging uitstellen is lichter dan een dubbel antwoord.

#### Eén leermoment per correctie (#715)

Omdat een hervatte verwerking dezelfde rij hergebruikt, draaide de correctiedetectie bij elke poging
opnieuw en leverde hetzelfde (origineel, correctie)-paar tot drie identieke leermomenten op. Die moest
de beheerder allemaal apart valideren, en meervoudig goedgekeurd woog hetzelfde voorbeeld zwaarder in
de AI-prompt dan bedoeld. `UQ_ClassificatieCorrectie_Paar` is nu de harde grens; de repository doet er
een `IF NOT EXISTS` voor zodat de normale herhaling geen fout oplevert.

---

## 2. Welke template wordt verstuurd?

**DB-override gaat vóór op de hardcoded tekst.** Voor vijf classificaties zoekt
`EmailTemplateService.GetTemplateAsync` eerst een actieve rij in `dbo.EmailTemplateInstellingen`
(per `ClubCode`, gecacht met TTL 5 minuten). Is die er, dan wordt die gebruikt en gelden de
hardcoded teksten hieronder niet. Zie §3a voor de keys en de placeholders.

De hardcoded teksten hieronder zijn de teksten die de code opbouwt als er géén DB-override is.
Ze zijn letterlijk uit `BerichtResponseGenerator` overgenomen.

### Classificatie: BeschikbaarheidCheck

Iemand vraagt of een datum/tijd/veld beschikbaar is.

#### Template A — Beschikbaar

**Wanneer:** Er is een veld beschikbaar op de gevraagde datum en tijd (`Beschikbaar` én een
`Toewijzing`).

> **Sinds #572 wordt deze template in de e-mailflow niet meer automatisch verstuurd.**
> Is planning mogelijk, dan gaat er geen mail uit (zie §1a) en plant de coördinator handmatig in.
> De template blijft in gebruik voor multidatum-antwoorden met gemengde uitkomst (Template D),
> voor de e-mailtester in de Admin GUI (die de reply-policy niet toepast), en als DB-override via
> `dbo.EmailTemplateInstellingen`.

```
{aanhef} {voornaam},

Op {datum} is {veld} beschikbaar om {aanvangstijd}. De wedstrijd eindigt om {eindtijd}.

[" Er zijn op deze dag nog diverse andere mogelijkheden." — bij 4 of meer vensters]
[optioneel: "Let op: {waarschuwingen}", bijv. minder lichtminuten]

{handtekening — zie §4}
```

#### Template B — Gevraagde tijd bezet, vensters beschikbaar

**Wanneer:** Niet beschikbaar, er zijn wel beschikbare vensters, én de afzender gaf een
voorkeurstijd op.

```
{aanhef} {voornaam},

Op {datum} om {aanvangstijd} is helaas geen ruimte. Beschikbare mogelijkheden:
- {veld}: beschikbaar van {van} tot {tot} [({opmerking})]
- ...

Geef een voorkeurstijd door, dan plannen we het in.
```

#### Template C — Open vraag zonder voorkeurstijd

**Wanneer:** Er zijn beschikbare vensters en de afzender gaf géén voorkeurstijd op.

```
{aanhef} {voornaam},

Op {datum} zijn de volgende mogelijkheden:
- {veld}: beschikbaar van {van} tot {tot} [({opmerking})]
- ...

Geef een voorkeurstijd door, dan plannen we het in.
```

#### Template D — Niet beschikbaar, alternatieve starttijden

**Wanneer:** Niet beschikbaar, geen vensters, maar wel alternatieve starttijden.

```
{aanhef} {voornaam},

Op {datum} [om {aanvangstijd}] is helaas geen ruimte. Alternatieven:
- {veld} om {aanvangstijd} (eindigt {eindtijd})
- ...

[optioneel: "Let op: {waarschuwingen}"]
```

#### Template E — Teamconflict

**Wanneer:** Het team heeft al een wedstrijd op de gevraagde datum (`TeamConflict`). De
veldbeschikbaarheid is dan niet eens gecheckt, dus "geen veld beschikbaar" zou hier onjuist zijn.

```
{aanhef} {voornaam},

{reden}

Hierdoor kan op {datum} geen oefenwedstrijd worden ingepland.
```

#### Template F — Geen beschikbaarheid (terugvaltekst)

**Wanneer:** Geen van de bovenstaande gevallen — geen toewijzing, geen vensters, geen
alternatieven, geen teamconflict.

```
{aanhef} {voornaam},

Op {datum} is helaas geen veld beschikbaar. [{reden}]
```

#### Template G — Multi-datum

**Wanneer:** De email vraagt naar meerdere datums voor hetzelfde team (bijv. "kan het 18 of 25 mei?").

Per datum wordt dezelfde logica als Template A–F toegepast, in een eigen sectie met de datum vet.

```
{aanhef} {voornaam},

**{datum 1}:** {veld} is beschikbaar om {tijd} (eindigt {eindtijd}).

**{datum 2}:** Om {tijd} is helaas geen ruimte. Beschikbare mogelijkheden:
- {veld}: beschikbaar van {van} tot {tot}

Laat weten welke optie(s) de voorkeur hebben, dan plannen we het in.
```

#### Template H — Wedstrijd al ingepland

**Wanneer:** De pipeline vindt de gevraagde wedstrijd al in het programma
(`FindMatchByOpponentAsync` op de gevraagde datum).

```
{aanhef} {voornaam},

De wedstrijd {wedstrijd} staat al ingepland op {datum} om {tijd}[ op {veld}].
```

Kunnen de wedstrijdgegevens niet worden opgehaald:

```
Er is een fout opgetreden bij het ophalen van de wedstrijdgegevens.
De coördinator neemt zo snel mogelijk contact op.
```

#### Template I — Team onbekend

**Wanneer:** De afzender noemt een tegenstander maar er is helemaal geen wedstrijd tegen die
tegenstander te vinden, ook niet op een andere datum.

```
{aanhef} {voornaam},

We kunnen de wedstrijd van {tegenstander} niet vinden in ons programma.
Tegen welk van onze teams zou deze wedstrijd zijn?
Dan kunnen we de beschikbaarheid voor je controleren.
```

#### Template P — Geen bruikbare datum

**Wanneer:** Er is geen geldige datum uit onderwerp, body of AI-classificatie te halen. Zonder
datum heeft een plannercheck geen zin; die zou een interne foutstring ("Ongeldige datum: .") in het
antwoord aan de afzender laten belanden.

Deze tekst loopt via de template-route (interne key `datum_onbekend`) zodat hij dezelfde
review-prefix en handtekening krijgt als de andere antwoorden. De key staat **niet** in de
Admin GUI en is dus niet via `dbo.EmailTemplateInstellingen` te overschrijven.

```
{aanhef} {voornaam},

Bedankt voor je bericht. We konden er geen concrete datum uit opmaken.
Kun je aangeven welke datum of datums je in gedachten hebt?
Dan controleren we de beschikbaarheid van onze velden voor je.
```

#### Weergaveregels bij beschikbaarheidsantwoorden

Deze regels bepalen wat de afzender wél en niet in de lijst ziet:

| Regel | Gedrag |
|---|---|
| **"einde dag"** | Een eindtijd van 21:00 of later wordt als `einde dag` weergegeven in plaats van als kloktijd (sluittijd sportpark) |
| **"diverse andere mogelijkheden"** | Deze zin verschijnt alleen bij 4 of meer beschikbare vensters |
| **Maximaal 3 alternatieven** | Alternatieve starttijden worden afgekapt op de eerste 3 |
| **Grasveld weglaten** | Het veldtype komt uit `dbo.Velden.VeldType` — niet uit het veldnummer (#705). Zijn er genoeg kunstgras-opties, dan worden grasveld-opties weggelaten — behalve een grasvenster dat in een tijdsblok valt waar geen kunstgras beschikbaar is. Is het veldtype van een veld onbekend, dan wordt dat veld nooit weggefilterd |

---

### Classificatie: HerplanVerzoek

Iemand wil een bestaande wedstrijd verplaatsen.

De deadline komt uit `herplanDeadlineDagen` in `dbo.AppSettings` (terugval: 8 dagen).

#### Template J — Te laat ingediend

**Wanneer:** Het aantal dagen tot de wedstrijd is kleiner dan `herplanDeadlineDagen`. Dit is de
enige uitkomst voor een te laat verzoek — er wordt in dat geval niet meer naar alternatieven
gezocht.

```
{aanhef} {voornaam},

De wedstrijd {wedstrijd} staat gepland op {datum} om {tijd} op {veld}. Dat is over {N} dag(en).

Volgens onze richtlijn moet een herplanverzoek minimaal {deadlineDagen} dagen voor de wedstrijd
worden ingediend. Omdat de wedstrijd al binnen die termijn valt, kunnen we het verzoek niet meer
automatisch verwerken.

Neem voor uitzonderingen rechtstreeks contact op met de coördinator.
```

Als de wedstrijd niet gevonden kan worden:

```
{aanhef} {voornaam},

Je herplanverzoek is helaas te laat ingediend. Volgens onze richtlijn moet een herplanverzoek
minimaal {deadlineDagen} dagen voor de wedstrijd worden ingediend.
```

#### Template K — Gewenste datum opgegeven

**Wanneer:** De afzender vraagt expliciet om een specifieke nieuwe datum (bijv. "kan het op 25 mei?")
en het verzoek is op tijd.

```
{aanhef} {voornaam},

De wedstrijd {wedstrijd} staat momenteel gepland op {datum} om {tijd} op {veld}.

Op {gewensteDatum} zijn de volgende mogelijkheden:
- {veld}: beschikbaar van {van} tot {tot}
- ...
```

Varianten op de laatste alinea, afhankelijk van de plannerrespons:

| Uitkomst | Tekst |
|---|---|
| Niet beschikbaar | `Helaas is er op {gewensteDatum} geen ruimte beschikbaar.` [+ `{reden}`] |
| Vensters beschikbaar | `Op {gewensteDatum} zijn de volgende mogelijkheden:` + lijst |
| Eén toewijzing | `Op {gewensteDatum} is {veld} beschikbaar om {tijd} (eindigt {eindtijd}).` |
| Beschikbaar zonder details | `Op {gewensteDatum} is er ruimte beschikbaar.` |
| Wedstrijd niet gevonden | `Er is geen wedstrijd gevonden voor {team} op {datum}.` |

#### Template L — Alternatieven op de huidige speeldag

**Wanneer:** Geen gewenste datum opgegeven; de processor zoekt eerdere en latere mogelijkheden op
dezelfde speeldag.

Alternatieven die minder dan 30 minuten van de huidige aanvangstijd afwijken vallen af — anders
krijgt de afzender "alternatieven" die praktisch hetzelfde tijdstip zijn. Per richting worden
maximaal 3 opties getoond.

Richtingdetectie op basis van trefwoorden in onderwerp + body:

| Trefwoorden in de tekst | Richting |
|---|---|
| `vervroeg`, `eerder`, `naar voren` (en geen van de andere groep) | vervroegen |
| `verlaat`, `verlat`, ` later`, `naar achter` (en geen van de andere groep) | verlaten |
| Geen trefwoorden, of trefwoorden uit **beide** groepen | beide richtingen |

```
{aanhef} {voornaam},

De wedstrijd {wedstrijd} staat gepland op {datum} om {tijd} op {veld}.

Eerdere mogelijkheden:
- {veld} om {tijd} (eindigt {eindtijd})
- ...

Latere mogelijkheden:
- {veld} om {tijd} (eindigt {eindtijd})
- ...

Laat weten welke optie de voorkeur heeft.
```

Drie terugvalvarianten:

| Situatie | Tekst |
|---|---|
| Wedstrijd niet gevonden | `Er is geen wedstrijd gevonden voor {team} op {datum}. Controleer de teamnaam en datum en probeer het opnieuw.` |
| Geen alternatieven uit de planner | `De wedstrijd {wedstrijd} op {datum} om {tijd} op {veld} kan helaas niet verplaatst worden. Er zijn geen alternatieven beschikbaar.` |
| Alle alternatieven binnen 30 minuten weggefilterd | `... Het is een volle wedstrijddag en er zijn helaas geen zinvolle alternatieven beschikbaar.` |

**KNVB-notitie.** Heeft de AI-classificatie een `knvbNotitie` opgeleverd, dan wordt aan elk
herplanantwoord toegevoegd:

```
Let op: {knvbNotitie} Zie ook: https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/regelen-dagelijkse-praktijk/verplaatsen-van-wedstrijden
```

#### Interne notificatie naar de teamleider (#66)

Naast het antwoord aan de afzender gaat er bij een herplanverzoek een **aparte interne mail** naar
de teamleider uit `avg.Teambegeleiding`, mits team en datum bekend zijn en het antwoord aan de
afzender daadwerkelijk verstuurd is. Is er geen teamleider gevonden, dan wordt dit stil
overgeslagen (alleen een logregel).

```
Onderwerp: Herplanverzoek ontvangen voor {team} op {datum}

Hoi {naam},

Er is een herplanverzoek ontvangen voor {team} op {datum}.

De coördinator heeft automatisch gereageerd op dit verzoek. Je hoeft zelf geen actie te
ondernemen, maar we willen je op de hoogte houden.

Als je vragen hebt over dit herplanverzoek, neem dan contact op met de veldplanner.

Met vriendelijke groet,
{plannerAfzenderNaam}
```

---

### Classificatie: TeamContactOpvragen

Iemand vraagt wie de trainer, coach, begeleider of teamleider van een team is.

#### Template M — Auto-reply "doorgestuurd"

**Wanneer:** Altijd bij dit type. Contactgegevens worden nooit gedeeld met de vraagsteller.

```
{aanhef} {voornaam},

Uw vraag over de begeleiding van {team} is doorgestuurd. De begeleider neemt rechtstreeks
contact met u op. Contactgegevens worden niet gedeeld conform AVG.
```

Is er geen teamnaam bekend, dan staat er "de begeleiding van het opgegeven team".

#### Doorsturen naar de coach (#168)

**Pas ná** het versturen van de auto-reply hierboven wordt de oorspronkelijke vraag doorgestuurd
naar de coach. Is er geen coach gevonden in `avg.Teambegeleiding`, dan gebeurt dit niet.

| Veld | Waarde |
|---|---|
| **Aan** | De coach/teamleider uit `avg.Teambegeleiding` |
| **Reply-To** | De oorspronkelijke afzender — zodat de coach rechtstreeks kan antwoorden |
| **BCC** | De **veldplanner** (`plannerEmailAdres` in `dbo.AppSettings`) als AVG-audit-kopie |
| **Onderwerp** | `[{team}] vraag van {afzenderNaam}` |

> **Ontbreekt `plannerEmailAdres`**, dan wordt de vraag alsnog doorgestuurd, maar **zonder**
> BCC-audit-kopie, met een waarschuwing in de log. De audit-kopie is een AVG-maatregel, geen
> nice-to-have: vul dit adres in bij Instellingen. Dit stond eerder op de sleutel `coordinatorEmail`
> — die bestaat niet in `dbo.AppSettings`, waardoor de kopie nooit uitging terwijl code én
> documentatie dat wel beloofden (#712).

---

### Classificatie: Bevestiging

#### Template N — Bevestiging ontvangen

**Wanneer:** De afzender bevestigt een eerder voorstel ("ja", "akkoord", "dat is goed", etc.).
Er wordt geen plannercheck uitgevoerd.

```
{aanhef} {voornaam},

Bedankt voor je bevestiging. Het verzoek is geregistreerd en wordt door de coördinator verwerkt.
```

---

### Classificatie: BuitenScope

#### Template O — Handmatige afhandeling

**Wanneer:** Deze tekst wordt in de e-mailflow **niet** verstuurd: een BuitenScope-bericht krijgt
alleen het Outlook-label (§1b). De tekst is bereikbaar via de e-mailtester in de Admin GUI en via
een DB-override op de key `buiten_scope`.

```
{aanhef} {voornaam},

Bedankt voor je bericht. Dit verzoek vereist handmatige afhandeling en is ter beoordeling bij
de coördinator neergelegd.
```

---

### Fout tijdens verwerking — géén e-mail

Treedt er een onverwachte fout op tijdens classificatie of plannerverwerking, dan krijgt de
afzender **geen** bericht. De verwerking legt vast:

- status `Fout` in `planner.EmailVerwerking`, met een gesaniteerde foutmelding (e-mailadressen
  verwijderd, afgekapt op 1000 tekens)
- een foutregel in de log met alleen het `MessageId` — nooit onderwerp, adres of body (AVG #210)
- het bericht blijft **ongelezen** en komt de volgende poll terug (zie §1d, max 3 pogingen)

> `BerichtResponseGenerator.BouwFoutAntwoord` bestaat wel, maar wordt door geen enkele
> productiecode aangeroepen. Een eerdere versie van dit document beschreef die tekst als
> "Template K — Technische fout" die naar de afzender ging; dat gebeurt niet (#708).

**Twee uitzonderingen die wél mail versturen — naar de eigen mailbox, niet naar de afzender:**

| Situatie | Mail |
|---|---|
| Database niet bereikbaar | Eenmalige noodmail "URGENT: Database niet bereikbaar — email-processor gepauzeerd". Herhaalt niet tot de database weer bereikbaar is |
| OpenAI-quota overschreden | Noodmail "URGENT: OpenAI quota overschreden"; maximaal één keer per 24 uur |

Beide noodmails bevatten een gecategoriseerde foutomschrijving, nooit de ruwe exception-tekst
(#425).

---

## 3. Overzichtstabel — alle templates

| # | Classificatie | Situatie | Opbouwmethode |
|---|---|---|---|
| A | BeschikbaarheidCheck | Veld beschikbaar op gevraagde tijd | `BouwBeschikbaarheidAntwoord` |
| B | BeschikbaarheidCheck | Gevraagde tijd bezet, vensters beschikbaar | `BouwBeschikbaarheidAntwoord` |
| C | BeschikbaarheidCheck | Open vraag zonder voorkeurstijd | `BouwBeschikbaarheidAntwoord` |
| D | BeschikbaarheidCheck | Niet beschikbaar, alternatieve starttijden | `BouwBeschikbaarheidAntwoord` |
| E | BeschikbaarheidCheck | Team heeft al een wedstrijd op die datum | `BouwBeschikbaarheidAntwoord` |
| F | BeschikbaarheidCheck | Geen beschikbaarheid (terugvaltekst) | `BouwBeschikbaarheidAntwoord` |
| G | BeschikbaarheidCheck | Meerdere datums gevraagd (zelfde team) | `BouwMultiDatumBeschikbaarheidAntwoord` |
| H | BeschikbaarheidCheck | Wedstrijd staat al ingepland | `BouwWedstrijdAlIngeplandAntwoord` |
| I | BeschikbaarheidCheck | Tegenstander niet in het programma te vinden | `BouwTeamOnbekendAntwoord` |
| P | BeschikbaarheidCheck | Geen bruikbare datum in het bericht | interne template `datum_onbekend` |
| J | HerplanVerzoek | Verzoek te laat ingediend | `BouwHerplanTeLaatAntwoord` |
| K | HerplanVerzoek | Gewenste herplandatum opgegeven | `BouwHerplanGewensteDatumAntwoord` |
| L | HerplanVerzoek | Geen gewenste datum, alternatieven gezocht | `BouwHerplanAntwoord` |
| M | TeamContactOpvragen | "Wie is de trainer/coach van [team]?" | `BouwTeamContactAutoReply` |
| N | Bevestiging | Afzender bevestigt eerder voorstel | `BouwBevestigingAntwoord` |
| O | BuitenScope | Alleen via e-mailtester of DB-override | `BouwBuitenScopeAntwoord` |
| — | BuitenScope | E-mailflow: geen antwoord, label "Geen AI antwoord" | — |
| — | (alle) | Technische fout: geen e-mail, status `Fout` | — |

### 3a. Wat is via de Admin GUI aanpasbaar?

**Admin GUI → E-mailtemplates** (`/email-templates`). Een template die je daar aanmaakt en op
`Actief` zet, vervangt de volledige hardcoded tekst voor die classificatie. Templates zijn
per club opgeslagen (`ClubCode` in `dbo.EmailTemplateInstellingen`).

| Template-key | Vervangt |
|---|---|
| `beschikbaarheid_check` | Template A–F (níet G, H, I of P — die hebben eigen logica die vóór de DB-lookup komt) |
| `herplan_verzoek` | Template L (níet J of K — die komen vóór de DB-lookup) |
| `bevestiging` | Template N |
| `team_contact_opvragen` | Template M |
| `buiten_scope` | Template O |

> **Belangrijk:** de DB-lookup gebeurt pas nádat de bijzondere gevallen zijn afgehandeld. Een
> override op `beschikbaarheid_check` heeft dus geen effect op een multidatum-antwoord, op "staat al
> ingepland", op "team onbekend" of op "geen datum". Zo ook: `herplan_verzoek` raakt niet aan een
> te laat verzoek of een verzoek met een gewenste datum.

**Ondersteunde placeholders — dit is de volledige lijst.** Alles wat de code invult zit in
`BerichtResponseGenerator.BouwAangepasteAntwoord`; een placeholder die daar niet in staat blijft
letterlijk in de mail staan. De Admin GUI toont dezelfde zes onder het body-veld.

| Placeholder | Inhoud |
|---|---|
| `{{aanhef}}` | Tijdsgebonden aanhef: Goedemorgen / Goedemiddag / Goedenavond |
| `{{voornaam}}` | Eerste woord van de afzendernaam (leeg als die ontbreekt) |
| `{{datum}}` | De datum uit de classificatie, als `dinsdag 26 mei 2026` |
| `{{team}}` | Genormaliseerde teamnaam uit de classificatie |
| `{{tegenstander}}` | Tegenstander uit de classificatie |
| `{{aanvangstijd}}` | Gevraagde aanvangstijd uit de classificatie |

Substitutie is case-insensitief; een niet-gevulde waarde wordt een lege string. Ook het veld
**Onderwerp** ondersteunt deze placeholders. Blijft het onderwerp leeg, dan wordt het
`Re: {origineel onderwerp}`.

> **Deze lijst is uitputtend.** Een eerdere versie van dit document noemde een template
> `HerplanInOverleg` met BCC naar begeleiders en de placeholders `{{wedstrijd}}`, `{{tijd}}`,
> `{{veld}}`, `{{deadlineDagen}}`, `{{dagenTotWedstrijd}}`, `{{alternatieven}}` en
> `{{bccOpmerking}}`. Geen daarvan bestaat: niet de template-key, niet de BCC-logica en niet één van
> die zeven placeholders (#708). Er is ook geen pad in de code dat bij een te laat herplanverzoek
> alsnog alternatieven zoekt — een te laat verzoek levert altijd Template J.
>
> Ook `Template L — Leeftijdscategorie of teamnaam onbekend` uit die versie bestaat niet: er is geen
> tekst die ontbrekende velden opsomt. Ontbreekt alleen de datum, dan volgt Template P; ontbreekt de
> leeftijdscategorie of teamnaam, dan gaat het verzoek gewoon naar de planner en komt de uitkomst
> daarvan in het antwoord terecht.

### 3b. Statussen in `planner.EmailVerwerking`

De `Status`-kolom bevat de naam van een `EmailStatus`-waarde (`FunctionApp/Email/BerichtModels.cs`).
Dit zijn ze alle acht:

| Status | Betekenis | Definitief? |
|---|---|---|
| `Ontvangen` | Rij aangemaakt, verwerking nog niet begonnen | Nee |
| `Geclassificeerd` | AI-classificatie vastgelegd | Nee |
| `Verwerkt` | Plannerlogica gedraaid, nog geen antwoordbesluit | Nee |
| `AntwoordVerstuurd` | Antwoord de deur uit; `IsBeantwoord` = 1 | **Ja** |
| `Review` | Voorstel opgeslagen in `AntwoordEmail`, niets verstuurd — review-mode of onbekende verzenduitkomst (#716) | Nee |
| `Fout` | Verwerking mislukt of opgegeven na 3 pogingen | Nee |
| `BuitenScope` | Buiten scope bevonden ná herclassificatie | **Ja** |
| `GeenAntwoordNodig` | Bewust geen antwoord: planning is mogelijk (#572) | **Ja** |

"Definitief" bepaalt of een volgende poll het bericht overslaat (§1d). `IsBeantwoord` is daarbij óók
leidend: staat die op 1, dan wordt er nooit een tweede antwoord gestuurd, ongeacht de status. Die
kolom overleeft de AVG-anonimisering, `VerstuurdNaar` niet (#718).

---

## 4. Handtekening en aanhef

**Aanhef** is tijdsgebonden, berekend uit UTC in de Nederlandse tijdzone:

| Lokale tijd | Aanhef |
|---|---|
| 00:00–11:59 | Goedemorgen |
| 12:00–17:59 | Goedemiddag |
| 18:00–23:59 | Goedenavond |

**Handtekening.** Er zijn twee mogelijkheden, en de eerste sluit de tweede volledig uit:

1. **Is `emailVoetnoot` gevuld in `dbo.AppSettings`, dan is die voetnoot de hele handtekening.**
   De afzendernaam en coördinatorregels hieronder worden dan niet toegevoegd.
2. Is `emailVoetnoot` leeg, dan bouwt de code de handtekening op uit losse instellingen:

```
Met vriendelijke groet,

{plannerAfzenderNaam}
Geautomatiseerd antwoord namens {coordinatorNaam}     ← alleen als gevuld
{coordinatorFunctie}                                   ← alleen als gevuld
```

`plannerAfzenderNaam` is **verplicht**: ontbreekt die instelling, dan gooit de code een
`InvalidOperationException` in plaats van een clubnaam te verzinnen. Dat is bewust — een stille
terugval zou multi-club ondersteuning breken.

**Review-mode prefix** (als `EmailReviewMode=true`): boven het opgebouwde antwoord komt een blok
met het originele afzenderadres, het onderwerp en het classificatietype. Bij een antwoord dat via
een DB-template is opgebouwd staat daar ook de template-key bij; bij de hardcoded teksten niet.

```
=== REVIEW MODE ===
Originele afzender: {adres}
Onderwerp: {onderwerp}
Classificatie: {type}
Template: {key}          ← alleen bij template-gebaseerde antwoorden
==================
```

Dit blok wordt in review-mode opgeslagen in `AntwoordEmail`; er wordt niets doorgestuurd.
