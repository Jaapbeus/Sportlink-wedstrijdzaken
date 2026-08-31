@{
    # ══════════════════════════════════════════════════════════════════════════
    # Verwachtingen voor de zelftest (#851) — de ENIGE bron van asserties.
    #
    # Zowel Test-PostgresTier.ps1 als de skill .claude/skills/zelftest/SKILL.md lezen dit bestand.
    # De skill verzint niets: staat een route hier niet in, dan is die route UNTESTED, geen PASS.
    #
    # WAAROM DIT BESTAAT
    # ------------------
    # Blazor WebAssembly geeft op ELKE route HTTP 200 terug (index.html-fallback). Een statuscode
    # bewijst dus niets. Test-App.ps1 test vandaag nog /veldbeschikbaarheid en /uitgesloten-emails —
    # allebei routes die niet meer bestaan, allebei al maanden groen. Dat kan niet nog eens: de
    # routelijst hieronder wordt bij elke run vergeleken met de @page-directives in de broncode,
    # en een verschil in beide richtingen is een fout.
    #
    # ONDERHOUD
    # ---------
    # Nieuwe pagina toegevoegd? Zet hem hieronder mét een assertie. Een assertie die alleen
    # "geen foutmelding" controleert is niet genoeg — nul rijen geeft ook geen foutmelding.
    # ══════════════════════════════════════════════════════════════════════════

    schemaVersion = 1

    # De democlub. Vaste waarde in broncode, seeds en testdata; nooit vervangen door een echte club.
    demoClub = 'ALLSTARS'

    # ──────────────────────────────────────────────────────────────────────────
    # Rijtellingen die de demodata-seed moet opleveren.
    #
    # Afgeleid uit het AllStars-blok in Database/Script.PostDeployment1.sql. Dit is een CONTRACT,
    # geen momentopname: het is bewust opgeschreven vóór er een Postgres-seed bestond, zodat de
    # test die seed toetst in plaats van hem te beschrijven.
    #
    # De twee blokkades die dit aanvankelijk lieten falen zijn opgelost en de verwachtingen worden
    # sindsdien echt gehaald: #853 (de business-key-kolom was GENERATED ALWAYS terwijl de seed
    # erin schreef) en #856 (de seed sloeg zichzelf over als de historische tabellen nog niet
    # bestonden). De Blocked-verwijzingen zijn daarom weg — laten staan zou suggereren dat hier
    # nog iets ongemeten is.
    # ──────────────────────────────────────────────────────────────────────────
    rowCounts = @(
        @{ Key = 'velden';             Exact = 3;   Reden = 'nummers 101-103, vermijden PK-conflict met de primaire club' }
        @{ Key = 'veldbeschikbaarheid';Exact = 21;  Reden = '3 velden x 7 dagen' }
        @{ Key = 'teams';              Exact = 28;  Reden = '14 categorieen x 2' }
        @{ Key = 'teambegeleiding';    Exact = 28;  Reden = 'een begeleider per team' }
        @{ Key = 'matches';            Exact = 224; Reden = '28 teams x 8 ronden' }
        @{ Key = 'speeltijden';        Min   = 1;   Reden = 'gekopieerd van de primaire club, aantal varieert' }
        @{ Key = 'teamregels';         Min   = 1;   Reden = 'een demoregel' }
    )

    # ──────────────────────────────────────────────────────────────────────────
    # De 13 functionele routes van de Admin-GUI.
    #
    # Context: 'demo' of 'primair'. De demomodus zet de clubcode in localStorage en stuurt hem als
    # header mee; de planner neemt daar een ANDER codepad. Het niet-demo-plannerpad roept een
    # externe dienst aan en is lokaal dus niet offline te verifieren — daarom OutOfScope.
    # ──────────────────────────────────────────────────────────────────────────
    routes = @(
        @{ Path = '/';                         Context = 'demo';    Assert = 'Versienummer zichtbaar in de header en de clubnaam bevat de democlubcode. Geen database-overlay.'; ReadsDatabase = $false }
        # De drie routes hieronder stonden geblokkeerd op #856 (en #853). Die blokkade gold voor de
        # BESTAANDE variant, waar het deployscript de bronnentabellen niet aanmaakt en de demoseed
        # zichzelf stil overslaat. Op de Postgres-tier maakt de zelftest die tabellen zelf aan
        # vóór de seed, en G4 bevestigt met rijtellingen dat er 28 teams en 224 wedstrijden staan.
        # De browsersweep heeft dat op 2026-09-01 ook echt gezien: /dagplanning toont de
        # demowedstrijden en /teambegeleiding vult de teamkeuzelijst met alle 28 teams.
        #
        # Een blokkade die alleen op de andere variant klopt, laat hier dekking wegvallen die er
        # wél is — even schadelijk als een overgeslagen assertie die groen meldt.
        @{ Path = '/dagplanning';              Context = 'demo';    Assert = 'Minstens een wedstrijdregel zichtbaar voor de eerstvolgende zaterdag, met teamnaam en aanvangstijd. LET OP: de veldkolom is bij demodata leeg — de seed plant niets op een veld in, dus daar valt niets over te beweren (zelfde nuance als bij api/planner/veldbezetting hieronder).' }

        # De eis "minstens een adres op het gereserveerde testdomein" is bewust geschrapt: het
        # endpoint achter deze pagina geeft NOOIT e-mailadressen terug (naam en rol, meer niet).
        # Dat is een AVG-ontwerpkeuze, geen tekortkoming — de assertie kon dus per definitie niet
        # slagen. Wat de pagina wél bewijst is dat de canonieke teamlijst werkt: de keuzelijst
        # bevat alle demoteams, en die lijst komt uit public.teams.
        @{ Path = '/teambegeleiding';          Context = 'demo';    Assert = 'De teamkeuzelijst bevat alle 28 demoteams (bewijs dat de canonieke teamlijst gevuld is). Geen e-mailadressen: die geeft het endpoint bewust nooit terug.' }

        # Geblokkeerd op #952 (afgesplitst van #949, dat alleen de verwachtingen hierboven dekte en
        # daarom bij de eerstvolgende release sluit): zes endpoints achter deze pagina bestaan niet
        # op de Postgres-tier
        # (404, geen niet-geimplementeerd-antwoord — daarom viel het buiten de stub-telling). De
        # pagina meldt zichtbaar "Teams ophalen mislukt: HTTP 404" en toont nul wedstrijden.
        @{ Path = '/testdata/wedstrijden';     Context = 'demo';    Assert = 'Exact 224 wedstrijden. Deze route is alleen zichtbaar in demomodus.'; Blocked = @(952) }
        @{ Path = '/instellingen';             Context = 'demo';    Assert = 'De testmodus-melding is zichtbaar. Een gevulde instellingenpagina is hier FOUT — dat bewijst juist dat de clubscheiding werkt.' }
        @{ Path = '/instellingen/velden';      Context = 'demo';    Assert = 'Drie velden en 21 beschikbaarheidsrijen.' }
        @{ Path = '/instellingen/speeltijden'; Context = 'demo';    Assert = 'Minstens een speeltijdregel.' }
        @{ Path = '/instellingen/thema';       Context = 'primair'; Assert = 'Kleurvelden gevuld met geldige hexwaarden uit de instellingen.' }
        @{ Path = '/voorkeurstijden';          Context = 'primair'; Assert = 'De rij die fase 8 aanmaakt is zichtbaar. Leeg is niet genoeg: dan bewijst de pagina niets.'; DependsOnCrud = 'voorkeurstijden' }
        @{ Path = '/teamaliassen';             Context = 'primair'; Assert = 'De alias die fase 8 aanmaakt is zichtbaar.'; DependsOnCrud = 'teamaliassen' }
        @{ Path = '/email-templates';          Context = 'demo';    Assert = 'Minstens een sjabloonsleutel zichtbaar met een niet-leeg onderwerp — de democlub heeft er sinds #911 twee uit de seed. In de primaire context is een lege lijst op een verse database juist correct.' }
        @{ Path = '/leermomenten';             Context = 'primair'; Assert = 'De tabel rendert en de statistiek geeft een getal terug.' }
        @{ Path = '/email-tester';             Context = 'demo';    Assert = 'De pagina rendert. Let op: deze doet bij het laden geen enkele API-aanroep, dus openen alleen bewijst niets over de database.'; ReadsDatabase = $false }
    )

    # Negatieve controle. Krijgt een niet-bestaande route dezelfde uitkomst als een echte pagina,
    # dan meet de sweep niets en is de hele browserfase ongeldig.
    negativeControlRoute = '/pagina-bestaat-niet-zelftest'

    # ──────────────────────────────────────────────────────────────────────────
    # API-endpoints. Een statuscode is niet genoeg: elk endpoint krijgt een inhoudseis.
    #
    # G6 in Test-PostgresTier.ps1 loopt exact deze lijst af en heeft voor elk pad een
    # geïmplementeerde assertie. Staat een pad hier wel maar kent de poort er geen assertie voor,
    # dan is dat een FOUT — niet een stilzwijgende overslag. Zo kan deze lijst niet uit de pas
    # gaan lopen met wat er daadwerkelijk gemeten wordt.
    #
    # 'Context = demo' stuurt de democlubcode als header mee; zonder Context praat de aanroep
    # tegen de primaire club van de deployment.
    # ──────────────────────────────────────────────────────────────────────────
    apiEndpoints = @(
        @{ Path = 'api/health';                      Assert = 'Bevat een tier-veld dat de verwachte tier noemt.' }
        @{ Path = 'api/beheer/settings';             Assert = 'Bevat de leesbare planning en de volgende momenten — die verdwijnen stil bij afwijkende kolomcasing.' }
        @{ Path = 'api/beheer/velden';               Assert = 'Drie velden in demomodus.'; Context = 'demo' }
        @{ Path = 'api/beheer/veldbeschikbaarheid';  Assert = '21 rijen in demomodus.'; Context = 'demo' }
        # LET OP — dit endpoint leest public.teams (de canonicalisatietabel), NIET his.teams (de
        # ETL-historie die rowCounts hierboven telt). Dat zijn twee verschillende tabellen met
        # dezelfde naam in de volksmond; de eerdere formulering "28 teams" haalde ze door elkaar.
        # Niet langer geblokkeerd (#931 opgelost). public.teams wordt gevuld door de
        # teamcanonicalisatie, en die draaide alleen als onderdeel van een echte synchronisatie —
        # die deze run niet heeft. Sinds #946 bestaat daar een expliciet beheerpad voor
        # (POST /api/beheer/teams/herstel), en poort G5b roept dat pad aan vóórdat G6 meet. De
        # lijst die hier geteld wordt is dus door productiecode opgebouwd, niet door een seed.
        #
        # Een ondergrens en niet 'geldige lijst': dit endpoint geeft 200 met een LEGE lijst als de
        # canonieke lijst leeg is — nooit een foutcode. Zonder telling zou de assertie precies de
        # toestand groen melden die zij moet uitsluiten.
        @{ Path = 'api/beheer/teams';                Assert = 'Exact 28 teamnamen uit public.teams voor de democlub — evenveel als de demoseed aan teams neerzet. Een lege lijst geeft hier 200, dus zonder telling meet deze poort niets.'; Context = 'demo' }
        # Niet langer geblokkeerd (#858 opgelost): de maskering loopt nu via het gedeelde
        # Planner.Shared.AvgMaskering — hoofdletterongevoelig, en het gooit als er niets te
        # maskeren viel. De demoseed levert sinds dezelfde wijziging twee e-maillogrijen, zodat
        # deze assertie iets meet in plaats van een lege lijst te bevestigen.
        @{ Path = 'api/beheer/email-log';            Assert = 'Minstens 2 rijen, en ELK afzenderveld is gemaskeerd (begint met ***). Een onvermaskerd adres is een AVG-bevinding, geen cosmetisch defect.'; Context = 'demo' }
        # #911: de demodata-seed levert nu twee sjablonen voor de democlub op BEIDE tiers, dus deze
        # assertie meet weer iets in plaats van een lege lijst die twee heel verschillende oorzaken
        # kan hebben (geen seed vs. een stilgevallen sjabloonquery).
        @{ Path = 'api/beheer/templates';            Assert = 'Minstens een sjabloon met een niet-leeg onderwerp; voor de democlub minimaal de sleutels bevestiging en buiten_scope.'; Context = 'demo' }
        @{ Path = 'api/beheer/voorkeurstijden';      Assert = 'Geldige lijst; na fase 8 bevat hij de testrij.' }
        @{ Path = 'api/beheer/teamregels';           Assert = 'Minstens een regel voor de democlub.'; Context = 'demo' }
        @{ Path = 'api/beheer/uitgesloten-emails';   Assert = 'Geldige lijst.' }
        @{ Path = 'api/beheer/teamaliassen';         Assert = 'Geldige lijst; na fase 8 bevat hij de testalias.' }
        @{ Path = 'api/beheer/sync/status';          Assert = 'Elke datumwaarde eindigt op een UTC-markering en ligt niet in de toekomst.' }
        @{ Path = 'api/beheer/leermomenten/stats';   Assert = 'Numerieke waarden.' }

        # De twee planner-endpoints die vandaag op de Postgres-tier bestaan (#888). Ze stonden
        # hiervoor NIET in deze lijst en werden dus door niets gedekt — terwijl de plannerlaag juist
        # het onderdeel is waar een omzetting stil fout kan gaan (lege bezetting leest als een
        # rustige dag, niet als een defect).
        #
        # {EERSTVOLGENDE_ZATERDAG} wordt door Test-PostgresTier.ps1 vervangen door de datum waarop de
        # demoseed zijn eerste speelronde zet. Bewust een token en geen vaste datum: die zou binnen
        # een week verlopen en de meting op een lege dag laten uitkomen.
        @{ Path = 'api/planner/veldbezetting?datum={EERSTVOLGENDE_ZATERDAG}'; Context = 'demo'
           Assert = 'Minstens een bezettingsrij op de eerste demospeeldag, elk met een teamnaam en een aanvangstijd. Leeg is FOUT: de seed zet 28 teams x 8 ronden neer, waarvan de helft thuis speelt. Let op: veld is bij demodata NULL — de seed plant niets op een veld in, dus daar valt niets over te beweren.' }

        # Niet langer geblokkeerd (#931 opgelost): poort G5b bouwt de canonieke teamlijst op via
        # het beheerpad uit #946, zodat de teamresolutie hier iets te resolven heeft.
        #
        # De eis op BEZETTE zaterdagen is essentieel en verving een te zwakke assertie. De
        # zaterdaglijst wordt opgebouwd van vandaag tot het seizoenseinde, volledig los van de
        # wedstrijden — die lijst is dus per definitie gevuld zodra het team herkend wordt. Een
        # kapotte aliaskoppeling levert daarmee een volle agenda zonder één bezette dag op, en dat
        # leest als een rustig seizoen in plaats van als een defect.
        @{ Path = 'api/planner/team-schedule?team=AllStars%20JO13%201'; Context = 'demo'
           Assert = 'Object met zaterdagen- en wedstrijden-lijst; minstens een zaterdag met status bezet. Alleen een niet-lege zaterdaglijst is niet genoeg: die loopt sowieso tot het seizoenseinde.' }
    )

    # De toepassingsnaam die de Postgres-tier op elke verbinding meegeeft
    # (FunctionApp.Postgres/PostgresDatabaseConfig.cs). G5 zoekt hem terug in pg_stat_activity:
    # dat is het bewijs dat de applicatie met DEZE databaseserver praat, onafhankelijk van wat de
    # applicatie in /api/health over zichzelf beweert.
    applicationName = 'SportlinkFunctionAppPostgres'

    # ──────────────────────────────────────────────────────────────────────────
    # Schrijfpaden. Elke ronde: aanroep -> herladen in de GUI -> controle in de database.
    # Alleen alle drie samen is groen. Alles met deze voorvoegsels wordt in fase 9 opgeruimd,
    # en fase 8 eindigt met de controle dat de tellingen uit fase 3 exact hersteld zijn.
    # ──────────────────────────────────────────────────────────────────────────
    crudPrefix = 'ZELFTEST-'
    crudCases  = @(
        @{ Key = 'velden';            Waarom = 'Booleaanse kolommen verschillen per engine — bewijst of alle vergelijkingen zijn omgezet.' }
        @{ Key = 'veldbeschikbaarheid';Waarom = 'Tijdwaarden moeten identiek terugkomen; bevat ook een bewuste validatiefout die de rij ongewijzigd moet laten.' }
        @{ Key = 'voorkeurstijden';   Waarom = 'Volledige levenscyclus inclusief verwijderen; de wijzigingstijdstempel MOET veranderen bij een aanpassing.' }
        @{ Key = 'speeltijden';       Waarom = 'De sleutel staat in het pad — test tegelijk URL-codering en hoofdlettergevoeligheid.' }
        @{ Key = 'templates';         Waarom = 'Terugzetten naar de standaard bewijst dat de standaardteksten zijn meegemigreerd.' }
        @{ Key = 'teamaliassen';      Waarom = 'Raakt de teamresolutie, waar hoofdlettergevoeligheid speelt.' }
        @{ Key = 'instellingen';      Waarom = 'Twee tabellen tegelijk: de instelling en de auditregel met oude en nieuwe waarde.' }
    )

    # ──────────────────────────────────────────────────────────────────────────
    # Achtergrondpaden. Geen enkele pagina raakt deze; twee ervan voeren de AVG-bewaartermijn uit.
    # ──────────────────────────────────────────────────────────────────────────
    timerFunctions = @(
        @{ Name = 'CleanupEmailVerwerking';   Assert = 'Een geprepareerde te oude rij is geanonimiseerd respectievelijk verwijderd.'; Discipline = 'dpo' }
        @{ Name = 'CleanupTeambegeleiding';   Assert = 'Idem voor de begeleidings- en importgegevens.'; Discipline = 'dpo' }
        @{ Name = 'EmailProcessor';           Assert = 'Draait zonder fout tegen een lege mailbox.' }
        @{ Name = 'FetchAndStoreApiData';     Assert = 'Draait tegen de lokale fixtureserver, nooit tegen de echte bron.'; Blocked = @(867) }
    )

    # ──────────────────────────────────────────────────────────────────────────
    # Bewust lastige rijen. Een schone seed verbergt juist de bekende risico's.
    # Deze worden DIFFERENTIEEL beoordeeld: de eis is niet "Postgres geeft N", maar
    # "Postgres geeft hetzelfde als de basismeting". Zo hoef je de juiste N niet te kennen.
    # ──────────────────────────────────────────────────────────────────────────
    dirtyRows = @(
        @{ Id = 'D1'; Wat = 'Teamnaam met afwijkende kast tussen de team- en wedstrijdtabel.';           Bewijst = 'Hoofdlettergevoeligheid (#820). Faalt dit alleen na de omzetting, dan dreigt een dubbele veldboeking.' }
        @{ Id = 'D2'; Wat = 'Teamnaam met een spatie aan het eind.';                                     Bewijst = 'De ene engine negeert die bij vergelijking, de andere niet.' }
        @{ Id = 'D3'; Wat = 'Teamnaam die op het G-patroon moet matchen.';                               Bewijst = 'De karakterklasse in het patroon bestaat niet in Postgres — G-teams verdwijnen dan stil uit de bezetting (#819).' }
        @{ Id = 'D4'; Wat = 'Accommodatienaam met afwijkende kast.';                                     Bewijst = 'Patroonvergelijking met samengevoegde tekst.' }
        @{ Id = 'D5'; Wat = 'Begeleidingsrij met afwijkende kast in de teamsleutel.';                    Bewijst = 'Normalisatie die maar aan een kant wordt toegepast.' }
        @{ Id = 'N1'; Wat = 'Twee identieke rijen met een lege waarde in een sleutelkolom.';             Bewijst = 'De samengestelde sleutel voegt ze samen tot een rij in plaats van er twee te maken.' }
        @{ Id = 'N2'; Wat = 'Dezelfde invoer nogmaals samenvoegen.';                                     Bewijst = 'De wijzigingstijdstempel blijft ongewijzigd voor onveranderde rijen.' }
        @{ Id = 'N3'; Wat = 'Twee rijen waarvan de sleutelkolommen elkaars spiegelbeeld leeg hebben.';   Bewijst = 'Het scheidingsteken voorkomt dat twee verschillende sleutels samenvallen.' }
        @{ Id = 'U1'; Wat = 'Een bekend tijdstip, weggeschreven en teruggelezen.';                        Bewijst = 'De round-trip door de tijdzone heen. Draait alleen zinvol met de databaseserver op een NIET-UTC tijdzone (#854).' }
    )

    # De databasecontainer draait bewust NIET op UTC. Op UTC valt de fout uit #854 samen met
    # correct gedrag en bewijst de meting niets.
    containerTimeZone = 'Europe/Amsterdam'
}
