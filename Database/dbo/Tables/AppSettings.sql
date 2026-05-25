CREATE TABLE [dbo].[AppSettings](
	[ClubName]				NVARCHAR(100)	NOT NULL,
	[ClubCode]				NVARCHAR(20)	NOT NULL,
	[SportlinkApiUrl]		NVARCHAR(100)	NOT NULL,
	[SportlinkClientId]		NVARCHAR(50)	NOT NULL,
	[SeasonStartMonth]		[int]			NOT NULL,
	[Accommodatie]			NVARCHAR(200)	NULL,
	[LastSyncTimestamp]		DATETIME2		NULL,
	[FetchSchedule]			NVARCHAR(50)	NOT NULL DEFAULT '0 0 4 * * *',
	[PlannerAfzenderNaam]	NVARCHAR(100)	NULL,
	[CoordinatorNaam]		NVARCHAR(100)	NULL,
	[CoordinatorFunctie]	NVARCHAR(100)	NULL,
	[PlannerEmailAdres]		NVARCHAR(200)	NULL,
	[HerplanDeadlineDagen]	INT				NULL,	-- default 8: herplanverzoek mag niet eerder dan X dagen voor wedstrijd
	[BufferMinuten]			INT				NULL,	-- default 15: buffer tussen wedstrijden op hetzelfde veld
	[EmailVoetnoot]			NVARCHAR(MAX)	NULL,	-- vrij te bewerken voettekst die onder alle uitgaande e-mails wordt geplaatst
	[AccommodatiePlaats]	NVARCHAR(100)	NULL,	-- plaatsnaam voor geocoding en zonsondergangsberekening
	[AccommodatieLatitude]	FLOAT			NULL,	-- breedtegraad WGS84 (decimaal)
	[AccommodatieLongitude]	FLOAT			NULL,	-- lengtegraad WGS84 (decimaal)
	[UseRealtimeApi]		BIT				NOT NULL DEFAULT 1,		-- 1=real-time Sportlink API raadplegen bij planner-checks, 0=alleen DB
	[ThemeColorPrimary]		NVARCHAR(7)		NULL,		-- hex kleur #rrggbb
	[ThemeColorSecondary]	NVARCHAR(7)		NULL,
	[ThemeColorAccent]		NVARCHAR(7)		NULL,
	[ThemeColorTextOnPrimary] NVARCHAR(7)	NULL,
	[ThemeClubWebsiteUrl]	NVARCHAR(300)	NULL		-- URL van club-website voor kleurextractie
	)