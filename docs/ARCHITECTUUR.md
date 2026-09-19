# Architectuurbeschrijving — Sportlink Wedstrijdzaken

> **Status:** geldend · **Laatst herzien:** 19 september 2026
> **Vorm:** architectuurbeschrijving volgens ISO/IEC/IEEE 42010:2022, gestructureerd met arc42.
>
> Dit is het **leidende** architectuurdocument. [ARCHITECTURE.md](ARCHITECTURE.md) beschrijft hoe
> de afspraken hieronder in de praktijk worden toegepast (checklists, schemaconventies,
> configuratiedetails) en mag ze niet zelfstandig wijzigen.
>
> **Dit document is publiek.** Het is gegrond in openbare standaarden en bevat geen waarde die deze
> installatie identificeert — zie §0.3.

---

## 0. Leeswijzer

### 0.1 Waarom deze vorm

Tot nu toe stonden de architectuurafspraken van dit project verspreid over een reeks documenten en
als doorlopende tekst in de projectinstructies. Dat werkt voor wie het geschreven heeft en slecht
voor iedereen daarna — mens of agent. Deze beschrijving lost dat op met drie keuzes:

1. **ISO/IEC/IEEE 42010:2022** als formele basis. Die standaard scheidt de *architectuur* van de
   *beschrijving* ervan, en eist dat een beschrijving expliciet maakt: wie de belanghebbenden zijn,
   welke zorgen zij hebben, vanuit welke gezichtspunten het systeem beschreven wordt, en welke
   beslissingen daaraan ten grondslag liggen. Zij schrijft géén techniek en géén bestandsformaat voor.
2. **arc42** als praktisch sjabloon. Twaalf vaste hoofdstukken, één leesbaar document, open en gratis.
   Dat voorkomt precies de versnippering waar dit project last van had.
3. **Eén register** waarin elke regel een ID, een externe basis en een bewijsvorm heeft.

### 0.2 Hoe je dit leest

| Je bent… | Begin bij |
|---|---|
| Nieuw in het project | §1 t/m §5 — doelen, beperkingen, context, bouwblokken |
| Een wijziging aan het maken | §8 (de regels) en §10 (het register: heeft deze regel een controle?) |
| Een agent die een wijziging toetst | §10 rechtstreeks — externe basis → lokale regel → bewijs |
| Benieuwd waarom iets zo is | §9 (besluiten) en §11 (risico's en schuld) |

**De belangrijkste conventie van dit document:** elke regel in §10 heeft een kolom **Bewijs**. Staat
daar `ontbreekt`, dan is de regel wel geldig maar niet controleerbaar. Dat is een zichtbare,
expliciete toestand — geen omissie die je pas ontdekt als het misgaat. Die kolom is er omdat de
toetsing van september 2026 als rode draad opleverde: *controles die minder dekken dan ze
suggereren, zijn gevaarlijker dan geen controle, want ze wekken vertrouwen.*

### 0.3 Waarom dit document geen intern materiaal van derden citeert

Deze repository is publiek en bedoeld om geforkt te worden. Een architectuurbeschrijving die leunt
op de interne standaard van een organisatie zou dat materiaal naar een openbaar kanaal trekken, en
zou bovendien onbruikbaar zijn voor een fork die die standaard niet kan lezen.

Daarom is elke regel hieronder gegrond in een **openbare** bron: een ISO-standaard, het Azure
Well-Architected Framework, OWASP ASVS, een IETF RFC, de OpenAPI-specificatie of de AVG. Een fork
kan elke bron zelf nalezen zonder toegang tot iets van deze installatie.

---

## 1. Inleiding en doelen

### 1.1 Wat dit systeem doet

Sportlink Wedstrijdzaken ondersteunt de wedstrijdsecretaris van een amateurvoetbalvereniging. Het
haalt wedstrijd- en teamgegevens op bij de externe databron van de bond, plant veldgebruik, verwerkt
inkomende e-mail over wedstrijdwijzigingen, en biedt een beheerinterface waarmee één beheerder de
vereniging kan bedienen.

### 1.2 Kwaliteitsdoelen

Vijf doelen, in volgorde van gewicht. Bij een conflict wint het hogere doel, en dat conflict wordt
als besluit vastgelegd (§9).

| # | Kwaliteitsdoel | Wat het concreet betekent | WAF-pijler |
|---|---|---|---|
| 1 | **Bescherming van persoonsgegevens** | Gegevens van leden, waaronder minderjarigen, komen niet in een publiek kanaal, een log, een issue of bij een onbevoegde verwerker terecht | Security |
| 2 | **Kosten binnen het gratis plafond** | De volledige stack draait op gratis tiers; een overschrijding is een storing, geen verrassing op de rekening | Cost Optimization |
| 3 | **Betrouwbaarheid van de dagelijkse keten** | Synchronisatie, e-mailverwerking en planning doen wat ze beloven, en een storing is zichtbaar in plaats van stil | Reliability |
| 4 | **Overdraagbaarheid** | Een andere vereniging kan de repository forken en draaien zonder toegang tot iets van de oorspronkelijke installatie | Operational Excellence |
| 5 | **Onderhoudbaarheid door een klein team** | Eén beheerder plus AI-assistentie moet dit kunnen onderhouden; complexiteit die dat niet dient, gaat eruit | Operational Excellence |

Performance Efficiency is bewust het laagste doel: de belasting is enkele gebruikers en een dagelijkse
batch. Dat is een expliciete afweging, geen omissie — zie besluit **WZ-ADR-005**.

### 1.3 Belanghebbenden en hun zorgen (ISO 42010)

| Belanghebbende | Voornaamste zorg | Waar die zorg wordt geadresseerd |
|---|---|---|
| Wedstrijdsecretaris / beheerder | Werkt het, en kan ik het zelf beheren | §6 runtime-scenario's, §7 installatie |
| Clubbestuur (verwerkingsverantwoordelijke) | Zijn de persoonsgegevens rechtmatig verwerkt | §8.1, §10 concern `data` |
| Betrokkene (lid, ouder, vrijwilliger) | Waar gaan mijn gegevens heen, en hoe lang blijven ze | §8.1 bewaartermijnen, §8.5 AI |
| Ontwikkelaar / reviewer | Welke regel geldt, en hoe toets ik hem | §10 register |
| AI-agent | Welke grens mag ik niet overschrijden | §10, machinaal leesbaar |
| Forkende vereniging | Kan ik dit draaien zonder de originele installatie | §2 beperkingen, §7 deployment |

---

## 2. Randvoorwaarden

Deze drie zijn niet onderhandelbaar en bepalen vrijwel elke afweging in dit document.

| # | Randvoorwaarde | Gevolg |
|---|---|---|
| **B1** | **Publieke, forkbare repository** | Geen club-identificerende waarde in code, configuratie, issue, pull request of buildlog. Alles wat een installatie uniek maakt, is configuratie. |
| **B2** | **Gratis Azure-tiers** | Diensten met een prijskaartje — sleutelkluis, containerhosting, betaalde alarmering, meerdere omgevingen — zijn geen standaard. Een maatregel die geld kost, vraagt een expliciet besluit. |
| **B3** | **Eén vereniging per installatie** | Er is geen autorisatiegrens tussen verenigingen nodig; de clubdiscriminator is een gegevensfilter en geen beveiligingsgrens. |

**B2 verdient toelichting, want hij wordt makkelijk als een tekortkoming gelezen.** Het Azure
Well-Architected Framework kent zelf een maturity-model in vijf niveaus en beveelt aan om gefaseerd
te beginnen bij wat essentieel is, en het hoogste niveau te reserveren voor bedrijfskritische
systemen. Deze applicatie zit bewust op niveau 1 tot 2: een solide basis en eigen werkstukken, niet
een always-on bedrijfskritische inrichting. Dat is een toepassing van het kader, geen afwijking ervan.

---

## 3. Context en systeemafbakening

### 3.1 Vakinhoudelijke context

```
  Wedstrijdsecretaris ──► Beheerinterface (browser)
                               │
  Externe databron bond ──────►│◄────── Postbus van de vereniging
  (wedstrijden, teams)         │        (inkomende wijzigingsverzoeken)
                               ▼
                         Wedstrijdzaken
                               │
                               ├──► AI-dienst (classificatie van berichten)
                               ├──► Identiteitsprovider (aanmelden en rollen)
                               └──► Uitgaande e-mail (antwoorden, meldingen)
```

### 3.2 Technische context

| Koppelvlak | Richting | Protocol | Grens |
|---|---|---|---|
| Databron van de bond | uitgaand | HTTPS, sleutel in querystring | Alleen lezen |
| Postbus | in- en uitgaand | Graph-API, applicatie-identiteit | Beperkt tot één postbus |
| AI-dienst | uitgaand | HTTPS, API-sleutel | Alleen als uitgaand verkeer is toegestaan én de functie is ingeschakeld |
| Identiteitsprovider | inkomend | OIDC en JWT | Enige bron van identiteit |
| Beheerinterface → API | inkomend | HTTPS met bearer-token | De autorisatiegrens |

**Alle uitgaande koppelvlakken passeren één poort** die controleert of extern verkeer in deze
omgeving is toegestaan. Zie **WZ-INT-01**.

---

## 4. Oplossingsstrategie

| Vraagstuk | Keuze | Reden | Besluit |
|---|---|---|---|
| Waar draait het | Serverloze functies, statische webhosting, beheerde database | Past binnen het gratis plafond en vraagt geen beheer van machines | WZ-ADR-001 |
| Hoe scheiden we lagen | Eén gedeelde kern zonder in- of uitvoerafhankelijkheden; providergebonden code in de tierbomen; de webinterface praat uitsluitend over HTTP | Houdt de kern testbaar zonder database | WZ-ADR-002 |
| Meerdere databasesoorten | Elke gebouwde tier is gelijkwaardig en krijgt dezelfde functionaliteit; één tier is actief per installatie | Overdraagbaarheid: een fork kiest zelf | WZ-ADR-003 |
| Identiteit | Externe identiteitsprovider bewijst wie je bent; de applicatie beslist wat je mag | Geen eigen accountopslag, geen eigen wachtwoorden | WZ-ADR-004 |
| Prestaties | Bewust geen optimalisatiedoel | Enkele gebruikers, dagelijkse batch | WZ-ADR-005 |
| AI | Provideronafhankelijke abstractie; de keuze van dienst en model is configuratie | Een wissel is één registratie, geen verbouwing | WZ-ADR-006 |

---

## 5. Bouwblokken

### 5.1 Niveau 1 — hoofdonderdelen

| Bouwblok | Verantwoordelijkheid | Mag níet |
|---|---|---|
| **Beheerinterface** (browsertoepassing) | Presentatie, invoer, aanroepen van de API | Persistentie, of enige databaseafhankelijkheid |
| **Gedeelde kern** | Tier- en provideronafhankelijke domeinlogica: planning, naamnormalisatie, thema, terugkoppeling, beveiligde uitgaande aanroepen | Database- of webframeworkafhankelijkheden bevatten |
| **Functie-app per tier** | HTTP-, timer- en wachtrij-ingangen, autorisatie, providergebonden gegevensverwerking | De andere tier aanroepen |
| **Databaselaag per tier** | Schema, migraties, gegevenstoegang | Logica die niets met de database te maken heeft |
| **Infrastructuur en werkstromen** | Herhaalbare uitrol, configuratie, bewaking | Club-identificerende waarden bevatten |

### 5.2 Waarom deze indeling werkt zoals ze is

De afhankelijkheden lopen één kant op en zijn cyclusvrij. De gedeelde kern bevat geen enkel
databasepakket; de beheerinterface heeft geen enkele projectverwijzing naar de achterkant. Dat is
geen voornemen maar de gemeten toestand — en precies daarom is het goedkoop om er een
architectuurtest omheen te zetten in plaats van te hopen dat het zo blijft (**WZ-ARC-01**).

**De grens die in de praktijk het vaakst verkeerd is gelegd**, is die tussen "providergebonden" en
"puur". Vier keer is bij het overzetten naar een tweede tier het *hele bestand* gekopieerd, inclusief
validatie, patronen en orkestratie die niets met de database te maken hadden. De vraag bij een
tierovergang is daarom nooit "vertaal ik dit bestand?" maar **"welk deel hiervan gaat over de
database, en welk deel niet?"** (**WZ-ARC-02**).

---

## 6. Runtime-scenario's

### 6.1 Aanmelden en autoriseren

1. De browser haalt bij de identiteitsprovider een token op.
2. Elke API-aanroep draagt dat token.
3. Het hostingplatform valideert het token en zet een gevalideerde identiteit door naar de functie.
4. De functie controleert de rol en beslist. **Dit is de autorisatiegrens.**
5. De interface verbergt wat je niet mag zien — als gebruiksgemak, nooit als beveiliging.

### 6.2 Een beheeractie

Rolcontrole → correlatie-identificatie vastleggen → databasebewerking → antwoord. Bij een fout: het
technische detail gaat naar het log, de aanroeper krijgt een gestandaardiseerd foutobject zonder
interne details (**WZ-API-02**).

### 6.3 Dagelijkse synchronisatie

Timer → poort voor uitgaand verkeer → externe databron ophalen → staging → samenvoegen → historie.
Er is ook een handmatige ingang; **die passeert dezelfde poort** (**WZ-INT-01**).

### 6.4 Verwerking van inkomende e-mail

Staat standaard uit. Bij inschakeling: postbus lezen → uitsluitingslijst opnieuw toepassen vlak vóór
de externe aanroep → classificeren → voorstel → menselijke bevestiging. De uitsluitingslijst wordt
tweemaal geraadpleegd, waarvan één keer met een verse lezing, zodat een net uitgesloten adres er niet
alsnog doorheen glipt. Is die lijst niet leesbaar, dan gebeurt er niets (**WZ-AI-03**).

### 6.5 Publicatie van door AI gegenereerde inhoud

De beheerder ziet **exact** de tekst die gepubliceerd wordt, en bevestigt. De bevestiging draagt díe
tekst terug in plaats van opnieuw te genereren — een tweede generatie zou andere tekst opleveren en
de voorvertoning tot een gok maken (**WZ-AI-04**).

---

## 7. Deployment en installatie

### 7.1 Doelomgeving

Eén omgeving per installatie: serverloze functie-app, statische webhosting, beheerde database,
opslagaccount, en een appregistratie bij de identiteitsprovider. Er is geen aparte test- of
acceptatieomgeving; dat volgt uit B2 en is vastgelegd als **WZ-ADR-007**.

### 7.2 Installatiepatroon zonder waarden in de repository

Dit is de kern van randvoorwaarde B1 en geldt voor elke fork.

1. Maak de appregistratie en de resources volgens de publieke installatiehandleiding.
2. Maak **buiten** de gekloonde repository een privéparameterbestand met uitsluitend de waarden van
   deze installatie. De handleiding toont alleen plaatshouders.
3. Meld lokaal aan bij het cloudplatform en voer de infrastructuurdefinitie eerst uit in
   *voorbeeldmodus*, daarna pas werkelijk.
4. Controleer na de uitrol de authenticatie-instellingen en het gedrag van een beheerendpoint zonder
   geldige aanmelding, vóórdat de installatie als gereed geldt.
5. Actualiseer of verwijder het privébestand bij een wijziging. Deel het nooit via de repository,
   een issue, een pull request of een buildlog.

### 7.3 Uitrol van wijzigingen

Wijziging → bouwen → tests en controles → databasemigraties → code → rookproef. Migraties gaan
vóór de code. Een migratie die de vorige codeversie breekt, hoort niet in dezelfde uitrol als de
code die haar nodig heeft.

---

## 8. Overkoepelende concepten

Dit hoofdstuk beschrijft de regels in gewone taal. §10 bevat dezelfde regels als toetsbaar register
met externe basis en bewijsvorm. **Bij twijfel is §10 leidend, want daar staat hoe je het controleert.**

### 8.1 Gegevens en privacy

Clubgegevens dragen een clubdiscriminator en bewerkingen filteren daarop. Tijdstempels worden in UTC
opgeslagen en pas in de interface omgezet. Nieuwe tabellen in de Postgres-tier schakelen rij-niveau
beveiliging in binnen dezelfde migratie — niet als applicatie-autorisatie, maar om te voorkomen dat
het platform de tabel via zijn eigen automatisch gegenereerde interface openstelt.

**Elke tabel met persoonsgegevens heeft een vastgelegde bewaartermijn, een fase en een motivering** —
inclusief het geval waarin de motivering "geen termijn" luidt; die krijgt dan een herzieningstrigger.
De termijn is configuratie, zodat de verantwoordelijke hem kan vaststellen zonder nieuwe versie.

Persoonsgegevens, exports en lokale geheimen gaan nooit in de repository.

### 8.2 Beveiliging en toegang

De identiteitsprovider bewijst identiteit; **de applicatie beslist over toegang**. Een zichtbare
interfacegrens is geen beveiligingsgrens. Een ontwikkelbypass is expliciet, beperkt tot de
ontwikkelomgeving en faalt dicht bij twijfel.

Een controle die beweert een autorisatiepatroon af te dwingen, moet **beide** tierbomen kennen én de
gebruikte hulpconstructies begrijpen. Een tekstuele controle die structureel vals alarm geeft, wordt
vervangen — want een controle die altijd afwijkt, leert de uitvoerder afwijkingen te negeren.

#### Rollenmatrix — de huidige werking

| Aanmelder | Beheerinterface | Beheer-API | Betekenis |
|---|---|---|---|
| Niet ingelogd | Omgeleid naar aanmelden | Geweigerd | Geen beheerder |
| Ingelogd, geen rol | Toegang geweigerd | Geweigerd | Geen beheerder |
| `user` | Interface laadt | Geweigerd op alle endpoints | **Onvolledig** — geen ondersteunde functie |
| `admin` | Volledig | Toegang | De beheerder |
| `wedstrijdzaken` | Geen toegang tot de beheerapp | Alleen de daarvoor bedoelde endpoints | Functionele rol |

De regel `user` is vandaag zonder effect: de interface laat hem binnen, de API weigert alles. Dat is
**te weinig toegang, geen lek** — maar code en documentatie beschrijven het verschillend, en dat is
een openstaand punt (**WZ-SEC-05**, §11).

Bij elke rolwijziging: eerst deze matrix bijwerken, dan per route een servercontrole, dan testen met
anoniem, zonder rol, `user` en `admin` — via de interface én rechtstreeks op de API.

### 8.3 API-contract

De machineleesbare specificatie is onderdeel van het contract, niet de documentatie erover. Zij is
leidend; de JSON-variant en de aanroepcollectie worden eruit afgeleid. Bij elke wijziging aan een
endpoint gaan specificatie, afgeleiden, documentatie en tests in dezelfde wijziging mee.

**Foutafhandeling volgt RFC 9457** (Problem Details for HTTP APIs) met de HTTP-semantiek van
RFC 9110. Elke door de applicatie geproduceerde foutresponse heeft het bijbehorende contenttype en
de velden `type`, `title` en `status`; `detail` en `instance` zijn optioneel. Uitbreidingen zijn
toegestaan mits gedocumenteerd, en bevatten nooit persoonsgegevens, geheimen, stacktraces of interne
details. Statuscodes volgen hun internationale betekenis; een `401` draagt de bijbehorende
uitdaging, een `429` draagt `Retry-After` wanneer de wachttijd bekend is.

**Paginering en idempotentie zijn geen standaardlaag.** Paginering is verplicht zodra een lijst
zonder functionele bovengrens kan groeien; een van nature begrensde keuzelijst blijft één antwoord.
Idempotentie is verplicht voor een mutatie met een externe of zichtbare bijwerking die realistisch
opnieuw verstuurd kan worden — een timeout, een wachtrijherlevering, een herhaalbare beheeractie.
Een gewone enkelvoudige mutatie zonder dat scenario krijgt geen extra sleutel.

Routes krijgen **geen** versieprefix. Er is één consument, die in dezelfde uitrol meegaat. Een
contractwijziging wordt daarom in één keer in code, client, specificatie, collectie en tests
doorgevoerd (**WZ-ADR-008**).

### 8.4 Database-tiers en pariteit

Elke gebouwde tier is gelijkwaardig. Een functie bestaat op álle gebouwde tiers of op geen. Welke
tier een installatie draait, is een uitrolkeuze en zegt niets over de status van de andere.

Providergebonden implementaties mogen verschillen; pure, betekenisgelijke logica wordt gedeeld zodra
zij geen providerkennis bevat.

Pariteit wordt geautomatiseerd bewaakt, **in beide richtingen**. De bestaande schemacontroles keken
maar één kant op; dat klopte toen de ene tier leidend was, en bij de omslag draaiden de rollen om
maar de controles niet.

### 8.5 AI en externe verwerking

Productiecode gebruikt de provideronafhankelijke abstractie; dienst en model zijn configuratie. Elke
tijdgevoelige instructie krijgt de datum dynamisch mee, en voorbeelden daarin bevatten geen vast
kalenderjaar — anders ondermijnt het voorbeeld stilletjes de dynamische datum.

**Doorgifte van persoonsgegevens aan een externe AI-verwerker wordt vastgelegd**: welke velden, op
welke grondslag, met welke bewaartermijn aan de kant van de verwerker. Een automatisch filter op
persoonsgegevens is een vangnet en **nooit** een garantie; zijn bekende blinde vlekken worden
uitputtend opgeschreven náást het filter.

### 8.6 Kosten

De stack blijft binnen gratis tiers. Een nieuwe resource of een tierwijziging wordt vooraf getoetst
tegen de actuele leveranciersdocumentatie — nooit uit geheugen, omdat een leverancier een gratis
tier zonder aankondiging kan beëindigen. Kostbare onderdelen staan in de infrastructuurdefinitie
achter een schakelaar die standaard uit staat en alleen met een expliciete keuze aan kan, zodat de
beslissing een reviewbare wijziging is.

### 8.7 Kwaliteit en bewijs

De blokkerende beveiligingspoort is leidend en wordt niet gecompenseerd door groene overige
controles. Elke structurele regel krijgt waar mogelijk een automatische controle; kan dat nog niet,
dan staat de regel in §10 met bewijs `ontbreekt`.

**Elke belangrijke controle bewijst ook dat hij kán falen.** Een negatieve test hoort bij de
invoering. Een controle die stilletjes nul meldt is gevaarlijker dan geen controle.

Er geldt **geen** generieke dekkingsdrempel: dekking is een signaal bij risicovolle of gewijzigde
code, geen zelfstandig criterium om te mogen samenvoegen.

---

## 9. Architectuurbesluiten

Elk besluit: context, keuze, gevolg. Een besluit wordt niet herschreven — een koerswijziging is een
nieuw besluit dat het oude vervangt.

| ID | Besluit | Context en gevolg |
|---|---|---|
| **WZ-ADR-001** | Serverloze hosting, geen containers | Containerhosting past niet binnen het gratis plafond. Gevolg: uitrol als pakket, en geen containerregister om te beheren. |
| **WZ-ADR-002** | Gedeelde kern zonder in- of uitvoerafhankelijkheden | Houdt domeinlogica testbaar zonder database. Gevolg: de grens "puur versus providergebonden" moet bij elke tierovergang expliciet worden getrokken. |
| **WZ-ADR-003** | Gebouwde tiers zijn gelijkwaardig | Vastgesteld 19-09-2026. De eerdere aanname dat één tier "alleen voor terugval" was, is ingetrokken: die is nooit als besluit voorgelegd. Gevolg: elke functie op alle gebouwde tiers, en pariteitsbewaking in beide richtingen. |
| **WZ-ADR-004** | Externe identiteit, lokale autorisatie | Geen eigen accountopslag. Gevolg: de applicatie blijft de autorisatiegrens, ook als het platform al valideert. |
| **WZ-ADR-005** | Prestaties zijn geen optimalisatiedoel | Enkele gebruikers, dagelijkse batch. Gevolg: geen belastingtests, geen prestatiedrempels; wél een grens op de duur van een synchronisatie. |
| **WZ-ADR-006** | Provideronafhankelijke AI-abstractie | Een wissel van AI-dienst is één registratie. Gevolg: geen providerklassen in domeincode. |
| **WZ-ADR-007** | Eén omgeving per installatie | Meerdere omgevingen kosten geld. Gevolg: geen goedkeuringsstappen tussen omgevingen; de integratiebranch neemt die rol over. |
| **WZ-ADR-008** | Geen versieprefix op routes | Eén consument, die meegaat in dezelfde uitrol. Gevolg: een contractwijziging raakt alles tegelijk. Herzien zodra er een tweede consument komt. |
| **WZ-ADR-009** | Foutmodel volgens RFC 9457 | Internationale standaard in plaats van een eigen formaat. Gevolg: één herbruikbaar schema in de specificatie; bestaande ad-hoc foutobjecten migreren. |
| **WZ-ADR-010** | Geen waarden van de installatie in de repository | Volgt uit B1. Gevolg: de eerste authenticatie-uitrol gebeurt lokaal met een privéparameterbestand; automatisering mag die waarden niet opslaan. |

### Afwijkingsregister

Een afwijking van een regel uit §10 is alleen geldig als registerregel met reden, eigenaar,
geldigheidsduur en controlebewijs. Zonder die regel geldt de norm.

| ID | Regel | Afwijking | Reden | Geldig tot | Bewijs |
|---|---|---|---|---|---|
| — | — | *(geen geregistreerde afwijkingen op dit moment)* | | | |

---

## 10. Het register — externe basis, lokale regel, bewijs

Dit is het hoofdstuk waar een agent of reviewer begint. Kolom **Bewijs** zegt hoe je controleert;
`ontbreekt` betekent dat de regel geldt maar nog niet toetsbaar is, en staat daarmee ook in §11.

### 10.1 Gegevens en privacy

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-DAT-01 | Clubscheiding | WAF Security | Clubdata draagt een discriminator; bewerkingen filteren erop; geen vaste clubwaarden in code | Reviewcontrole |
| WZ-DAT-02 | Tijd | ISO 8601 | Opslag in UTC, omzetting pas in de interface | CI-controle |
| WZ-DAT-03 | Platformblootstelling | WAF Security | Nieuwe tabellen zetten rij-niveau beveiliging aan in dezelfde migratie | CI-controle tegen een levende database |
| WZ-DAT-04 | Bewaartermijnen | AVG art. 5 lid 1 sub e | Elke tabel met persoonsgegevens heeft termijn, fase en motivering; termijn is configuratie | Documentatie + geplande opschoning |
| WZ-DAT-05 | Geen gegevens in de repository | OWASP ASVS V14 | Persoonsgegevens, exports en lokale geheimen nooit committen | Hooks + beveiligingspoort |
| WZ-DAT-06 | Testgegevens | AVG art. 5 lid 1 sub c | Alleen waarden uit een uitputtende lijst, elk met de reden waarom ze niet herleidbaar zijn; de lijst is gekoppeld aan de scanners | Scanner-uitzonderingslijst |

### 10.2 Beveiliging en toegang

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-SEC-01 | Autorisatiegrens | OWASP ASVS V4 | Elke beheerroute heeft een servercontrole; de interface is nooit de grens | **ontbreekt** — zie §11 |
| WZ-SEC-02 | Ontwikkelbypass | ASVS V4 | Expliciet, beperkt tot ontwikkeling, faalt dicht bij twijfel | **ontbreekt** |
| WZ-SEC-03 | Beveiligingskoppen | ASVS V14.4 | De webinterface stuurt een restrictief inhoudsbeleid en de bijbehorende koppen | Configuratiebestand |
| WZ-SEC-04 | Geheimen | ASVS V2 / V14 | Nooit in code, sjabloon, log, issue of pull request | Hooks + beveiligingspoort |
| WZ-SEC-05 | Rollenmatrix | ASVS V4 | Code, matrix en documentatie beschrijven dezelfde werking | **ontbreekt** — lopen nu uiteen |
| WZ-SEC-06 | Injectie | ASVS V5 | Uitsluitend geparametriseerde query's | Reviewcontrole |
| WZ-SEC-07 | Uitgaande aanroep op invoer | ASVS V12 | Een door de gebruiker opgegeven adres passeert de beveiligde client met adrescontrole en begrensde doorverwijzingen | Bestaande tests |
| WZ-SEC-08 | Publicatiecontrole | — | Een tekst zonder echte waarden kan nog een vindaanwijzing zijn; bij een nog niet verholpen bevinding alleen klasse en codepad | Reviewcontrole |

### 10.3 API

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-API-01 | Contract | OpenAPI 3.0.3 | De specificatie is leidend; afgeleiden worden gegenereerd | Reviewcontrole |
| WZ-API-02 | Foutmodel | RFC 9457 / RFC 9110 | Problem Details met correct contenttype; geen interne details | **ontbreekt** — ad-hoc formaat in gebruik |
| WZ-API-03 | Statuscodes | RFC 9110 / RFC 6585 | Internationale betekenis; `401` met uitdaging, `429` met `Retry-After` | Endpointtests |
| WZ-API-04 | Routepariteit | — | Specificatie en code beschrijven dezelfde routes | **ontbreekt** — drift aangetoond |
| WZ-API-05 | Paginering | — | Verplicht bij een lijst zonder functionele bovengrens; gedocumenteerd met grens, sortering en totaal | **ontbreekt** |
| WZ-API-06 | Idempotentie | RFC 9110 §9.2.2 | Verplicht bij een mutatie met externe bijwerking die opnieuw verstuurd kan worden | **ontbreekt** |
| WZ-API-07 | Geen versieprefix | WZ-ADR-008 | Contractwijziging raakt alles in dezelfde uitrol | Reviewcontrole |

### 10.4 Architectuur en tiers

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-ARC-01 | Laaggrenzen | ISO 42010 | De gedeelde kern is vrij van database- en webafhankelijkheden; de interface heeft geen persistentie; tiers roepen elkaar niet aan | **ontbreekt** — geldt feitelijk al, is niet vastgeklikt |
| WZ-ARC-02 | Tierovergang | — | Bij een overgang wordt per deel bepaald of het providergebonden is; puur deel gaat naar de kern | Duplicatieplafond |
| WZ-ARC-03 | Tierpariteit | — | Elke functie op alle gebouwde tiers; bewaking in **beide** richtingen | **ontbreekt** voor code; aanwezig voor schema |
| WZ-ARC-04 | Interfacelogica | — | Geen logica in paginabestanden; achterliggende klasse | CI-controle |
| WZ-ARC-05 | Bestands- en methodeomvang | — | Plafonds mogen niet stijgen | CI-controle |

### 10.5 Integraties en AI

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-INT-01 | Uitgaande poort | WAF Security | Elk uitgaand pad — HTTP, timer én wachtrij — passeert de poort | **ontbreekt** — één pad omzeilt hem |
| WZ-AI-01 | Providerabstractie | WZ-ADR-006 | Geen providerklassen in domeincode | Reviewcontrole |
| WZ-AI-02 | Dynamische datum | — | Tijdgevoelige instructies krijgen de datum mee; voorbeelden zonder vast jaar | Reviewcontrole |
| WZ-AI-03 | Uitsluiting vóór verzending | AVG art. 21 | Uitsluitingsregels opnieuw toepassen vlak vóór de externe aanroep; faalt dicht | Bestaande code |
| WZ-AI-04 | Publicatiegrens | — | De bevestiging draagt de getoonde tekst terug; het filter is nooit een garantie | Bestaande code |
| WZ-AI-05 | Doorgifte vastgelegd | AVG art. 30 | Velden, grondslag en bewaartermijn bij de verwerker staan beschreven | **ontbreekt** |

### 10.6 Infrastructuur, kosten en uitrol

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-COST-01 | Gratis tiers | WAF Cost Optimization | Binnen gratis tiers; wijziging vooraf toetsen tegen actuele leveranciersdocumentatie | Schakelaars in de infrastructuurdefinitie |
| WZ-COST-02 | Kostenpoort als code | WAF Cost Optimization | Kostbare onderdelen staan standaard uit, aanzetten is een expliciete keuze | Infrastructuurdefinitie |
| WZ-DEP-01 | Installatie zonder waarden | WAF Security / WZ-ADR-010 | Eerste authenticatie-uitrol lokaal met privéparameterbestand; niets in de repository | Voorbeeldmodus + controle na uitrol |
| WZ-DEP-02 | Infrastructuur is waar wat ze belooft | WAF Operational Excellence | Drukt de definitie een instelling uit, dan moet de uitrol die ook werkelijk zetten — anders eruit en de handmatige stap documenteren | **ontbreekt** — een voorwaarde wordt nooit waar |
| WZ-DEP-03 | Labels | WAF Cost Optimization | Elke resource draagt labels voor omgeving, applicatie, beheerwijze, kosten en dataclassificatie | **ontbreekt** |
| WZ-DEP-04 | Aanmelding van automatisering | WAF Security | Nieuwe of ingrijpend gewijzigde werkstromen gebruiken federatieve aanmelding zonder geheim | **ontbreekt** — bestaande werkstromen migreren |

### 10.7 Kwaliteit en bewijs

| ID | Onderwerp | Externe basis | Lokale regel | Bewijs |
|---|---|---|---|---|
| WZ-QUA-01 | Beveiligingspoort | WAF Security | Blokkerend; niet te compenseren | Werkstroom |
| WZ-QUA-02 | Negatieve controle | WAF Operational Excellence | Elke belangrijke controle bewijst dat hij kan falen | Per controle |
| WZ-QUA-03 | Geen dekkingsdrempel | WAF Operational Excellence | Dekking is een signaal, geen samenvoegcriterium | Bewust geen |
| WZ-QUA-04 | Meldketen | WAF Operational Excellence | Een geautomatiseerde controle draagt een manier om de melding te laten afgaan zonder echt probleem | Testingang |
| WZ-QUA-05 | Documentatie loopt mee | WAF Operational Excellence | Documentatie is bij vóór samenvoegen; verouderde informatie misleidt | **ontbreekt** — vier documenten lopen uiteen |

---

## 11. Risico's en technische schuld

Alle `ontbreekt`-regels uit §10, op volgorde van effect gedeeld door kosten. Dit is tevens de
werkvoorraad.

| # | Punt | Register-ID | Waarom dit eerst | Omvang |
|---|---|---|---|---|
| 1 | Eén uitgaand pad omzeilt de poort | WZ-INT-01 | Lokaal kan een volledige ophaalronde bij de externe bron starten | Enkele regels, twee tiers |
| 2 | Geen pariteitscontrole op de codebomen | WZ-ARC-03 | Vier gedocumenteerde storingen komen hieruit voort | ± 1 dag |
| 3 | Laaggrenzen niet vastgeklikt | WZ-ARC-01 | De regels zijn vandaag al waar; borging kost geen codewijziging | ± 1 dag |
| 4 | Autorisatiedekking niet afgedwongen | WZ-SEC-01 | Negenennegentig handmatige aanroepen; niets bewaakt de honderdste | valt samen met 3 |
| 5 | Doorgifte aan AI-verwerker niet vastgelegd | WZ-AI-05 | Verantwoordingsplicht; geen code nodig | documentatie |
| 6 | Rollenmatrix loopt uiteen met de code | WZ-SEC-05 | Een verplichte test waarvan een stap niet kan slagen, leert afwijkingen negeren | ± halve dag |
| 7 | Aanmelding van automatisering zonder geheim | WZ-DEP-04 | Kost niets en de rechten staan al klaar | ± halve dag |
| 8 | Infrastructuur belooft wat ze niet uitrolt | WZ-DEP-02 | Voorbeeldmodus zou hier blijvend verschil moeten tonen | ± 2 uur |
| 9 | Labels op resources | WZ-DEP-03 | Goedkoop; nodig voor kostentoerekening | ± 2 uur |
| 10 | Documentatiedrift | WZ-QUA-05 | Vier documenten, waarvan twee een beveiligingsmechanisme verkeerd beschrijven | ± halve dag |
| 11 | Foutmodel, routepariteit, paginering, idempotentie | WZ-API-02/04/05/06 | Contractkwaliteit; begin bij foutmodel en routepariteit | meerdere dagen |
| 12 | Ontwikkelbypass zonder tweede signaal | WZ-SEC-02 | Laag risico in de praktijk, maar hangt aan één omgevingsvariabele | ± 2 uur |

**Bewust niet op deze lijst**, met reden: een abstractielaag over alle gegevenstoegangsklassen
(weken werk, testbaarheid bestaat al via integratietests, geen storing eraan toe te schrijven), en
een herstructurering naar een vierlagenmodel (verdubbelt het oppervlak waarop tierpariteit bewaakt
moet worden — precies het probleem onder punt 2).

---

## 12. Begrippenlijst

| Begrip | Betekenis in dit document |
|---|---|
| **Tier** | Een volledige implementatie voor één databasesoort. Meerdere tiers bestaan naast elkaar; één is actief per installatie. |
| **Gedeelde kern** | Het project met logica die van geen enkele databasesoort of webframework afhankelijk is. |
| **Pariteit** | De eigenschap dat alle gebouwde tiers dezelfde functies, tabellen en kolommen hebben. |
| **Poort voor uitgaand verkeer** | De ene plek die bepaalt of externe aanroepen in deze omgeving zijn toegestaan. |
| **Clubdiscriminator** | De kolom die gegevens aan een vereniging koppelt. Een gegevensfilter, geen beveiligingsgrens (B3). |
| **Bewijs** | De manier waarop een regel controleerbaar is: een geautomatiseerde controle, een test, een configuratiebestand, of een expliciete reviewstap. |
| **Negatieve controle** | Een test die aantoont dat een controle daadwerkelijk rood kan worden. |

---

## 13. Onderhoud van dit document

Dit document is de centrale, leidende plek voor de geldende architectuurafspraken.
Uitvoeringsdocumentatie beschrijft *hoe* een afspraak wordt toegepast en mag haar niet zelfstandig
wijzigen.

Een nieuw besluit is nodig wanneer een wijziging een vastgelegde grens, een publiek contract, de
gegevensbescherming, de kostenlimiet of de tierstrategie raakt.

**Twee onderhoudsregels die uit de toetsing van september 2026 volgen:**

1. **Een nieuwe regel krijgt een controle, of komt met bewijs `ontbreekt` in §10 én in §11.** Er is
   geen derde mogelijkheid. Zo ontstaan geen regels die alleen in een document leven.
2. **Een onbeantwoorde vraag is geen afwijking en wordt geen norm.** Zij hoort in een apart
   vragenbestand tot zij beslist is.

Beoordeel dit document opnieuw na een relevante architectuurwijziging, een incident, een nieuwe
gegevensstroom, of een wijziging in de gratis tiers van de leverancier.

---

## Bijlage — gebruikte externe kaders

| Kader | Rol | Toepassing hier |
|---|---|---|
| ISO/IEC/IEEE 42010:2022 | Formele basis voor een architectuurbeschrijving | Belanghebbenden, zorgen, gezichtspunten, besluiten (§1, §9) |
| arc42 | Praktisch sjabloon | De hoofdstukindeling van dit document |
| Azure Well-Architected Framework | Toetsbaseline voor de cloudkeuzes | Vijf pijlers: Reliability, Security, Cost Optimization, Operational Excellence, Performance Efficiency. Het kader kent een maturity-model in vijf niveaus en beveelt een gefaseerde toepassing aan; deze applicatie bevindt zich bewust op niveau 1–2. Er bestaat ook een MCP-server met WAF-servicegidsen, wat geautomatiseerde toetsing later mogelijk maakt. |
| OWASP ASVS | Securityverificatie | Alleen de controles die op deze webapplicatie van toepassing zijn (§10.2) |
| RFC 9110, RFC 9457, RFC 6585 | HTTP-semantiek en foutmodel | §8.3 en §10.3 |
| OpenAPI 3.0.3 | Contractformaat | §8.3 en §10.3 |
| AVG (Verordening (EU) 2016/679) | Gegevensbescherming | §8.1, §8.5 en §10.1 |
