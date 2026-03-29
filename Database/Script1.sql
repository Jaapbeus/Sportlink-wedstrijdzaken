USE [SportlinkSqlDb]
GO
TRUNCATE TABLE [dbo].[Seizoen];
INSERT INTO [dbo].[Seizoen]
           ([Name]
           ,[DateFrom]
           ,[DateUntil])
     VALUES
           ('2024-2025',DATEFROMPARTS(2024,7,1),DATEFROMPARTS(2025,6,30)),
           ('2025-2026',DATEFROMPARTS(2025,7,1),DATEFROMPARTS(2026,6,30)),
		   ('2026-2027',DATEFROMPARTS(2026,7,1),DATEFROMPARTS(2027,6,30)),
		   ('2027-2028',DATEFROMPARTS(2027,7,1),DATEFROMPARTS(2028,6,30)),
		   ('2028-2029',DATEFROMPARTS(2028,7,1),DATEFROMPARTS(2029,6,30))
GO

-- NOG VARIABLE TE MAKEN

/****** Object:  Table [dbo].[Seizoen]    Script Date: 18-1-2025 14:06:20 ******/
IF  EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Seizoen]') AND type in (N'U'))
DROP TABLE [dbo].[Seizoen]
GO

/****** Object:  Table [dbo].[Seizoen]    Script Date: 18-1-2025 14:06:20 ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[Seizoen](
	[SeizoenId] [int] identity (1,1) NOT NULL,
	[Name] [nchar](9) NULL,
	[DateFrom] [date] NULL,
	[DateUntil] [date] NULL
) ON [PRIMARY]
GO



-- VIEWS


SELECT m.[bk_matches]
      ,m.[wedstrijddatum]
      ,m.[wedstrijdcode]
      ,m.[wedstrijdnummer]
      ,CAST(m.[datum] as date) as datum
      ,m.[wedstrijd]
      --,[datumopgemaakt]
      --,[accommodatie]
      --,[aanvangstijd]
      ,m.[thuisteam]
      ,m.[thuisteamid]
      --,m.[thuisteamlogo]
      --,m.[thuisteamclubrelatiecode]
      --,m.[uitteamclubrelatiecode]
      ,m.[uitteam]
      ,m.[uitteamid]
      --,[uitteamlogo]
      ,m.[uitslag]
      --,[uitslag-regulier]
      --,[uitslag-nv]
      --,[uitslag-s]
      ,m.[competitienaam]
      ,m.[competitiesoort]
      --,[eigenteam]
      --,m.[sportomschrijving]
      --,m.[verenigingswedstrijd]
      --,m.[status]
      --,m.[meer]
      --,m.[mta_inserted]
      --,m.[mta_modified]
      --,m.[mta_deleted]
	  ,tt.teamnaam as tt_teamnaam
	  ,tu.teamnaam as tu_teamnaam
--	  ,m.*
  FROM [his].[matches] m
  -- LEFT JOIN because name in teams table inconsistent "competitienaam='0295 Mannen 35+ Vrijdag Toernooivorm 7x7 (najaar)'" and "'0295 Mannen 35+ Vrijdag 7x7 (najaar)'"
  LEFT JOIN his.teams tt ON tt.teamcode=m.thuisteamid AND m.competitiesoort = tt.competitiesoort AND LEFT(tt.competitienaam, 5)=LEFT(m.competitienaam,5)
  LEFT JOIN his.teams tu ON tu.teamcode=m.uitteamid   AND m.competitiesoort = tu.competitiesoort AND LEFT(tu.competitienaam, 5)=LEFT(m.competitienaam,5)

  WHERE m.competitiesoort='regulier' AND 
  (tt.teamnaam IS NULL AND tu.teamnaam is null)


  SELECT * FROM his.teams WHERE teamcode=347069 
  




-- IMPORTED FROM https://www.knvb.nl/downloads/sites/bestand/knvb/28353/speeldagenkalender-veld-west-2024-2025 PDF File with a little AI help 

INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)
VALUES 
('2024-08-24', '2024-08-25', 'Schema 14', 'Categorie A', 'Senioren', 'Vrij'),
('2024-08-31', '2024-09-01', 'Schema 14', 'Categorie A', 'Senioren', 'Beker poule'),
('2024-09-07', '2024-09-08', 'Schema 14', 'Categorie A', 'Senioren', 'Beker poule'),
('2024-09-14', '2024-09-15', 'Schema 14', 'Categorie A', 'Senioren', 'Beker poule'),
('2024-09-21', '2024-09-22', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-09-28', '2024-09-29', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-10-05', '2024-10-06', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-10-12', '2024-10-13', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-10-19', '2024-10-20', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-10-26', '2024-10-27', 'Schema 14', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2024-11-02', '2024-11-03', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-11-09', '2024-11-10', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-11-16', '2024-11-17', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-11-23', '2024-11-24', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-11-30', '2024-12-01', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-12-07', '2024-12-08', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2024-12-14', '2024-12-15', 'Schema 14', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2025-01-11', '2025-01-12', 'Schema 14', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2025-01-18', '2025-01-19', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-01-25', '2025-01-26', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-02-01', '2025-02-02', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-02-08', '2025-02-09', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-02-15', '2025-02-16', 'Schema 14', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2025-02-22', '2025-02-23', 'Schema 14', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2025-03-01', '2025-03-02', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-03-08', '2025-03-09', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-03-15', '2025-03-16', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-03-22', '2025-03-23', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-03-29', '2025-03-30', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-04-05', '2025-04-06', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-04-12', '2025-04-13', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-04-19', '2025-04-20', 'Schema 14', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2025-04-26', '2025-04-27', 'Schema 14', 'Categorie A', 'Senioren', 'Vrij'),
('2025-05-03', '2025-05-04', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-05-10', '2025-05-11', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-05-17', '2025-05-18', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-05-24', '2025-05-25', 'Schema 14', 'Categorie A', 'Senioren', 'WD'),
('2025-05-31', '2025-06-01', 'Schema 14', 'Categorie A', 'Senioren', 'NC'),
('2025-06-07', '2025-06-08', 'Schema 14', 'Categorie A', 'Senioren', 'NC'),
('2025-06-14', '2025-06-15', 'Schema 14', 'Categorie A', 'Senioren', 'NC');


-- "Now for Schema 12, categorie A and Senioren the INSERT with columnnames INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)"

INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)
VALUES 
('2024-08-24', '2024-08-25', 'Schema 12', 'Categorie A', 'Senioren', 'Vrij'),
('2024-08-31', '2024-09-01', 'Schema 12', 'Categorie A', 'Senioren', 'Beker poule'),
('2024-09-07', '2024-09-08', 'Schema 12', 'Categorie A', 'Senioren', 'Beker poule'),
('2024-09-14', '2024-09-15', 'Schema 12', 'Categorie A', 'Senioren', 'Beker poule'),
('2024-09-21', '2024-09-22', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-09-28', '2024-09-29', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-10-05', '2024-10-06', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-10-12', '2024-10-13', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-10-19', '2024-10-20', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-10-26', '2024-10-27', 'Schema 12', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2024-11-02', '2024-11-03', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-11-09', '2024-11-10', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-11-16', '2024-11-17', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-11-23', '2024-11-24', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-11-30', '2024-12-01', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-12-07', '2024-12-08', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2024-12-14', '2024-12-15', 'Schema 12', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2025-01-11', '2025-01-12', 'Schema 12', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2025-01-18', '2025-01-19', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-01-25', '2025-01-26', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-02-01', '2025-02-02', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-02-08', '2025-02-09', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-02-15', '2025-02-16', 'Schema 12', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2025-02-22', '2025-02-23', 'Schema 12', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2025-03-01', '2025-03-02', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-03-08', '2025-03-09', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-03-15', '2025-03-16', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-03-22', '2025-03-23', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-03-29', '2025-03-30', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-04-05', '2025-04-06', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-04-12', '2025-04-13', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-04-19', '2025-04-20', 'Schema 12', 'Categorie A', 'Senioren', 'Inh. / Bek.'),
('2025-04-26', '2025-04-27', 'Schema 12', 'Categorie A', 'Senioren', 'Vrij'),
('2025-05-03', '2025-05-04', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-05-10', '2025-05-11', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-05-17', '2025-05-18', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-05-24', '2025-05-25', 'Schema 12', 'Categorie A', 'Senioren', 'WD'),
('2025-05-31', '2025-06-01', 'Schema 12', 'Categorie A', 'Senioren', 'NC'),
('2025-06-07', '2025-06-08', 'Schema 12', 'Categorie A', 'Senioren', 'NC'),
('2025-06-14', '2025-06-15', 'Schema 12', 'Categorie A', 'Senioren', 'NC');

INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)
VALUES 
('2024-08-24', '2024-08-25', 'Schema 12', 'Categorie B', 'Senioren', 'Vrij'),
('2024-08-31', '2024-09-01', 'Schema 12', 'Categorie B', 'Senioren', 'Beker poule'),
('2024-09-07', '2024-09-08', 'Schema 12', 'Categorie B', 'Senioren', 'Beker poule'),
('2024-09-14', '2024-09-15', 'Schema 12', 'Categorie B', 'Senioren', 'Beker poule'),
('2024-09-21', '2024-09-22', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2024-09-28', '2024-09-29', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2024-10-05', '2024-10-06', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2024-10-12', '2024-10-13', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2024-10-19', '2024-10-20', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2024-10-26', '2024-10-27', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2024-11-02', '2024-11-03', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2024-11-09', '2024-11-10', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2024-11-16', '2024-11-17', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2024-11-23', '2024-11-24', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2024-11-30', '2024-12-01', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2024-12-07', '2024-12-08', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2024-12-14', '2024-12-15', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2025-01-11', '2025-01-12', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2025-01-18', '2025-01-19', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2025-01-25', '2025-01-26', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-02-01', '2025-02-02', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-02-08', '2025-02-09', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-02-15', '2025-02-16', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2025-02-22', '2025-02-23', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2025-03-01', '2025-03-02', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-03-08', '2025-03-09', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-03-15', '2025-03-16', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-03-22', '2025-03-23', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-03-29', '2025-03-30', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2025-04-05', '2025-04-06', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-04-12', '2025-04-13', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-04-19', '2025-04-20', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2025-04-26', '2025-04-27', 'Schema 12', 'Categorie B', 'Senioren', 'Vrij'),
('2025-05-03', '2025-05-04', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-05-10', '2025-05-11', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-05-17', '2025-05-18', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-05-24', '2025-05-25', 'Schema 12', 'Categorie B', 'Senioren', 'WD'),
('2025-05-31', '2025-06-01', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2025-06-07', '2025-06-08', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.'),
('2025-06-14', '2025-06-15', 'Schema 12', 'Categorie B', 'Senioren', 'Inh. / Bek.');

INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)
VALUES 
('2024-08-24', '2024-08-25', 'Senioren', 'Reeksen Cat B', 'O23', 'Vrij'),
('2024-08-31', '2024-09-01', 'Senioren', 'Reeksen Cat B', 'O23', 'Beker poule'),
('2024-09-07', '2024-09-08', 'Senioren', 'Reeksen Cat B', 'O23', 'Beker poule'),
('2024-09-14', '2024-09-15', 'Senioren', 'Reeksen Cat B', 'O23', 'Beker poule'),
('2024-09-21', '2024-09-22', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-09-28', '2024-09-29', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-10-05', '2024-10-06', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-10-12', '2024-10-13', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-10-19', '2024-10-20', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-10-26', '2024-10-27', 'Senioren', 'Reeksen Cat B', 'O23', 'Inh. / Bek.'),
('2024-11-02', '2024-11-03', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-11-09', '2024-11-10', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-11-16', '2024-11-17', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-11-23', '2024-11-24', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-11-30', '2024-12-01', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-12-07', '2024-12-08', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2024-12-14', '2024-12-15', 'Senioren', 'Reeksen Cat B', 'O23', 'Inh. / Bek.'),
('2025-01-11', '2025-01-12', 'Senioren', 'Reeksen Cat B', 'O23', 'Inh. / Bek.'),
('2025-01-18', '2025-01-19', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-01-25', '2025-01-26', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-02-01', '2025-02-02', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-02-08', '2025-02-09', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-02-15', '2025-02-16', 'Senioren', 'Reeksen Cat B', 'O23', 'Inh. / Bek.'),
('2025-02-22', '2025-02-23', 'Senioren', 'Reeksen Cat B', 'O23', 'Inh. / Bek.'),
('2025-03-01', '2025-03-02', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-03-08', '2025-03-09', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-03-15', '2025-03-16', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-03-22', '2025-03-23', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-03-29', '2025-03-30', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-04-05', '2025-04-06', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-04-12', '2025-04-13', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-04-19', '2025-04-20', 'Senioren', 'Reeksen Cat B', 'O23', 'Inh. / Bek.'),
('2025-04-26', '2025-04-27', 'Senioren', 'Reeksen Cat B', 'O23', 'Vrij'),
('2025-05-03', '2025-05-04', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-05-10', '2025-05-11', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-05-17', '2025-05-18', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-05-24', '2025-05-25', 'Senioren', 'Reeksen Cat B', 'O23', 'WD NJ'),
('2025-05-31', '2025-06-01', 'Senioren', 'Reeksen Cat B', 'O23', 'Inh. / Bek.'),
('2025-06-07', '2025-06-08', 'Senioren', 'Reeksen Cat B', 'O23', 'Inh. / Bek.'),
('2025-06-14', '2025-06-15', 'Senioren', 'Reeksen Cat B', 'O23', 'Inh. / Bek.');

INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)
VALUES 
('2024-08-24', '2024-08-25', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Vrij'),
('2024-08-31', '2024-09-01', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Beker poule'),
('2024-09-07', '2024-09-08', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Beker poule'),
('2024-09-14', '2024-09-15', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Beker poule'),
('2024-09-21', '2024-09-22', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-09-28', '2024-09-29', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-10-05', '2024-10-06', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-10-12', '2024-10-13', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-10-19', '2024-10-20', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-10-26', '2024-10-27', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Inh. / Bek.'),
('2024-11-02', '2024-11-03', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-11-09', '2024-11-10', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-11-16', '2024-11-17', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-11-23', '2024-11-24', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-11-30', '2024-12-01', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-12-07', '2024-12-08', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2024-12-14', '2024-12-15', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Inh. / Bek.'),
('2025-01-11', '2025-01-12', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Inh. / Bek.'),
('2025-01-18', '2025-01-19', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-01-25', '2025-01-26', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-02-01', '2025-02-02', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-02-08', '2025-02-09', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-02-15', '2025-02-16', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Inh. / Bek.'),
('2025-02-22', '2025-02-23', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Inh. / Bek.'),
('2025-03-01', '2025-03-02', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-03-08', '2025-03-09', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-03-15', '2025-03-16', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-03-22', '2025-03-23', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-03-29', '2025-03-30', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-04-05', '2025-04-06', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-04-12', '2025-04-13', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-04-19', '2025-04-20', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Inh. / Bek.'),
('2025-04-26', '2025-04-27', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Vrij'),
('2025-05-03', '2025-05-04', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-05-10', '2025-05-11', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-05-17', '2025-05-18', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-05-24', '2025-05-25', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'WD NJ'),
('2025-05-31', '2025-06-01', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Inh. / Bek.'),
('2025-06-07', '2025-06-08', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Inh. / Bek.'),
('2025-06-14', '2025-06-15', 'Schema 12', 'Divisies Cat. A NJ en VJ-reeks', 'O13-O19', 'Inh. / Bek.');

INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)
VALUES 
('2024-08-24', '2024-08-25', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Beker'),
('2024-08-31', '2024-09-01', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-09-07', '2024-09-08', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-09-14', '2024-09-15', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-09-21', '2024-09-22', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-09-28', '2024-09-29', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-10-05', '2024-10-06', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-10-12', '2024-10-13', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-10-19', '2024-10-20', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-10-26', '2024-10-27', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Inh. / Bek.'),
('2024-11-02', '2024-11-03', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-11-09', '2024-11-10', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-11-16', '2024-11-17', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-11-23', '2024-11-24', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-11-30', '2024-12-01', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-12-07', '2024-12-08', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2024-12-14', '2024-12-15', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Inh. / Bek.'),
('2025-01-11', '2025-01-12', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Inh. / Bek.'),
('2025-01-18', '2025-01-19', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-01-25', '2025-01-26', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-02-01', '2025-02-02', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-02-08', '2025-02-09', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-02-15', '2025-02-16', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Inh. / Bek.'),
('2025-02-22', '2025-02-23', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Inh. / Bek.'),
('2025-03-01', '2025-03-02', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-03-08', '2025-03-09', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-03-15', '2025-03-16', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-03-22', '2025-03-23', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-03-29', '2025-03-30', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-04-05', '2025-04-06', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-04-12', '2025-04-13', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-04-19', '2025-04-20', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Inh. / Bek.'),
('2025-04-26', '2025-04-27', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Vrij'),
('2025-05-03', '2025-05-04', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-05-10', '2025-05-11', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-05-17', '2025-05-18', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-05-24', '2025-05-25', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'WD NJ'),
('2025-05-31', '2025-06-01', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Inh. / Bek.'),
('2025-06-07', '2025-06-08', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Inh. / Bek.'),
('2025-06-14', '2025-06-15', 'Schema 8', 'Senioren Reeksen Categorie B', 'O23', 'Inh. / Bek.');

INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)
VALUES 
('2024-08-24', '2024-08-25', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Vrij'),
('2024-08-31', '2024-09-01', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Vrij'),
('2024-09-07', '2024-09-08', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 1'),
('2024-09-14', '2024-09-15', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 1'),
('2024-09-21', '2024-09-22', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 1'),
('2024-09-28', '2024-09-29', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 1'),
('2024-10-05', '2024-10-06', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 1'),
('2024-10-12', '2024-10-13', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 1'),
('2024-10-19', '2024-10-20', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 1'),
('2024-10-26', '2024-10-27', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Inhaal'),
('2024-11-02', '2024-11-03', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Vrij'),
('2024-11-09', '2024-11-10', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 2'),
('2024-11-16', '2024-11-17', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 2'),
('2024-11-23', '2024-11-24', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 2'),
('2024-11-30', '2024-12-01', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 2'),
('2024-12-07', '2024-12-08', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 2'),
('2024-12-14', '2024-12-15', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Inhaal'),
('2025-01-11', '2025-01-12', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Vrij'),
('2025-01-18', '2025-01-19', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Vrij'),
('2025-01-25', '2025-01-26', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Beker'),
('2025-02-01', '2025-02-02', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-02-08', '2025-02-09', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-02-15', '2025-02-16', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-02-22', '2025-02-23', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Inhaal'),
('2025-03-01', '2025-03-02', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-03-08', '2025-03-09', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-03-15', '2025-03-16', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-03-22', '2025-03-23', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-03-29', '2025-03-30', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-04-05', '2025-04-06', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-04-12', '2025-04-13', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-04-19', '2025-04-20', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Inhaal'),
('2025-04-26', '2025-04-27', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Vrij'),
('2025-05-03', '2025-05-04', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-05-10', '2025-05-11', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-05-17', '2025-05-18', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-05-24', '2025-05-25', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Fase 3'),
('2025-05-31', '2025-06-01', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Inhaal'),
('2025-06-07', '2025-06-08', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Inhaal'),
('2025-06-14', '2025-06-15', NULL, 'Meiden 3+1 fasen', 'MO13 t/m MO20', 'Inhaal');

INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)
VALUES 
('2024-08-24', '2024-08-25', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Vrij'),
('2024-08-31', '2024-09-01', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Vrij'),
('2024-09-07', '2024-09-08', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 1'),
('2024-09-14', '2024-09-15', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 1'),
('2024-09-21', '2024-09-22', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 1'),
('2024-09-28', '2024-09-29', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 1'),
('2024-10-05', '2024-10-06', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 1'),
('2024-10-12', '2024-10-13', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 1'),
('2024-10-19', '2024-10-20', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 1'),
('2024-10-26', '2024-10-27', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Inhaal'),
('2024-11-02', '2024-11-03', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Vrij'),
('2024-11-09', '2024-11-10', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 2'),
('2024-11-16', '2024-11-17', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 2'),
('2024-11-23', '2024-11-24', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 2'),
('2024-11-30', '2024-12-01', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 2'),
('2024-12-07', '2024-12-08', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 2'),
('2024-12-14', '2024-12-15', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Inhaal'),
('2025-01-11', '2025-01-12', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Vrij'),
('2025-01-18', '2025-01-19', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Vrij'),
('2025-01-25', '2025-01-26', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Beker'),
('2025-02-01', '2025-02-02', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-02-08', '2025-02-09', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-02-15', '2025-02-16', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-02-22', '2025-02-23', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Inhaal'),
('2025-03-01', '2025-03-02', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-03-08', '2025-03-09', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-03-15', '2025-03-16', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-03-22', '2025-03-23', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-03-29', '2025-03-30', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-04-05', '2025-04-06', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-04-12', '2025-04-13', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-04-19', '2025-04-20', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Inhaal'),
('2025-04-26', '2025-04-27', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Vrij'),
('2025-05-03', '2025-05-04', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-05-10', '2025-05-11', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-05-17', '2025-05-18', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-05-24', '2025-05-25', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Fase 3'),
('2025-05-31', '2025-06-01', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Inhaal'),
('2025-06-07', '2025-06-08', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Inhaal'),
('2025-06-14', '2025-06-15', 'Districtscompetities', 'Junioren cat. A+B 3 fasen', 'O13-O19', 'Inhaal');

INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)
VALUES 
('2024-08-24', '2024-08-25', NULL, 'Pupillen 4 fasen', 'O7-O12', ''),
('2024-08-31', '2024-09-01', NULL, 'Pupillen 4 fasen', 'O7-O12', ''),
('2024-09-07', '2024-09-08', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 1'),
('2024-09-14', '2024-09-15', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 1'),
('2024-09-21', '2024-09-22', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 1'),
('2024-09-28', '2024-09-29', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 1'),
('2024-10-05', '2024-10-06', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 1'),
('2024-10-12', '2024-10-13', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 1'),
('2024-10-19', '2024-10-20', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 1'),
('2024-10-26', '2024-10-27', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Vrij'),
('2024-11-02', '2024-11-03', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Vrij'),
('2024-11-09', '2024-11-10', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 2'),
('2024-11-16', '2024-11-17', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 2'),
('2024-11-23', '2024-11-24', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 2'),
('2024-11-30', '2024-12-01', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 2'),
('2024-12-07', '2024-12-08', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 2'),
('2024-12-14', '2024-12-15', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Inhaal'),
('2025-01-11', '2025-01-12', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Vrij'),
('2025-01-18', '2025-01-19', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Vrij'),
('2025-01-25', '2025-01-26', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 3'),
('2025-02-01', '2025-02-02', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 3'),
('2025-02-08', '2025-02-09', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 3'),
('2025-02-15', '2025-02-16', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Inhaal'),
('2025-02-22', '2025-02-23', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Vrij'),
('2025-03-01', '2025-03-02', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 3'),
('2025-03-08', '2025-03-09', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 3'),
('2025-03-15', '2025-03-16', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 3'),
('2025-03-22', '2025-03-23', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 3'),
('2025-03-29', '2025-03-30', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 4'),
('2025-04-05', '2025-04-06', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 4'),
('2025-04-12', '2025-04-13', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 4'),
('2025-04-19', '2025-04-20', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Vrij'),
('2025-04-26', '2025-04-27', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Vrij'),
('2025-05-03', '2025-05-04', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Vrij'),
('2025-05-10', '2025-05-11', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 4'),
('2025-05-17', '2025-05-18', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 4'),
('2025-05-24', '2025-05-25', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Fase 4'),
('2025-05-31', '2025-06-01', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Inhaal'),
('2025-06-07', '2025-06-08', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Inhaal'),
('2025-06-14', '2025-06-15', NULL, 'Pupillen 4 fasen', 'O7-O12', 'Inhaal');

INSERT INTO dbo.KNVB_speeldagenkalender (DateFrom, DateUntil, SchemaName, CategorieName, LeeftijdsCategorieName, SpeeldagType)
VALUES 
('2024-09-21', '2024-09-22', 'Toernooi', '7x7 vrijdag', NULL, 'Fase 1 R1:20-09'),
('2024-10-05', '2024-10-06', 'Toernooi', '7x7 vrijdag', NULL, 'Fase 1 R2: 04-10'),
('2024-11-09', '2024-11-10', 'Toernooi', '7x7 vrijdag', NULL, 'Fase 2 R3: 08-11'),
('2024-11-23', '2024-11-24', 'Toernooi', '7x7 vrijdag', NULL, 'Fase 2 R4: 22-11'),
('2024-12-07', '2024-12-08', 'Toernooi', '7x7 vrijdag', NULL, 'Fase 2 R5: 06-12'),
('2025-03-08', '2025-03-09', 'Toernooi', '7x7 vrijdag', NULL, 'Fase 3 R1:07-03'),
('2025-03-22', '2025-03-23', 'Toernooi', '7x7 vrijdag', NULL, 'Fase 3 R2: 21-03'),
('2025-04-05', '2025-04-06', 'Toernooi', '7x7 vrijdag', NULL, 'Fase 4 R3: 04-04'),
('2025-05-10', '2025-05-11', 'Toernooi', '7x7 vrijdag', NULL, 'Fase 4 R4: 09-05'),
('2025-05-24', '2025-05-25', 'Toernooi', '7x7 vrijdag', NULL, 'Fase 4 R5: 23-05');



