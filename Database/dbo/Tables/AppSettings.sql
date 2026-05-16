CREATE TABLE [dbo].[AppSettings](
	[ClubName]				NVARCHAR(100)	NOT NULL,
	[ClubCode]				NVARCHAR(20)	NOT NULL CONSTRAINT [DF_AppSettings_ClubCode] DEFAULT 'VRC',
	[SportlinkApiUrl]		NVARCHAR(100)	NOT NULL,
	[SportlinkClientId]		NVARCHAR(50)	NOT NULL,
	[SeasonStartMonth]		[int]			NOT NULL,
	[Accommodatie]			NVARCHAR(200)	NULL CONSTRAINT [DF_AppSettings_Accommodatie] DEFAULT 'Sportpark Spitsbergen',
	[LastSyncTimestamp]		DATETIME2		NULL,
	[FetchSchedule]			NVARCHAR(50)	NOT NULL DEFAULT '0 0 4 * * *',
	[PlannerAfzenderNaam]	NVARCHAR(100)	NULL,
	[CoordinatorNaam]		NVARCHAR(100)	NULL,
	[CoordinatorFunctie]	NVARCHAR(100)	NULL,
	[PlannerEmailAdres]		NVARCHAR(200)	NULL,
	[InternDomein]			NVARCHAR(100)	NULL,	-- bijv. '[club-domein]' — emails van dit domein overslaan
	[HerplanDeadlineDagen]	INT				NULL,	-- default 8: herplanverzoek mag niet eerder dan X dagen voor wedstrijd
	[BufferMinuten]			INT				NULL	-- default 15: buffer tussen wedstrijden op hetzelfde veld
	)