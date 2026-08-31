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
        @{ Path = '/dagplanning';              Context = 'demo';    Assert = 'Minstens een wedstrijdregel zichtbaar voor de eerstvolgende zaterdag, met een veldnaam uit de demovelden.'; Blocked = @(856) }
        @{ Path = '/teambegeleiding';          Context = 'demo';    Assert = 'Exact 28 rijen; minstens een adres op het gereserveerde testdomein.'; Blocked = @(853, 856) }
        @{ Path = '/testdata/wedstrijden';     Context = 'demo';    Assert = 'Exact 224 wedstrijden. Deze route is alleen zichtbaar in demomodus.'; Blocked = @(856) }
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
        # public.teams wordt gevuld door de teamcanonicalisatie tijdens een synchronisatie, en die
        # is op de Postgres-tier nog niet vertaald (gedocumenteerd gat 2 van #890). Op een verse,
        # geseede database is het antwoord daarom leeg — op BEIDE tiers, want geen van beide seeds
        # vult deze tabel.
        @{ Path = 'api/beheer/teams';                Assert = 'De teamnamen uit public.teams voor de democlub.'; Context = 'demo'; Blocked = @(890) }
        @{ Path = 'api/beheer/email-log';            Assert = 'ELK afzenderveld is gemaskeerd. Een onvermaskerd adres is een AVG-bevinding, geen cosmetisch defect.'; Blocked = @(858) }
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

        # Kan pas gemeten worden als de zelftest een canonieke teamlijst heeft (#931): dit endpoint
        # resolvet de teamnaam via public.teams/public.teamaliassen, en die vult de demoseed niet.
        # Een 404 is hier dus correct gedrag van het endpoint, geen defect — vandaar Blocked en niet
        # weglaten: zo blijft zichtbaar dat hier dekking hoort te komen.
        @{ Path = 'api/planner/team-schedule?team=AllStars%20JO13%201'; Context = 'demo'; Blocked = @(931)
           Assert = 'Object met zaterdagen- en wedstrijden-lijst; de zaterdaglijst loopt tot het seizoenseinde en is dus niet leeg.' }
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
