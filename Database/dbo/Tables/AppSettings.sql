CREATE TABLE [dbo].[AppSettings](
	[ClubName]				NVARCHAR(100)	NOT NULL,
	[SportlinkApiUrl]		NVARCHAR(100)	NOT NULL,
	[SportlinkClientId]		NVARCHAR(50)	NOT NULL,
	[SeasonStartMonth]		[int]			NOT NULL,
	[LastSyncTimestamp]		DATETIME2		NULL,
	[FetchSchedule]			NVARCHAR(50)	NOT NULL DEFAULT '0 0 4 * * *',
	[PlannerAfzenderNaam]	NVARCHAR(100)	NULL,
	[CoordinatorNaam]		NVARCHAR(100)	NULL,
	[CoordinatorFunctie]	NVARCHAR(100)	NULL,
	[PlannerEmailAdres]		NVARCHAR(200)	NULL
	)