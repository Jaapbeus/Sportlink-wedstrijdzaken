CREATE TABLE [dbo].[AppSettings](
	[ClubName]				NVARCHAR(100)	NOT NULL,
	-- NOT NULL alleen is niet genoeg: een lege ClubCode komt als "" in de settings-cache terecht en
	-- maakt elke ClubCode-filter leeg (o.a. de uitsluitingslijst van de e-mailverwerking). (#707)
	[ClubCode]				NVARCHAR(20)	NOT NULL CONSTRAINT [CK_AppSettings_ClubCode] CHECK (LEN(LTRIM(RTRIM([ClubCode]))) > 0),
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
	[ThemeClubWebsiteUrl]	NVARCHAR(300)	NULL,		-- URL van club-website voor kleurextractie
	[SyncEnabled]			BIT				NOT NULL DEFAULT 1,	-- 0 = geen Sportlink API-sync voor deze club
	-- #561: verzet-zonder-datum flow — KNVB-speeldagenkalender als bijlage + BCC eigen team
	[KnvbPdfBijlageIngeschakeld] BIT			NOT NULL DEFAULT 1,	-- 1 = KNVB-kalender-PDF bijvoegen bij verzet-zonder-datum-antwoord
	[KnvbStandaardRegio]	NVARCHAR(20)	NULL,	-- KNVB-regio van deze club: West/Noord/Oost/Zuid/Landelijk/LandelijkJeugd. Geen default — ontbrekend = geen bijlage/vrije-zaterdagen-tekst
	-- #781 (AVG art. 5 lid 1 sub e): bewaartermijn voor dbo.AppSettingsAudit in dagen. Een numerieke
	-- default is geen club-specifieke string (die regel geldt voor namen/domeinen/URLs), dus 730
	-- dagen (24 maanden) als default is toegestaan. Dit is een gedocumenteerd UITGANGSPUNT, geen
	-- definitief beleid — zie de toelichting in sp_CleanupAppSettingsAudit.sql. Beheerder kan de
	-- waarde aanpassen via een directe UPDATE op deze tabel (nog geen GUI-veld).
	[AppSettingsAuditBewaarDagen] INT NOT NULL DEFAULT 730,
	-- #988: schakelaar voor de Sportlink Web Extension (epic #986) — standaard UIT, club kiest zelf
	[SportlinkExtensionEnabled] BIT NOT NULL DEFAULT 0
	)