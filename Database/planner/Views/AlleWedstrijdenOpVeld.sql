CREATE VIEW [planner].[AlleWedstrijdenOpVeld]
AS
-- Thuiswedstrijden op eigen accommodatie (gefilterd op Accommodatie uit dbo.AppSettings)
-- Speelduur exclusief via dbo.Speeltijden (WedstrijdTotaal = speeltijd + rust).
-- Sportlink [Duration] uit matchdetails wordt niet meer gebruikt — DB is leidend (#291).
-- ClubCode uit CROSS APPLY ipv SELECT TOP 1 — voorkomt scalar subquery fout bij >1 rij (#428).
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
    RTRIM(SUBSTRING(m.[veld], 7, 10))                                               AS VeldSubpositie,
    'Competitie'                                                                    AS Bron
FROM [his].[matches] m
CROSS APPLY (SELECT TOP 1 [ClubCode], [Accommodatie] FROM [dbo].[AppSettings] WHERE [SyncEnabled] = 1 ORDER BY [Id]) a
LEFT JOIN [his].[teams] t
    ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
LEFT JOIN [dbo].[Speeltijden] s
    ON s.[Leeftijd] = CASE
        WHEN m.[teamnaam] LIKE a.[ClubCode] + ' G[0-9]%' THEN 'G'
        ELSE REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
    END
   AND s.[ClubCode] = a.[ClubCode]
LEFT JOIN [dbo].[Velden] v
    ON RTRIM(LEFT(m.[veld], 6)) = v.[VeldNaam]
   AND v.[ClubCode] = a.[ClubCode]
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
    'Planner'                                                                       AS Bron
FROM [planner].[GeplandeWedstrijden]
WHERE [Status] <> 'Geannuleerd'
  AND [IsVervallen] = 0;
