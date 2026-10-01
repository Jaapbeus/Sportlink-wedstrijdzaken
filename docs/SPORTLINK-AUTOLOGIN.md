# Automatische Sportlink-login (#1411)

## Doel en status

De eigenaar heeft bevestigd dat na tien uur een nieuwe login nodig is en dat Sportlink de
machine-to-machine-aanvraag heeft afgewezen. De eigenaar heeft de oorspronkelijke acceptatieduur
van 24 uur bijgesteld naar 14 uur en 39 minuten en heeft de lokale test na 14u39 zonder verbreking
als geslaagd en afgerond verklaard. Daarmee is de eigenaarstest voor deze feature geaccepteerd
volgens de door de eigenaar vastgestelde duur. Dit is geen uptime- of SLA-garantie voor productie.
Deze opt-in functie gebruikt het normale account met wachtwoord en TOTP. MFA blijft actief. De
runtime genereert de code uit de legitiem ingestelde authenticator-instelsleutel. Een actuele
zescijferige code opslaan werkt niet.

De provider ondersteunt de bekende Keycloak-formulieren via authorization code + PKCE S256.
Tests gebruiken lokale fixtures en fictieve gegevens. De eigenaar beoordeelt de 14u39-proef als
geslaagd; daarin zijn de huidige Sportlink-schermen doorlopen en de tien-uursherlogin praktisch
aangetoond. Een onbekend formulier, CAPTCHA, gewijzigd MFA-proces of extra identity provider wordt geweigerd.
Een geslaagde build of fixturetest is geen bewijs van live compatibiliteit.

## Productie-initialisatie

1. Neem de release naar productie pas nadat `develop` is getest. PostgreSQL krijgt de additive
   migraties `029_sportlinkautologin.sql` en `030_remove_legacy_sportlink_tokens.sql`. SQL Server
   krijgt `dbo.SportlinkAutoLogin` via het idempotente
   `Database/Script.PostDeployment1.sql` (synchroon met het schema-project). De nieuwe code en
   configuratie moeten samen uitgerold worden: de oude refresh-tokenopslag wordt verwijderd.
2. Genereer lokaal een cryptografisch willekeurige sleutel van 32 bytes, bijvoorbeeld met
   `openssl rand -base64 32`. Stel de Base64-waarde in als bestaande Function App-secretsetting
   `SportlinkAutoLoginEncryptionKey`. Bewaar een recoverykopie in het bestaande beveiligde
   secretbeheer, apart van databaseback-ups. Geen sleutel in git, frontend, database of chat.
   Ontbrekende of ongeldige sleutels laten de automatische configuratie gesloten (HTTP 503).
3. Log als beheerder in, selecteer de primaire club en open Sportlink Web Extension. Stel bij
   de functionele rol automatische login in met het account met minimale benodigde rechten.
   Vul gebruikersnaam, wachtwoord en authenticator-instelsleutel in. Algoritme, aantal cijfers
   en periode moeten overeenkomen met de echte enrollment; standaard SHA1/6/30 is geen bewijs
   dat iedere Sportlink-configuratie die gebruikt.
4. Opslaan versleutelt de gegevens; het doet nog geen externe login. De volgende bestaande
   uurtimer of toegestane Sportlink-aanroep probeert aan te melden. De extension-toggle en
   `EgressGuard` blijven leidend. De dry-run-instelling voor wedstrijdmutaties blijft behouden.
5. Controleer de status in de beheerpagina en voer een bewuste live controle uit. De administratie
   toont alleen status en tijden, nooit credentials, MFA-seed of tokens.

Er is één ondersteunde route: credentials instellen via de beveiligde beheerpagina en automatische
login via de backend. De oude refresh-token capture/uploadroute en opslag zijn verwijderd; de
PostgreSQL-migratie verwijdert de oude tabel. Geen legacy-fallback bestaat.

## Opslag en coördinatie

AES-256-GCM gebruikt een willekeurige nonce en een authenticatietag. Versie, club, rol en doel
zijn authenticated associated data: credentials en refresh-tokens zijn niet uitwisselbaar,
ook niet tussen clubs. Alleen de backend kan ontsleutelen. Databasetoegang alleen levert geen
leesbare secrets op. Wie zowel runtime als hostsleutel compromitteert kan beide factoren
gebruiken; dit ontwerp behoudt geen fysieke scheiding tussen wachtwoord en tweede factor.

Alle refresh-/loginoperaties met de nieuwe store en alle beheerwrites gebruiken dezelfde
databaselease per club/rol. PostgreSQL gebruikt een transaction-scoped advisory lock (ook
geschikt voor transaction pooling), SQL Server een transaction-owned application lock. De
leaseverbinding blijft open gedurende de operatie; de afzonderlijke state/tokenwrites zijn
duurzaam voordat de lease wordt vrijgegeven. Er is geen generieke DB-providerabstractie.

Na negen uur wordt een nieuwe login geprobeerd, zodat de uurtimer vóór de bevestigde
tien-uursgrens kan herstellen. Een gemiste timer wordt bij de volgende toegestane aanroep
opgevangen. `invalid_grant` kan een eerdere herlogin vragen, maar nooit binnen vijftien minuten
van de vorige succesvolle login. Een poging wordt vóór de netwerkcall vastgelegd. Tijdelijke
fouten krijgen minimaal vijftien minuten pauze en maximaal drie pogingen. Onbekende challenges
of geweigerde credentials blokkeren automatisch herstel totdat de beheerder opnieuw configureert.
Een pending poging na een crash telt mee, zodat ook crashes geen onbeperkte loginlus veroorzaken.

Rotatie wordt afgewacht vóór access-token caching en succesrapportage. Bij onzekerheid na een
netwerk- of opslagfout kan een nieuwe sessie nodig zijn; dat is geen reden om een oude
wedstrijdmutatie automatisch opnieuw uit te voeren. De bestaande mutatieguards blijven gelden.

## Verwijderen en intrekken

Verwijderen wist credentials en het afgeleide opgeslagen refresh-token. Een al gecachet access-token kan zijn resterende
geldigheidsduur behouden; voor onmiddellijke intrekking moet de beheerder het account/de sessie
bij Sportlink intrekken. Dit endpoint is geen providerlogout.

Een verkeerd wachtwoord, gewijzigde seed of onbekend scherm vereist herstel in de beheerpagina.
Na permanente blokkade of drie mislukte pogingen kan opnieuw opslaan de blokkade opheffen.
Geen token, responsebody, cookie of wachtwoord in supportmeldingen opnemen.

### Als automatische login vastloopt of de sessie breekt

1. Open **Instellingen → Sportlink Ext.** en lees alleen de generieke status en tijdstippen.
   Kopieer geen logs met requestdetails of responsebody naar een issue.
2. Controleer bij een tijdelijke fout of de uurtimer na de cooldown opnieuw probeert. Controleer
   ook dat de bestaande EgressGuard toestemming en de extensie-instelling actief zijn.
3. Bij `enabled=false`, een permanente blokkade, gewijzigd wachtwoord of gewijzigde MFA-enrollment:
   voer de volledige username, password en actuele TOTP-instelsleutel opnieuw in via de beheerpagina.
   Opslaan wist de blokkade. Een zescijferige actuele code is geen vervanging voor de instelsleutel.
4. Bij een onbekend Sportlink/Keycloak-scherm, CAPTCHA of gewijzigde MFA-flow: stop automatische
   retries. Verifieer eerst de gewijzigde loginflow en werk de provider met tests bij; schakel MFA
   niet uit en probeer geen challenge te omzeilen.
5. Als de ingestelde hostsleutel ontbreekt of veranderd is, herstel exact de oorspronkelijke waarde
   uit het afzonderlijk beheerde recovery-secret. Is die verloren, dan moeten credentials opnieuw
   worden ingesteld en encrypted records opnieuw opgebouwd; databaseback-ups alleen zijn onvoldoende.
6. Bevestig herstel met status/tijdstempels en een read-only Sportlink-controle. Herhaal geen
   wedstrijdmutatie om authenticatie te testen.

### Eerste hulp voor een nieuwe developer

Gebruik de feature branch en lokale testomgeving. Maak secrets aan in de lokale secret store; zet
geen echte credentials of hostsleutel in `local.settings.json`, git, terminaloutput, issue of chat.
Gebruik alleen fictieve fixtures voor provider- en encryptietests. Voor initiële productieconfiguratie
volgt een bevoegde beheerder de stappen hierboven nadat de release is uitgerold. Ontwikkelaars
hebben geen toegang tot echte credentials nodig.

## Sleutelrotatie

Sleutelrotatie is expliciet onderhoud: pauzeer uitgaande verwerking, vervang de hostsleutel,
voer credentials opnieuw in en laat een nieuwe sessie verkrijgen. Oude ciphertext is met de
nieuwe sleutel onleesbaar. Herstel via de oude sleutel/recoverykopie of opnieuw koppelen; er is
geen stilzwijgende plaintext-fallback. Verwijderen is ook na een sleutelwissel mogelijk.

## API

`GET/PUT/DELETE /api/beheer/sportlink-extensie/rollen/{rolNaam}/autologin` is admin-only, alleen
voor de primaire club en de rol `Wedstrijdzaken`. Antwoorden hebben `Cache-Control: no-store`.

PUT accepteert maximaal 8192 bytes JSON met `username`, `password`, `totpSecret`, optioneel
`totpAlgorithm` (SHA1/SHA256/SHA512), `totpDigits` (6/8) en `totpPeriodSeconds` (30/60). GET en
de schrijfresponses retourneren uitsluitend `configured`, `enabled`, `lastLoginUtc`,
`retryAfterUtc` en een generieke `lastError`. Geen secret wordt teruggegeven. Een permanente
blokkade heeft een retrydatum in jaar 9999 en `enabled=false`; het is een status, geen planning.

## Validatie vóór productie

- RFC 6238-testvectoren, encryptie/tampering/AAD, callback-state, PKCE, redirect/formaction-
  controles, onbekende challenges, cooldown, circuit breaker en opslagvolgorde met fictieve data.
- Database-integratietests voor encryptie, clubisolatie, verwijdering en uitsluiting tussen
  onafhankelijke store-instanties; autorisatietests voor admin-only en afwijzing van andere clubs.
- Builds van beide tiers en frontend, toepasselijke CI-guards en browser-smokecheck.
- De eigenaar heeft de acceptatieproef afgerond verklaard volgens de door hem vastgestelde
  duur van 14 uur en 39 minuten zonder verbreking. De productie-release en productie-initialisatie
  volgen de aparte releaseprocedure; dit is geen aanvullende openstaande auth-acceptatietest.

De expliciete eenmalige implementatie-uitzondering voor Codex staat in issue #1411. Agents
hebben geen echte credentials nodig voor ontwikkeling en gebruiken geen echte Sportlink-tokens.
