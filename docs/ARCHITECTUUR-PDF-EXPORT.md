# PDF-export van planning en veldbezetting — QuestPDF

> Status: **gebouwd (#1363) en aangesloten (#1364)** op `GET planner/veldbezetting` en
> `POST planner/auto-plan` (`?format=html|pdf`, op beide tiers) en de schermen Planning en Veld
> optimalisatie (epic #1365). Architectuurbesluit: WZ-ADR-012 in
> [ARCHITECTUUR.md](ARCHITECTUUR.md) §9.

## 1. Wat er staat

| Onderdeel | Plek | Rol |
|---|---|---|
| `PlannerShareModel` / `PlannerShareWedstrijd` | `Planner.Shared/Deel/PlannerShareModel.cs` | Klein, tier- en formaatonafhankelijk documentmodel: titel, club, speeldag, regels met Tijd/Team/Tegenstander/Veld/Competitie/Scheidsrechter |
| `IVeldbezettingRegel` / `IPlanWedstrijdRegel` | idem | De velden die de builders lezen. Beide tiers laten hun eigen wire-DTO (`VeldbezettingItem`, `AutoPlanWedstrijdItem`) deze interfaces implementeren, zodat de mapping één keer bestaat |
| `PlannerShareModelBuilder` | `Planner.Shared/Deel/PlannerShareModelBuilder.cs` | `VanVeldbezetting(...)` (Planning-pagina) en `VanPlan(..., PlanWeergave.Huidig/Optimaal)` (Veld optimalisatie). Pure mapping, geen klok, geen I/O |
| `PlannerPdfGenerator.Genereer(model) → byte[]` | `Planner.Shared/Deel/PlannerPdfGenerator.cs` | De QuestPDF-implementatie: A4 liggend, tabel die over pagina's doorloopt met herhaalde kop, "Pagina x van y" |

De bestaande HTML-export (`Planner.Shared/PlannerHtmlGenerator.cs`, security-gehard in #1010/#603)
is bewust niet aangeraakt. De PDF is een parallelle, eenvoudige weergave uit een eigen model — geen
HTML→PDF-conversie.

**Een PDF kan alleen server-side worden gemaakt.** QuestPDF levert geen native bibliotheek voor
`browser-wasm`; de Blazor-app kan de generator dus niet zelf aanroepen. #1364 hoort de PDF via een
endpoint te leveren (het `?format=html`-patroon van `GET /api/planner/team-schedule` is het
precedent), op beide tiers.

**Hoe het is aangesloten (#1364).** De tier-onafhankelijke respons-logica (`?format=`/`?tab=`/datum
valideren, `PlannerShareModel` → `FileContentResult`/`ContentResult`) staat in
`Planner.Endpoints/Deel/PlannerDeelEndpointCore.cs`; de HTML-weergave van hetzelfde model in
`Planner.Shared/Deel/PlannerShareHtmlGenerator.cs` (zelfde kolommen als de PDF, alles
HTML-geëncodeerd, geen script). Elke `PlannerFunction.cs` doet alleen nog de databasevraag en één
`if (format != null)`. Autorisatie ongewijzigd: `veldbezetting` blijft `admin`+`user`
(`ExecuteAuthenticatedAsync`), `auto-plan` blijft `admin` (`ExecuteAsync`). De Blazor-kant gebruikt
`AdminApiClient.Deel.cs`; Veld optimalisatie laat de server de PDF opnieuw berekenen met de datum en
buffer waarmee het getoonde plan is gemaakt (`AutoPlanAsync` is een pure leesbewerking).

**Persoonsgegevens:** dezelfde velden als de pagina vandaag al toont (epic #1365, besluit 3). Geen
extra filtering, geen logging van de inhoud. De scheidsrechterkolom verschijnt alleen als minstens
één regel er een heeft; de huidige builders vullen hem niet, omdat geen van beide bron-DTO's het
veld bevat.

## 2. Licentie — QuestPDF Community License

QuestPDF is **niet** open source in OSI-zin. Het is *source-available* en dubbel gelicentieerd:
een gratis Community License en betaalde Professional/Enterprise-licenties. Beoordeeld op de tekst
die in het NuGet-pakket zelf zit (`LICENSE.md` in `QuestPDF 2026.9.1`, License Selection Guide en
Community License **versie 3.0, ingangsdatum 6 juli 2026**; samenvatting op
<https://www.questpdf.com/license/> en <https://www.questpdf.com/pricing>).

**Wie de Community License mag gebruiken** (een van de zeven categorieën):

| Categorie | Van toepassing op een club die deze repo forkt? |
|---|---|
| 5. Open-sourceproject onder een OSI-licentie | **Nee.** Deze repository heeft (nog) geen `LICENSE`-bestand, dus is er geen OSI-licentie. En ook mét zo'n licentie is het de *club* die QuestPDF in productie draait |
| 3. Charitatieve/algemeen-nut-organisatie | **Niet aannemen.** "Non-profit legal form or tax status alone is not sufficient." Een sportvereniging dient vooral haar leden; dat een vereniging hieronder valt is pleitbaar, niet zeker |
| 6. Kleine organisatie: jaaromzet **< USD 1.000.000** (vorig afgesloten boekjaar, geconsolideerd) | **Ja — dit is de categorie waarop elke club zich beroept.** Contributie, kantine, sponsoring en subsidies tellen allemaal mee als omzet |

Uitgesloten ongeacht omzet: overheidsorganisaties en beursgenoteerde bedrijven — niet relevant voor
een amateurvereniging.

**Conclusie: passend, onder voorwaarde.** Voor de overgrote meerderheid van de amateurverenigingen
blijft de jaaromzet ruim onder USD 1 miljoen. Een grote club (veel leden, eigen kantine, grote
sponsorinkomsten) kan die grens wél halen. Daarom:

1. **`Settings.License = LicenseType.Community` is een zelfverklaring van de club die deze
   installatie draait** — niet van deze repository. Sinds #1459 (eigenaarsbesluit 2026-10-03) is
   die verklaring een **clubinstelling, alleen aan/uit, standaard uit (fail-closed)**:
   `PdfExportIngeschakeld` (`public.appsettings.pdfexportingeschakeld`, migratie 034;
   `dbo.AppSettings.PdfExportIngeschakeld`). De beheerder zet hem aan bij Instellingen → PDF-export
   en bevestigt daarmee dat de voorwaarden gelden. De licentie wordt pas in
   `PlannerPdfGenerator.Genereer(model, pdfIngeschakeld: true)` gezet (niet meer in de statische
   constructor); `false` gooit. Boven die vangrail weigert de ene gedeelde beslissing
   `PlannerDeelEndpointCore.BeslisPdfAsync` `?format=pdf` met **409** zolang de instelling uit
   staat — op `planner/veldbezetting`, `planner/auto-plan` én `planner/auto-plan/deel` (#1460), op
   beide tiers. De 0/1-validatie van de instelling staat in `Planner.Endpoints/Admin/AppSettingsValidatieCore.cs`
   (samen met de overige settingsvalidatie). Per tier staat alleen de databasevraag
   (`Planner/PdfExportInstelling.IsIngeschakeldAsync`, zelfde pad en naam op beide tiers). De GUI
   verbergt de PDF-knop (`DeelPaneel.PdfOphalen = null`) op basis van `GET planner/pdf-export`
   (`Planner/PdfExportStatusFunction.cs` per tier), dat
   open staat voor elke ingelogde rol — `beheer/settings` is admin-only, en Planning is sinds #1400
   ook voor de rol `user`. De stand wordt opnieuw opgehaald bij een clubwissel. Geen migratie of
   seed zet hem voor een specifieke club aan; na de release moet de beheerder hem zelf inschakelen.
2. **Elke club controleert dat zelf bij het forken** (zie
   [SETUP-NIEUWE-CLUB.md](../SETUP-NIEUWE-CLUB.md) §1). Haalt een club de grens wel: Professional
   License kopen, of de PDF-export niet gebruiken. QuestPDF kent een overgangstermijn van 90 dagen
   na het boekjaar waarin de grens werd overschreden.
3. **Een versiebump is ook een licentiebump.** "The version of this Community License distributed
   or published with a given release of the Software governs your use of that release." Daarom staat
   QuestPDF in `.github/dependabot.yml` buiten de verzamel-PR (`exclude-patterns`): elke nieuwe versie
   komt als eigen PR, en wie die beoordeelt leest `LICENSE.md` in het pakket opnieuw.
4. "Code generated by an AI coding tool or agent on behalf of an organisation is treated as written
   by a Developer of that organisation." Dat verandert niets aan de categorie — die hangt aan de
   organisatie die het product in productie gebruikt, niet aan wie de code schrijft.

**Vergelijking met FluentAssertions (#1170).** Daar is de licentie afgewezen omdat de Xceed
Community License uitsluitend niet-commercieel gebruik toestaat, wat een contributie-innende
vereniging pleitbaar uitsluit. De QuestPDF Community License staat commercieel gebruik expliciet toe
en toetst alleen op omzet. Dat verschil is de reden dat deze wél past.

## 3. Platform — geen puur managed pakket

De aanname in #1363/#1365 dat QuestPDF "pure managed .NET zonder native binary" is, is **onjuist**.
Het pakket levert per platform twee native bibliotheken mee:

| Platform (RID) | Bestanden | Grootte |
|---|---|---|
| `linux-x64` (productie: Linux, Flex Consumption) | `libQuestPdfSkia.so`, `libqpdf.so` | 8,1 MB + 3,3 MB |
| `linux-arm64`, `linux-musl-x64`, `osx-x64`, `osx-arm64`, `win-x64`, `win-x86`, `win-arm64` | idem per platform | 9–12 MB per platform |
| *(alle platforms)* | `QuestPDF.dll` + het meegeleverde lettertype `QuestPDF.Fonts.Lato.br` | 0,7 MB + 3,0 MB |

Dat veranderde het besluit niet — het risico van de afgewezen headless-Chromium-aanpak (honderden
MB, een eigen browserproces, geheugendruk) treedt hier niet op — maar het moest wel bewezen worden
op het productieplatform in plaats van aangenomen. Bewijs (2 oktober 2026, `QuestPDF 2026.9.1`):

- **Draait op Linux x64 met het Functions-image.** Een probe-app, gepubliceerd zoals `deploy.yml`
  dat doet (`dotnet publish -c Release`, zonder runtime-identifier), gedraaid in
  `mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated9.0` (`linux/amd64`, Debian 12,
  .NET 9.0.20): .NET kiest `runtimes/linux-x64/native/` via `deps.json`, de PDF begint met
  `%PDF-1.4`, heeft 3 pagina's, en tekstextractie vindt de titel en "Zoë’s Café", "Coöperatie Reünie
  é" zonder vervangteken.
- **Geen systeemfonts nodig.** Dezelfde probe in `mcr.microsoft.com/dotnet/runtime:9.0`, dat geen
  `/usr/share/fonts` heeft: identieke uitkomst, byte voor byte even groot. De generator zet
  `UseSystemFonts = false` en gebruikt uitsluitend het meegeleverde Lato.
- **Geen extra systeembibliotheken.** `ldd libQuestPdfSkia.so` toont alleen `libc`, `libstdc++`,
  `libm`, `libgcc_s` en `libpthread` — geen fontconfig of freetype van het besturingssysteem.
- **Let op de datering.** Bovenstaand bewijs is geleverd op het .NET 9-image van vóór de overstap op Flex Consumption en .NET 10 (v3.10.0.0/v3.11.0.0). Een herhaling op het huidige productie-image (`dotnet-isolated 10.0`) is niet vastgelegd; draai de probe opnieuw bij elke wijziging van runtime of QuestPDF-versie.
- **De unittests draaien op Linux x64:** `Planner.Shared.Tests` (`Deel`) in
  `mcr.microsoft.com/dotnet/sdk:9.0` (`linux/amd64`): 24 geslaagd, 0 gefaald. CI (ubuntu) draait ze
  bij elke PR.

Drie instellingen in de statische constructor van `PlannerPdfGenerator`, met reden:

| Instelling | Waarde | Waarom |
|---|---|---|
| `UseSystemFonts` | `false` | Azure Functions op Linux garandeert geen fonts; dezelfde invoer moet overal dezelfde PDF geven |
| `FontDiscoveryPath` | `null` | Anders scant QuestPDF bij de eerste PDF recursief de hele app-map naar fontbestanden — koude-starttijd voor niets |
| `ThrowOnMissingTextGlyphs` | `false` | Sinds 2026.9 standaard `true`: één emoji in een teamnaam zou de hele export laten mislukken. Nu wordt het een vervangteken |

### 3.1 Pakketgrootte — een merkbaar gevolg

Omdat `deploy.yml` zonder runtime-identifier publiceert, komen de native bibliotheken van **alle**
platforms in het deploypakket. Gemeten met `dotnet publish -c Release` vóór en na deze wijziging:

| Tier | Uitgepakt vóór → na | Zip vóór → na |
|---|---|---|
| Postgres (`FunctionApp.Postgres`) | 73,8 MB → 160,6 MB (**+86,8 MB**) | 19,9 MB → 59,4 MB (**+39,5 MB**) |
| SQL Server (`FunctionApp`) | 79,8 MB → 166,6 MB (**+86,8 MB**) | 22,0 MB → 61,5 MB (**+39,5 MB**) |
| Postgres, ter vergelijking met `-r linux-x64 --self-contained false` | 87,1 MB | 27,4 MB |

Dat valt ruim binnen de grens van het Consumption Plan (deploypakket max. 1 GB, Microsoft Learn
`azure-functions/functions-scale#service-limits`) en kost geen geld, maar het zip-pakket wordt ruim
twee keer zo groot, en dat pakket wordt bij elke koude start opgehaald. Het pakket alleen voor
`linux-x64` publiceren bespaart het grootste deel, maar wijzigt de productie-deploy en is daarom hier
**bewust niet** gedaan: dat hoort als eigen, apart te testen wijziging (bij voorkeur samen met de
Flex Consumption-migratie, epic #1063).

`Database.Postgres` verwijst ook naar `Planner.Shared`; het migratieprogramma
(`Database.Postgres.Cli`, gebruikt in de deployjob `db-migrate-postgres`) neemt QuestPDF dus
transitief mee. Het wordt nooit aangeroepen, en dat programma wordt niet naar Azure gedeployd.

## 4. Beveiliging en afhankelijkheden

- **CVE-controle:** `dotnet list package --vulnerable --include-transitive` op `Planner.Shared`,
  `Planner.Shared.Tests`, `FunctionApp.Postgres` en `FunctionApp`: geen kwetsbare pakketten
  (2 oktober 2026). De Dependency Vulnerability Scan in CI (Trivy) blijft dit bij elke PR doen.
- **Blinde vlek:** NuGet-advisories dekken het pakket, niet de native onderdelen die erin
  meegecompileerd zijn (Skia, qpdf, libpng, libjpeg-turbo, libwebp, freetype, harfbuzz — de lijst
  staat in `ExternalDependencyLicenses/` in het pakket). Een kwetsbaarheid in bijvoorbeeld libpng
  verschijnt dus niet als NuGet-advisory. De generator verwerkt alleen tekst uit onze eigen database
  (geen afbeeldingen, geen geüploade bestanden), wat het aanvalsoppervlak van die decoders klein
  houdt; bijblijven met QuestPDF-releases is de mitigatie.
- **Geen injectie-oppervlak zoals bij HTML.** Waarden gaan als tekst de PDF in via QuestPDF's
  `Text(...)`; er is geen markup die een teamnaam als opdracht kan interpreteren. De test
  `Genereer_TekstMetOpmaaktekens_WordtLetterlijkWeergegeven` legt dat vast.
- **Testafhankelijkheid:** `PdfPig` (Apache-2.0, puur managed) alleen in `Planner.Shared.Tests`, om
  de tekst uit de gegenereerde PDF terug te lezen.

## 5. Tests

| Testklasse | Wat ze bewijst |
|---|---|
| `Planner.Shared.Tests/Deel/PlannerShareModelBuilderTests.cs` | Mapping van beide bronnen, sortering (zonder tijd achteraan), Huidig/Optimaal-keuze en titel, afleiding van de tegenstander zonder te gokken, lege lijst |
| `Planner.Shared.Tests/Deel/PlannerPdfGeneratorTests.cs` | `%PDF-`-header, paginatelling, titel/club/tijd/team/veld in de geëxtraheerde tekst, nette PDF bij een lege lijst, Nederlandse tekens zonder vervangteken, emoji breekt de export niet, meerdere pagina's met paginanummer, metadata-titel |
| `FunctionApp.Tests/Planner/PlannerShareTierContractTests.cs` en `FunctionApp.Postgres.Tests/Planner/PlannerShareTierContractTests.cs` | De echte wire-DTO's van elke tier gaan zonder eigen mapping de gedeelde laag in — breekt de build zodra een tier een eigenschap hernoemt |
