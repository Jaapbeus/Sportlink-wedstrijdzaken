# KNVB Speeldagenkalenders — archief

Dit archief bevat lokale kopieen van de KNVB speeldagenkalenders en de overzichtspagina,
voor het geval de bronnen op knvb.nl gewijzigd of verwijderd worden na afloop van een seizoen.

## Bron

- Overzichtspagina: <https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/seizoensplanning/speeldagenkalenders>
- Lokaal: [knvb-speeldagenkalenders-overzicht.html](knvb-speeldagenkalenders-overzicht.html)

## Inhoud per seizoen

### Seizoen 2025/'26

| Bestand | Brondocument |
|---|---|
| [speeldagenkalender-veld-landelijk-2025-2026.pdf](2025-2026/speeldagenkalender-veld-landelijk-2025-2026.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29142/speeldagenkalender-veld-landelijk-2025-2026> |
| [speeldagenkalender-veld-landelijk-jeugd-2025-2026.pdf](2025-2026/speeldagenkalender-veld-landelijk-jeugd-2025-2026.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29396/speeldagenkalender-veld-landelijk-jeugd-2025-2026> |
| [speeldagenkalender-veld-noord-oost-2025-2026.pdf](2025-2026/speeldagenkalender-veld-noord-oost-2025-2026.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29143/speeldagenkalender-veld-noord-oost-2025-2026> |
| [speeldagenkalender-veld-west-2025-2026.pdf](2025-2026/speeldagenkalender-veld-west-2025-2026.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29144/speeldagenkalender-veld-west-2025-2026> |
| [speeldagenkalender-veld-zuid-2025-2026.pdf](2025-2026/speeldagenkalender-veld-zuid-2025-2026.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29145/speeldagenkalender-veld-zuid-2025-2026> |

### Seizoen 2026/'27

| Bestand | Brondocument |
|---|---|
| [speeldagenkalender-veld-landelijk-2026-2027.pdf](2026-2027/speeldagenkalender-veld-landelijk-2026-2027.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29859/speeldagenkalender-veld-landelijk-2026-2027> |
| [speeldagenkalender-veld-landelijk-jeugd-2026-2027.pdf](2026-2027/speeldagenkalender-veld-landelijk-jeugd-2026-2027.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29860/speeldagenkalender-veld-landelijk-jeugd-2026-2027> |
| [speeldagenkalender-veld-noord-2026-2027.pdf](2026-2027/speeldagenkalender-veld-noord-2026-2027.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29861/speeldagenkalender-veld-noord-2026-2027> |
| [speeldagenkalender-veld-oost-2026-2027.pdf](2026-2027/speeldagenkalender-veld-oost-2026-2027.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29862/speeldagenkalender-veld-oost-2026-2027> |
| [speeldagenkalender-veld-west-2026-2027.pdf](2026-2027/speeldagenkalender-veld-west-2026-2027.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29863/speeldagenkalender-veld-west-2026-2027> |
| [speeldagenkalender-veld-zuid-2026-2027.pdf](2026-2027/speeldagenkalender-veld-zuid-2026-2027.pdf) | <https://www.knvb.nl/downloads/sites/bestand/knvb/29864/speeldagenkalender-veld-zuid-2026-2027> |

> KNVB merkt zelf op dat sommige kalenders na initiele publicatie zijn herzien.
> Bij twijfel altijd verifieren bij de actuele versie op knvb.nl.

## Structuur PDF (West/Districten)

Matrix met **datum (rij) x competitieschema (kolom)**. Voorbeeldwaarden:

- `R1`...`R26` — competitierondes (regulier)
- `WD 1 NJ`, `WD 5 VJ` — wedstrijddag najaar / voorjaar
- `Beker poule`, `Beker`, `Q1 Beker BV` — bekerwedstrijden
- `Inh. / Bek.` — inhaaldag of beker
- `Vrij` — geen competitie
- `Start Fase 1/2/3/4`, `Week 2`, `Fase 3 (West I)` — fase-systeem voor jeugd/meiden
- `NC` — nacompetitie
- `Final League`, `Finales Districtsbeker`
- `Inhaal` — pure inhaaldag
- Pupillen 7x7 kolom: vrijdagdata zoals `19/09`, `03/10` voor pupillentoernooien
- Opmerkingen: feestdagen, schoolvakanties per regio (M/N/Z = Midden/Noord/Zuid)

## Structuur PDF (Landelijk)

Vergelijkbaar, kolommen voor 2e/3e/4e divisie + KNVB-bekerronden (1/8, 1/4, 1/2 finale, bekerfinale).
Landelijke jeugd kan afwijken door interlands (voetnoot in PDF).

## Hoe wordt dit gebruikt?

De inhoud is samengevat in de SQL-tabel `dbo.KnvbKalenderDag` voor seizoen 2025/'26
(West + Landelijk). Zie [Database/Script.PostDeployment1.sql](../../Database/Script.PostDeployment1.sql).

Toekomstige automatische import: zie open feature request op GitHub.
