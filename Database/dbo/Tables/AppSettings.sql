CREATE TABLE [dbo].[AppSettings](
	[ClubName]			NVARCHAR(100)	NOT NULL,
	[SportlinkApiUrl]	NVARCHAR(100)	NOT NULL,
	[SportlinkClientId]	NVARCHAR(50)	NOT NULL,
	[SeasonStartMonth]	[int]			NOT NULL,
	[LastSyncTimestamp]	DATETIME2		NULL
	)