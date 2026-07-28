CREATE VIEW [planner].[AlleWedstrijdenOpVeld]
AS
-- Thuiswedstrijden op eigen accommodatie (gefilterd op Accommodatie uit dbo.AppSettings)
-- Speelduur exclusief via dbo.Speeltijden (WedstrijdTotaal = speeltijd + rust).
-- Sportlink [Duration] uit matchdetails wordt niet meer gebruikt — DB is leidend (#291).
-- ClubCode uit CROSS APPLY ipv SELECT TOP 1 — voorkomt scalar subquery fout bij >1 rij (#428).
-- ClubCode en Wedstrijdcode als kolom (#580, #574): consumers filteren expliciet op club en
-- kunnen één wedstrijd op exacte code uitsluiten in plaats van op tekst-contains.
-- his.matches.ClubCode is nullable (migratie 001): niet-gestempelde rijen horen bij de
-- primaire club — daarom ISNULL(m.ClubCode, a.ClubCode). Zonder die tolerantie vallen
-- legacy-wedstrijden uit de bezetting → onderschatte bezetting → dubbele boekingen.
--
-- Veldkoppeling via OUTER APPLY i.p.v. RTRIM(LEFT(m.[veld], 6)) (#719): die afkap op zes tekens
-- registreerde een wedstrijd op "veld 10" als bezetting op "veld 1", waardoor veld 10 vrij leek en
-- er een tweede wedstrijd op hetzelfde veld en tijdstip bij kon — een dubbele boeking. Een veldnaam
-- langer dan zes tekens ("hoofdveld") viel volledig uit de bezetting weg. De matching is nu gelijk
-- aan FunctionApp/Planner/VeldResolutie.cs en PlannerShared.ResolveVeld: exacte veldnaam, of veldnaam
-- plus een spatie en de subpositie, langste veldnaam eerst.
--
-- LET OP: deze view staat óók als CREATE OR ALTER in Database/Script.PostDeployment1.sql, en CI rolt
-- alléén dat script uit. Wijzig altijd beide — VeldResolutieDriftTests bewaakt dat.
SELECT
    CAST(m.[kaledatum] AS DATE)                                                     AS Datum,
    CAST(m.[aanvangstijd] AS TIME)                                                  AS AanvangsTijd,
    DATEADD(MINUTE,
        s.[WedstrijdTotaal],
        CAST(CAST(m.[kaledatum] AS DATE) AS DATETIME) + CAST(m.[aanvangstijd] AS DATETIME)
    )                                                                               AS EindTijd,
    v.[VeldNummer],
    COALESCE(s.[Veldafmeting], 1.00)                                                AS VeldDeelGebruik,
    t.[leeftijdscategorie]                                                          AS LeeftijdsCategorie,
    m.[teamnaam]                                                                    AS TeamNaam,
    m.[wedstrijd]                                                                   AS Wedstrijd,
    v.[Subpositie]                                                                  AS VeldSubpositie,
    'Competitie'                                                                    AS Bron,
    ISNULL(m.[ClubCode], a.[ClubCode])                                              AS ClubCode,
    CAST(m.[wedstrijdcode] AS BIGINT)                                               AS Wedstrijdcode
FROM [his].[matches] m
CROSS APPLY (SELECT TOP 1 [ClubCode], [Accommodatie] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1 ORDER BY [ClubCode]) a
LEFT JOIN [his].[teams] t
    ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
   AND ISNULL(t.[ClubCode], a.[ClubCode]) = ISNULL(m.[ClubCode], a.[ClubCode])
LEFT JOIN [dbo].[Speeltijden] s
    ON s.[Leeftijd] = CASE
        WHEN m.[teamnaam] LIKE a.[ClubCode] + ' G[0-9]%' THEN 'G'
        ELSE REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
    END
   AND s.[ClubCode] = a.[ClubCode]
OUTER APPLY (
    SELECT TOP 1
        vv.[VeldNummer],
        NULLIF(LTRIM(SUBSTRING(LTRIM(RTRIM(REPLACE(ISNULL(m.[veld], ''), '  ', ' '))), LEN(vn.[Naam]) + 1, 100)), '') AS [Subpositie]
    FROM [dbo].[Velden] vv
    CROSS APPLY (SELECT LTRIM(RTRIM(REPLACE(ISNULL(vv.[VeldNaam], ''), '  ', ' '))) AS [Naam]) vn
    WHERE vv.[ClubCode] = a.[ClubCode]
      AND LEN(vn.[Naam]) > 0
      AND (
            LTRIM(RTRIM(REPLACE(ISNULL(m.[veld], ''), '  ', ' '))) = vn.[Naam]
            OR (
                 LEN(LTRIM(RTRIM(REPLACE(ISNULL(m.[veld], ''), '  ', ' ')))) > LEN(vn.[Naam])
                 AND LEFT(LTRIM(RTRIM(REPLACE(ISNULL(m.[veld], ''), '  ', ' '))), LEN(vn.[Naam])) = vn.[Naam]
                 AND SUBSTRING(LTRIM(RTRIM(REPLACE(ISNULL(m.[veld], ''), '  ', ' '))), LEN(vn.[Naam]) + 1, 1) = ' '
               )
          )
    ORDER BY LEN(vn.[Naam]) DESC
) v
WHERE m.[accommodatie] LIKE '%' + a.[Accommodatie] + '%'
  AND m.[status] <> 'Afgelast'
  AND m.[aanvangstijd] IS NOT NULL
  AND v.[VeldNummer] IS NOT NULL
  AND s.[WedstrijdTotaal] IS NOT NULL

UNION ALL

-- Planner-scheduled matches (alleen niet-vervallen; vervallen = overgenomen in Sportlink)
SELECT
    [Datum],
    [AanvangsTijd],
    [EindTijd],
    [VeldNummer],
    [VeldDeelGebruik],
    [LeeftijdsCategorie],
    [TeamNaam],
    COALESCE([TeamNaam], '') + ' - ' + COALESCE([Tegenstander], '')                 AS Wedstrijd,
    ''                                                                              AS VeldSubpositie,
    'Planner'                                                                       AS Bron,
    [ClubCode],
    [SportlinkWedstrijdCode]                                                        AS Wedstrijdcode
FROM [planner].[GeplandeWedstrijden]
WHERE [Status] <> 'Geannuleerd'
  AND [IsVervallen] = 0;
