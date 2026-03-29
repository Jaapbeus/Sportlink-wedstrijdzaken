CREATE TABLE [mta].[source_target_mapping](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[source_type] [int] NULL,
	[source_root] [nvarchar](250) NULL,
	[source_schema] [nvarchar](10) NULL,
	[source_entity] [nvarchar](250) NULL,
	[source_pk] [nvarchar](250) NULL,
	[target_type] [int] NULL,
	[target_root] [nvarchar](250) NULL,
	[target_schema] [nvarchar](10) NULL,
	[target_entity] [nvarchar](250) NULL,
	[target_pk] [nvarchar](250) NULL);

/*
INSERT INTO mta.source_target_mapping ([source_type],[source_root],[source_schema],[source_entity],[source_pk],[target_type],[target_root],[target_schema],[target_entity],[target_pk])
VALUES
(1,'https://data.sportlink.com',NULL,'teams',NULL,0,'SportlinkSqlDb','stg','teams',NULL),
(0,'SportlinkSqlDb','stg','teams','[teamcode],[lokaleteamcode],[poulecode]',0,'SportlinkSqlDb','his','teams','bk_teams NVARCHAR(100)'),
(1,'https://data.sportlink.com',NULL,'uitslagen',NULL,0,'SportlinkSqlDb','stg','matches',NULL),
(0,'SportlinkSqlDb','stg','matches','[wedstrijdcode]',0,'SportlinkSqlDb','his','matches','bk_matches NVARCHAR(100)'),
(1,'https://data.sportlink.com',NULL,'wedstrijd-informatie',NULL,0,'SportlinkSqlDb','stg','matchdetails',NULL),
(0,'SportlinkSqlDb','stg','matchdetails','[WedstrijdCode]',0,'SportlinkSqlDb','his','matchdetails','bk_WedstrijdCode INT')
;
*/