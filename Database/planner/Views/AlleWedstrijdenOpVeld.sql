CREATE VIEW [planner].[AlleWedstrijdenOpVeld]
AS
-- Competition matches from Sportlink (home matches at Sportpark Spitsbergen only)
SELECT
    CAST(m.[kaledatum] AS DATE)                                                     AS Datum,
    CAST(m.[aanvangstijd] AS TIME)                                                  AS AanvangsTijd,
    DATEADD(MINUTE,
        COALESCE(CAST(md.[Duration] AS INT), s.[WedstrijdTotaal], 105),
        CAST(CAST(m.[kaledatum] AS DATE) AS DATETIME) + CAST(m.[aanvangstijd] AS DATETIME)
    )                                                                               AS EindTijd,
    v.[VeldNummer],
    COALESCE(s.[Veldafmeting], 1.00)                                                AS VeldDeelGebruik,
    t.[leeftijdscategorie]                                                          AS LeeftijdsCategorie,
    m.[teamnaam]                                                                    AS TeamNaam,
    m.[wedstrijd]                                                                   AS Wedstrijd,
    'Competitie'                                                                    AS Bron
FROM [his].[matches] m
LEFT JOIN [his].[matchdetails] md
    ON CAST(md.[InternCode] AS BIGINT) = CAST(m.[wedstrijdcode] AS BIGINT)
LEFT JOIN [his].[teams] t
    ON t.[teamnaam] = m.[teamnaam]
LEFT JOIN [dbo].[Speeltijden] s
    ON s.[Leeftijd] = t.[leeftijdscategorie]
LEFT JOIN [dbo].[Velden] v
    ON RTRIM(LEFT(m.[veld], 6)) = v.[VeldNaam]
WHERE m.[accommodatie] LIKE '%Spitsbergen%'
  AND m.[status] <> 'Afgelast'
  AND m.[aanvangstijd] IS NOT NULL
  AND v.[VeldNummer] IS NOT NULL

UNION ALL

-- Planner-scheduled matches
SELECT
    [Datum],
    [AanvangsTijd],
    [EindTijd],
    [VeldNummer],
    [VeldDeelGebruik],
    [LeeftijdsCategorie],
    [TeamNaam],
    COALESCE([TeamNaam], '') + ' - ' + COALESCE([Tegenstander], '')                 AS Wedstrijd,
    'Planner'                                                                       AS Bron
FROM [planner].[GeplandeWedstrijden]
WHERE [Status] <> 'Geannuleerd';
