# Automatische Sportlink-login (#1411)

## Doel en status

De eigenaar heeft bevestigd dat na tien uur een nieuwe login nodig is en dat Sportlink de
machine-to-machine-aanvraag heeft afgewezen. Deze opt-in functie gebruikt daarom het normale
account met wachtwoord en TOTP. MFA blijft actief. De runtime genereert de code uit de
legitiem ingestelde authenticator-instelsleutel. Een actuele zescijferige code opslaan werkt niet.

De provider ondersteunt de bekende Keycloak-formulieren via authorization code + PKCE S256.
Tests gebruiken lokale fixtures en fictieve gegevens. De huidige Sportlink-schermen en de
gehele tien-uurscyclus moeten vóór ingebruikname door de eigenaar worden geverifieerd. Een
onbekend formulier, CAPTCHA, gewijzigd MFA-proces of extra identity provider wordt geweigerd.
Een geslaagde build of fixturetest is geen bewijs van live compatibiliteit.

## Installatie en invoer

1. Publiceer na review de bijbehorende migratie: PostgreSQL `029_sportlinkautologin.sql` of
   SQL Server `dbo.SportlinkAutoLogin` via het idempotente
   `Database/Script.PostDeployment1.sql` (synchroon met het schema-project). Geen migratie door een agent op
   productie en geen nieuwe cloudresource nodig.
2. Laat de beheerder een cryptografisch willekeurige sleutel van 32 bytes genereren en als
   Base64 opslaan in de bestaande Function App-secretinstelling
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
5. Controleer de beheerstatus. De live controle blijft een afzonderlijke menselijke actie.

Met een geldige hostsleutel gebruiken beide tiers de nieuwe encrypted tokenstore. Zonder rij
mag de bestaande tokenstore eenmalig als bron dienen; geroteerde tokens worden vervolgens
alleen in de nieuwe versleutelde opslag geschreven. De handmatige token-upload wordt met deze
modus geweigerd zodat die niet buiten de coördinatie om een verouderde tokenbron bijwerkt.
Oude bootstrapwaarden moeten na succesvolle migratie door de beheerder uit de oude opslag
worden verwijderd. Verwijder de hostsleutel daarna niet als manier om de functie uit te zetten:
dat zou de legacy-modus terug inschakelen.

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

## Verwijderen, herstel en rotatie

Verwijderen wist credentials en het afgeleide opgeslagen refresh-token. Een lege rij voorkomt
terugvallen op een oude bootstrapwaarde. Een al gecachet access-token kan zijn resterende
geldigheidsduur behouden; voor onmiddellijke intrekking moet de beheerder het account/de sessie
bij Sportlink intrekken. Dit endpoint is geen providerlogout.

Een verkeerd wachtwoord, gewijzigde seed of onbekend scherm vereist herstel in de beheerpagina.
Na permanente blokkade of drie mislukte pogingen kan opnieuw opslaan de blokkade opheffen.
Geen token, responsebody, cookie of wachtwoord in supportmeldingen opnemen.

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
- Eigenaar doorloopt minstens 24 uur en twee nieuwe sessies inclusief herstart. Controleer
  alleen gesaneerde status/timestamps en of dezelfde functionele rechten behouden blijven.
  Test geen wedstrijdschrijfacties tijdens deze authenticatieproef.

De expliciete eenmalige implementatie-uitzondering voor Codex staat in issue #1411. Agents
hebben geen echte credentials nodig voor ontwikkeling en gebruiken geen echte Sportlink-tokens.
