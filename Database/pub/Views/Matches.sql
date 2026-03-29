CREATE VIEW [pub].[Matches]
	AS 
SELECT m.[wedstrijdcode]					AS WedstrijdCode
	,tt.teamnaam							AS VerenigingsTeam
	,md.Categorie							AS CompetitieCategorie
	,m.[competitienaam]						AS CompetitieNaam
	,m.[competitiesoort]					AS CompetitieSoort
	,md.[PouleCode]							AS CompetitiePouleCode
	,m.[wedstrijdnummer]					AS WedstrijdNummer
	,m.[wedstrijd]							AS Wedstrijd
	,md.[WedstrijdType]						AS WestrijdType
	,CAST(m.[datum] AS date)				AS WedstrijdDatum
	,CAST(m.[aanvangstijd] AS time)			AS WedstrijdAanvangsTijd
	,md.[Duration]							AS WestrijdDuur
	,m.[status]								AS WedstrijdStatus
	,md.[VeldNaam]							AS VeldNaam
	,md.[VeldLocatie]						AS VeldLocatie
	,m.[thuisteam]							AS TeamThuis
	,m.[thuisteamid]						AS TeamThuisId
	,m.[uitteam]							AS TeamUit
	,m.[uitteamid]							AS TeamUitId
	,m.[uitslag]							AS Uitslag
	,m.[uitslag-regulier]					AS UitslagRegulier
	,m.[uitslag-nv]							AS UitslagNv
	,m.[uitslag-s]							AS UitslagS
	,md.[ThuisScore]						AS UitslagThuisScore
	,md.[ThuisScoreRegulier]				AS UitslagThuisScoreRegulier
	,md.[ThuisScoreNV]						AS UitslagThuisScoreNv
	,md.[ThuisScoreS]						AS UitslagThuisScoreS
	,md.[UitScore]							AS UitslagUitScore
	,md.[UitScoreRegulier]					AS UitslagUitScoreRegulier
	,md.[UitScoreNV]						AS UitslagUitScoreNv
	,md.[UitScoreS]							AS UitslagUitScoreS
	,m.[verenigingswedstrijd]				AS IsVerenigingsWedstrijd
	,'Thuis'								AS IsThuisUitWedstrijd
	,md.[Opmerkingen]						AS DivOpmerkingen
	,md.[VerenigingScheidsrechterCode]		AS DivOfficialScheidsrechterCode
	,md.[VerenigingScheidsrechter]			AS DivOfficialScheidsrechter
	,md.[OverigeOfficialCode]				AS DivOfficialOverigeCode
	,md.[OverigeOfficial]					AS DivOfficialOverige
FROM [his].[matches] m
LEFT JOIN [his].[matchdetails] md ON CAST(md.InternCode AS bigint)=CAST(m.wedstrijdcode AS bigint)
LEFT JOIN [his].[teams] tt ON tt.teamcode = m.thuisteamid AND (LEFT(tt.competitienaam,6) = LEFT(md.competitietype,6) OR md.PouleCode=tt.poulecode)
WHERE tt.teamnaam IS NOT NULL 

UNION ALL

SELECT m.[wedstrijdcode]					AS WedstrijdCode
	,tu.teamnaam							AS VerenigingsTeam
	,md.Categorie							AS CompetitieCategorie
	,m.[competitienaam]						AS CompetitieNaam
	,m.[competitiesoort]					AS CompetitieSoort
	,md.[PouleCode]							AS CompetitiePouleCode
	,m.[wedstrijdnummer]					AS WedstrijdNummer
	,m.[wedstrijd]							AS Wedstrijd
	,md.[WedstrijdType]						AS WestrijdType
	,CAST(m.[datum] AS date)				AS WedstrijdDatum
	,CAST(m.[aanvangstijd] AS time)			AS WedstrijdAanvangsTijd
	,md.[Duration]							AS WestrijdDuur
	,m.[status]								AS WedstrijdStatus
	,md.[VeldNaam]							AS VeldNaam
	,md.[VeldLocatie]						AS VeldLocatie
	,m.[thuisteam]							AS TeamThuis
	,m.[thuisteamid]						AS TeamThuisId
	,m.[uitteam]							AS TeamUit
	,m.[uitteamid]							AS TeamUitId
	,m.[uitslag]							AS Uitslag
	,m.[uitslag-regulier]					AS UitslagRegulier
	,m.[uitslag-nv]							AS UitslagNv
	,m.[uitslag-s]							AS UitslagS
	,md.[ThuisScore]						AS UitslagThuisScore
	,md.[ThuisScoreRegulier]				AS UitslagThuisScoreRegulier
	,md.[ThuisScoreNV]						AS UitslagThuisScoreNv
	,md.[ThuisScoreS]						AS UitslagThuisScoreS
	,md.[UitScore]							AS UitslagUitScore
	,md.[UitScoreRegulier]					AS UitslagUitScoreRegulier
	,md.[UitScoreNV]						AS UitslagUitScoreNv
	,md.[UitScoreS]							AS UitslagUitScoreS
	,m.[verenigingswedstrijd]				AS IsVerenigingsWedstrijd
	,'Uit'								AS IsThuisUitWedstrijd
	,md.[Opmerkingen]						AS DivOpmerkingen
	,md.[VerenigingScheidsrechterCode]		AS DivOfficialScheidsrechterCode
	,md.[VerenigingScheidsrechter]			AS DivOfficialScheidsrechter
	,md.[OverigeOfficialCode]				AS DivOfficialOverigeCode
	,md.[OverigeOfficial]					AS DivOfficialOverige
  FROM [his].[matches] m
  LEFT JOIN [his].[matchdetails] md ON CAST(md.InternCode AS bigint)=CAST(m.wedstrijdcode AS bigint)
  LEFT JOIN [his].[teams] tu ON tu.teamcode = m.uitteamid   AND (LEFT(tu.competitienaam,6) = LEFT(md.competitietype,6) OR md.PouleCode=tu.poulecode)
  WHERE tu.teamnaam IS NOT NULL

  UNION ALL

  SELECT m.[wedstrijdcode]					AS WedstrijdCode
	,tu.teamnaam							AS VerenigingsTeam
	,md.Categorie							AS CompetitieCategorie
	,m.[competitienaam]						AS CompetitieNaam
	,m.[competitiesoort]					AS CompetitieSoort
	,md.[PouleCode]							AS CompetitiePouleCode
	,m.[wedstrijdnummer]					AS WedstrijdNummer
	,m.[wedstrijd]							AS Wedstrijd
	,md.[WedstrijdType]						AS WestrijdType
	,CAST(m.[datum] AS date)				AS WedstrijdDatum
	,CAST(m.[aanvangstijd] AS time)			AS WedstrijdAanvangsTijd
	,md.[Duration]							AS WestrijdDuur
	,m.[status]								AS WedstrijdStatus
	,md.[VeldNaam]							AS VeldNaam
	,md.[VeldLocatie]						AS VeldLocatie
	,m.[thuisteam]							AS TeamThuis
	,m.[thuisteamid]						AS TeamThuisId
	,m.[uitteam]							AS TeamUit
	,m.[uitteamid]							AS TeamUitId
	,m.[uitslag]							AS Uitslag
	,m.[uitslag-regulier]					AS UitslagRegulier
	,m.[uitslag-nv]							AS UitslagNv
	,m.[uitslag-s]							AS UitslagS
	,md.[ThuisScore]						AS UitslagThuisScore
	,md.[ThuisScoreRegulier]				AS UitslagThuisScoreRegulier
	,md.[ThuisScoreNV]						AS UitslagThuisScoreNv
	,md.[ThuisScoreS]						AS UitslagThuisScoreS
	,md.[UitScore]							AS UitslagUitScore
	,md.[UitScoreRegulier]					AS UitslagUitScoreRegulier
	,md.[UitScoreNV]						AS UitslagUitScoreNv
	,md.[UitScoreS]							AS UitslagUitScoreS
	,m.[verenigingswedstrijd]				AS IsVerenigingsWedstrijd
	,NULL								    AS IsThuisUitWedstrijd
	,md.[Opmerkingen]						AS DivOpmerkingen
	,md.[VerenigingScheidsrechterCode]		AS DivOfficialScheidsrechterCode
	,md.[VerenigingScheidsrechter]			AS DivOfficialScheidsrechter
	,md.[OverigeOfficialCode]				AS DivOfficialOverigeCode
	,md.[OverigeOfficial]					AS DivOfficialOverige
  FROM [his].[matches] m
  LEFT JOIN [his].[matchdetails] md ON CAST(md.InternCode AS bigint)=CAST(m.wedstrijdcode AS bigint)
  LEFT JOIN [his].[teams] tt ON tt.teamcode = m.thuisteamid AND (LEFT(tt.competitienaam,6) = LEFT(md.competitietype,6) OR md.PouleCode=tt.poulecode)
  LEFT JOIN [his].[teams] tu ON tu.teamcode = m.uitteamid   AND (LEFT(tu.competitienaam,6) = LEFT(md.competitietype,6) OR md.PouleCode=tu.poulecode)
  WHERE tt.teamnaam IS NULL 
	AND tu.teamnaam IS NULL
GO