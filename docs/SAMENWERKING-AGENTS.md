# Samenwerking tussen agents

**Protocol v1 staat standaard in SHADOW.** De registratie geeft geen review-, merge-, release- of deploybevoegdheid en verandert de bestaande exacte-head-review en verplichte CI-gates niet. De bestaande overdrachtsroute blijft gelden zolang de eigenaar de activering niet afzonderlijk heeft goedgekeurd. Een expliciet via dit protocol gerouteerde aanvraag wordt door CI geweigerd; gebruik de registratie dus alleen voor de toegewezen lokale shadowproef. Er is geen automatische dispatch.

Deze uitleg werkt `AGENTS.md` uit. `AGENTS.md` blijft de enige normatieve bron; bij verschil geldt die bron. De CLI, scripts en configuratie kunnen een deel van deze procesregels afdwingen, niet de volledige menselijke of GitHub-autorisatie.

De proceswaarborgen hieronder beschrijven het doelprotocol dat in SHADOW wordt vergeleken met de
bestaande route. Ze dispatchen, blokkeren of autoriseren die normale route niet; expliciete
protocolrouting blijft door CI geweigerd zolang de eigenaar niet tot activering besluit.

## Rollen en opdrachtrecord

Per taak wijst de eigenaar één implementer aan. Een reviewaanvraag wijst precies één onafhankelijke reviewer aan, met een unieke sessie-ID (een agenttype alleen is geen identiteit). De dispatcher coördineert aanvraag, checkpoint en overdracht, maar krijgt daardoor geen bevoegdheid om te implementeren, mergen of deployen. De reviewer is read-only.

Leg bij het issue en de PR vast: issue/PR, immutable request-ID, poging en fencing generation, branch, volledige base-, merge-base- en head-SHA, implementer- en reviewer-sessie, scope/bestanden, ronde, integratiepoging, afhankelijkheden, verplichte checks en subtaken, toegestane acties, eigenaarstoestemming en volgende stap. Elke permission noemt verlenende eigenaar, exacte sessie, issue/PR, actie en branch, voorwaarden, verval/herroeping en of ze een headwijziging overleeft. Dezelfde autorisatie en scope zijn vindbaar in issue en PR.

De exacte toegewezen sessie bevestigt request-ID, poging, generation, SHA en scope. Die bevestiging en een comment zijn **geen lock**. `turn:`-, `status:`-labels, PR-status en comments zijn zichtbare projecties, geen dispatch- of bevoegdheidsbron. Een mismatch blokkeert afronding totdat de bevoegde registratie en projecties zijn vergeleken. Neem geen taak over op basis van een leeg issue, verouderde claim of verlopen tijd.

## Reviewverzoek, bevindingen en afronding

Een aangeboden review bevriest de volledige head-SHA en scope. Er mag niet op die SHA worden gepusht zolang de review loopt. Het resultaat vermeldt reviewer-sessie, request-ID, ronde, SHA, scope, uitkomst, bevindingen, beperkingen en alle verificatie. Publiceer geen schone uitkomst zolang een verplichte check/subtaak loopt, ontbreekt, onbekend, mislukt of is geannuleerd zonder afgerond gelijkwaardig vervangend bewijs. De reviewer moet het resultaat van iedere verplichte taak afwachten en verifiëren. Een afgerond reviewbericht alleen heft deze poort niet op.

Alle bevindingen staan append-only in een PR-brede ledger met stabiel ID, herkomst, sessie, SHA, ernst, locatie/bewijs, status en dispositie. Een onverwachte of dubbele reviewer kan de gevraagde review niet vervangen, maar een concrete bevinding blijft behouden en wordt bevoegd getrieerd. Een latere schone review wist eerdere bevindingen nooit. Oplossen verwijst naar fixcommit en verificatie; niet overnemen vereist reden en bevoegd besluit. Een correctie trekt het oude resultaat niet uit de historie: voeg een herroeping toe, maak gereedheid ongeldig en heropen de beoordeling. Late ongunstige uitkomst heropent eveneens.

Publieke issue-/PR-notities volgen altijd `AGENTS.md` veiligheidsregel 4a. Verberg geen finding,
maar bewaar gevoelige of nog onopgeloste details via de bestaande besloten route; publiceer geen
locatie, omvang of exploiteerdetail zolang dit niet veilig is.

Verplichte verificatie wordt vooraf als zodanig geregistreerd. Ze mag niet achteraf als optioneel worden hernoemd. Annuleren voldoet nooit aan een verplichte taak: laat haar slagen met geverifieerd bewijs of wijs expliciet een gelijkwaardige vervanging toe en voltooi die. Alleen optionele verkenning mag met reden worden geannuleerd nadat vaststaat dat ze gestopt/gefenced is; reeds ontvangen en latere bevindingen blijven zichtbaar. Een onbekende taakstatus blokkeert.

Houd maximaal **drie inhoudelijke reviewrondes** per PR bij. Een code-/documentwijziging, gedragsconflict, scopewijziging of onzekere equivalentie vraagt een nieuwe inhoudelijke ronde. Bevindingen uit ronde drie gaan eerst terug naar de implementer voor dispositie en daarna naar de eigenaar; ronde vier vereist expliciete, beperkte eigenaarstoestemming. Herroeping corrigeert dezelfde ronde; reviewerwissel reset de teller niet.

Een basismutatie na een schone review kan hoogstens **twee integratiepogingen** gebruiken, met eigen teller en integratieattest; teller reset niet bij tijd, label, sessie of reviewerwissel. Na uitputting volgt eigenaarsbesluit. Integratieattest is geen nieuwe review, geen kopie van goedkeuring en geen toestemming tot mergen.

## Integratieattest en ontwikkelbranch

Na activering horen alle schrijvers naar `develop` (agents, eigenaar, CLI, automation en bots) bij
één globale, geserialiseerde integratielane. De lane bepaalt volgorde, niet bevoegdheid. Elke
schrijver blijft gebonden aan bestaande eigenaarspaden, zelfmergevoorwaarden, branchbescherming en
CI. Een coördinator of lane-reservering verleent geen mergebevoegdheid.

In SHADOW blijft de bestaande effectieve workflow actief; de lokale registratie is observatie, geen
lock. Zij bewijst niet dat een lokale gebruiker of GitHub-credential is tegengehouden. De beoogde
eerste actieve fase is één lokale coördinator met duurzame externe status; CI-agenten en andere
writers moeten dan aantoonbaar meedoen of worden stilgezet. Pas na bewijs van één lane voor alle
ontwikkelbranch-schrijvers kan de eigenaar activering besluiten. Multi-host vereist gedeelde
transactionele autoriteit en gecontroleerde side-effects; een lokaal bestand, label of generation
kan een directe remote write niet blokkeren.

Een integratieattest bindt oude review-base/head/tree aan nieuwe base/head/tree, request, ledger en beleidsversie. Het bevat de commitgraaf, **geordende** volledige vergelijking van de PR-bijdrage, alle basewijzigingen en hun interactie met de bijdrage, conflictresoluties, gegenereerde metadata en checks op de resulterende head. `range-diff` kan helpen, maar een gesorteerde diffhash bewijst geen gelijkwaardigheid. Onderzoek gedeelde contracten, callers, schema, workflows, ratchets, security, docs/instructies, API en versie/changelog. Bij gewijzigde of onzekere bijdrage is een inhoudelijke review nodig; bij onvolledige vergelijking blokkeert het attest.

Een uitsluitend base-gerelateerde update kan de oude review behouden, maar alleen als attest bewijst
dat de PR-bijdrage inhoudelijk gelijk bleef en alle nieuwe basiseffecten beoordeeld zijn. Een
automatisch beperkte gelijkwaardigheid kan uitsluitend volledige boomgelijkheid of de vooraf
begrensde negen csproj-versievelden plus twee OpenAPI-`info.version`-velden betreffen. Ieder ander
verschil of conflict vereist handmatige inhoudelijke beoordeling; geen geselecteerde diffregel- of
sorted-hashroute. Alle vereiste checks moeten daarna op de exacte geïntegreerde head groen zijn.
Een conflict in gedrag of gedeelde contracten wordt nooit automatisch gekozen.

## Changelog, versie en API-sync — gekozen doelroute

De doelroute verplaatst versie- en releasemetadata naar de seriële integratiestap. Iedere wijziging met release-impact krijgt dan een eigen stabiele changelogfragment-ID, issueverwijzing, categorie, gebruikersgerichte tekst en releaseclassificatie. Een deterministisch preview maakt unreleased wijzigingen zichtbaar. Fragmenten worden bij release exact eenmaal verwerkt; backports bewaren ID/herkomst zonder oude aankondiging of bump te dupliceren. Normatieve instructies en API-documentatie blijven in de implementatie-PR synchroon met gedrag.

De gekozen integratiestap schrijft de versie synchroon in de drie csproj's en API `info.version`/JSON waar van toepassing. De tooling valideert de verwachte huidige versie en schrijft alleen bij expliciete apply; fragmentpreview en voorbereiding hebben geen schrijfeffect zonder die optie. Dit is een doelroute, geen claim dat de huidige PR/branchroute al is vervangen.

Tot expliciete activering blijven de huidige regels gelden: `CHANGELOG.md` onder `[Unreleased]`, versies commit-voor-commit waar de bestaande werkinstructie dat voorschrijft, API-bijwerking in dezelfde commit als endpointwijziging, de drie csproj's synchroon en bestaande exacte-head-CI. Voeg niet tegelijk een fragment en dezelfde handgeschreven release-entry toe tenzij de gekozen releaseprocedure dat expliciet vereist.

## Storing, herstel en no-response

Een checkpoint bevat alle IDs/tellers, request/SHA/base, checks, bevindingen, actieve subtaken, rechten, laatste gebeurtenis en volgende toegestane stap. Hervatten begint met vergelijking van die checkpoint met actuele PR, branch, head, checks, merge-status en grants. Elke mismatch pauzeert; een nieuwe poging of integratiepoging krijgt nieuwe identiteit en behoudt de geschiedenis.

Een stale sessie wordt nooit automatisch overgenomen. Eerst stopt een bevoegde actor de oude sessie en stelt vast dat die niet verder kan schrijven; daarna wordt ongecontroleerde remote state verzoend, generation opgehoogd en de overdracht expliciet vastgelegd. Onzekere stop betekent blokkade. Een timeout of nieuw generation voorkomt geen directe GitHub-side-effect. Bij crash na push/merge: controleer eerst de remote toestand tegen de vastgelegde action-intent; nooit blind opnieuw uitvoeren.

Bij quota, ontbrekende tool, netwerkstoring, dispatcher/store-uitval, onduidelijke eigenaarstoestemming, onverwachte writer of onzekere state: checkpoint, pauzeer en meld exacte ontbrekende actie. Start niet automatisch opnieuw, verhoog de quota niet en verruim rechten niet. Een statusvraag van de gebruiker pauzeert het werk niet: antwoord kort en hervat de vastgelegde volgende stap zodra dat kan.

## Activering

Activeren vereist een aparte expliciete eigenaarbeslissing en toegewezen implementatie/review. Voor omschakeling naar `ACTIVE` controleert de eigenaar minimaal: default-branch workflows en branchbescherming; elke lokale/CI/bot/owner ontwikkelbranch-writer; exclusieve dispatcher, store en herstel; fencing en crashreconciliatie; de volledige acceptatieset; changelogfragment- en versie/API-tools inclusief idempotentie; behoud van bestaande Security Gate, exacte-headchecks en eigenaargrenzen. Onbewezen onderdeel betekent SHADOW houden. Een policyversie, CI-configuratie of workflow op een featurebranch activeert het beleid niet.

De taakregister-cli gebruikt alleen een absoluut databasepad buiten de checkout en standaard uitvoer zonder geheimen.
Protocol 1 gebruikt lokaal schema-revisie 2; een oudere experimentele store wordt geweigerd en
vereist export en handmatige reconciliatie/migratie. `init` legt één `owner_session` vast. Dit en
alle sessie-/grantinvoer zijn lokale verklaringen: de CLI authenticeert geen mens, proces of remote
credential en kan remote schrijvers niet blokkeren.

`request-create` vereist niet-lege `required_checks` en een vooraf vastgelegde `mandatory_tasks`-lijst
met minstens één `reviewer_verification: true`-taak van de toegewezen reviewer. Per repository/PR
is één actieve aanvraag toegestaan; een inhoudelijke SHA kan niet via een nieuw request-ID opnieuw
worden aangeboden. Herstel op dezelfde SHA gebruikt intrekking en `reassign` van de bestaande aanvraag. `task-register`
start zo'n taak; extra taken mogen alleen optioneel zijn. Een child levert `task-output` met exacte
aanvraagidentiteit en resultaat. `task-complete` blijft een onafhankelijke revieweractie met
succesvol verificatiebewijs en, bij een child, diens exacte `output_event_id`. Stale/ongeautoriseerde
uitvoer blijft in de historie en kan geen nieuwe generatie voltooien. Een mislukte taakuitvoer
blijft als te triëren bevinding behouden en maakt eerdere gereedheid voor die PR ongeldig. Bij `reassign` is ook de stop
van alle actieve child-sessies expliciet vereist; taken en checks moeten opnieuw worden bewezen.

Bij rode of ontbrekende checks, of onvoltooide taken, kan de reviewer `finish` met `result: blocked`
teruggeven: reden, reviewerstop, stop-/fence-bewijs en bevestigde stop van actieve child-sessies zijn
verplicht. De aanvraag wordt `invalidated`, de generatie stijgt en alle taken, checks en bevindingen
blijven intact. Dit levert nooit `ready` op; een nieuwe SHA kan daarna een nieuwe aanvraag krijgen.
`finish` met `result: clean` weigert iedere nog open PR-bevinding. Een late bevinding trekt ook
nieuwere gereedheid van dezelfde PR in; `action-intent` controleert het ledger opnieuw.

Een `verified`-dispositie vereist `fix_commit`, `verification_sha` en `verification_request_id`.
De exact toegewezen onafhankelijke reviewer registreert deze op de actieve nieuwe aanvraag en
nieuwe head waarop de fix is beoordeeld, ook voor bevindingen uit eerdere rondes. Gereedheid en
actie-intents vereisen die binding op de huidige aanvraag/generatie. Oude ongebonden disposities
blijven bewaard maar gelden niet als bewijs. De velden zijn lokale bewijsverklaringen; de CLI
controleert geen Git-ancestry van de opgegeven fixcommit. Schema-revisie 2 blijft compatibel:
nieuwe bindingen staan in append-only events, zonder bestaande historie te herschrijven.

`kind: integration` vereist de nieuwste schone bronaanvraag en laat de CLI de bestaande
`integration-evidence.py` uitvoeren. Alleen exacte integratietree of gesynchroniseerde versievelden
worden geaccepteerd. `integration-attest` vereist daarna een afzonderlijke onafhankelijke
interactiebeoordeling die alle oude/nieuwe SHA's, trees en de bewijshash bindt. Conflicten, gewijzigd
gedrag, onzekere vergelijking of ongeldige bronreview blokkeren; de inhoudelijke route houdt haar
eigen limiet. Checks blijven verplicht op de nieuwe head.

De lokale grantregistratie ondersteunt uitsluitend `owner_accepted` voor één bevinding op de exacte
aanvraagtuple. Grants hebben een expliciete vervaltijd, herroeping, bronreferentie en exacte sessie;
ze overleven geen head-, poging- of generatiewijziging. Ze geven geen merge-/releasebevoegdheid.
Een `withdrawn` lane vereist stop-, fence- en reconciliatiebewijs en blokkeert zolang geregistreerde
remote action-intents niet definitief zijn verzoend. De historie blijft behouden en de generatie
wordt verhoogd. Het concrete JSON-contract staat in `DEVELOPER-SETUP.md` §9.5.
 De changelog-/versietooling biedt preview/validatie en schrijft pas met expliciete apply; volg de actuele command-help en contracten van de scripts. Verzin geen subcommands of JSON-velden op basis van dit document.

---

*Laatste verificatie: v3.17.4.0 — 2026-11-14*
