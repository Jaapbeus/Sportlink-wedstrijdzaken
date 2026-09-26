-- #1266 — SQL Server-tegenhanger van public.sportlinkpublicmatchidcache (Postgres-migratie 014,
-- index uit 018). Beide tiers zijn gelijkwaardig; deze tabel ontbrak omdat epic #986 na de
-- basisimplementatie alleen op de Postgres-tier is doorontwikkeld.
--
-- Doel: het resultaat van de reverse-lookup (#987/#1016) bewaren. Sportlinks MatchProgramOverview
-- matcht op ExternalMatchId (ons eigen wedstrijdnummer) en die aanroep is traag (12+ s) en niet
-- club-gescoped. Zonder cache zou elke wedstrijdactie die lookup opnieuw doen.
--
-- UTC: OpgehaaldOp gebruikt GETUTCDATE(), nooit GETDATE() — zie de UTC-regel in CLAUDE.md (#246).
CREATE TABLE [dbo].[SportlinkPublicMatchIdCache] (
	[Wedstrijdcode]	BIGINT			NOT NULL,
	[ClubCode]		NVARCHAR(20)	NOT NULL,
	[PublicMatchId]	NVARCHAR(50)	NOT NULL,
	[OpgehaaldOp]	DATETIME2		NOT NULL DEFAULT GETUTCDATE(),
	CONSTRAINT [PK_SportlinkPublicMatchIdCache] PRIMARY KEY CLUSTERED ([Wedstrijdcode] ASC, [ClubCode] ASC)
	)
GO

-- Twee query's filteren op ClubCode ZONDER Wedstrijdcode en kunnen de primaire sleutel dus niet
-- gebruiken (zie Postgres-migratie 018, bevinding A1 van de review in #1122):
--   - de contract-check-timer en het health-endpoint: WHERE ClubCode = @ClubCode ORDER BY OpgehaaldOp DESC
--   - de reverse-lookup op publieke wedstrijd-ID's: WHERE ClubCode = @ClubCode AND PublicMatchId IN (...)
CREATE NONCLUSTERED INDEX [IX_SportlinkPublicMatchIdCache_ClubCode_OpgehaaldOp]
	ON [dbo].[SportlinkPublicMatchIdCache] ([ClubCode] ASC, [OpgehaaldOp] DESC)
GO

CREATE NONCLUSTERED INDEX [IX_SportlinkPublicMatchIdCache_ClubCode_PublicMatchId]
	ON [dbo].[SportlinkPublicMatchIdCache] ([ClubCode] ASC, [PublicMatchId] ASC)
GO
