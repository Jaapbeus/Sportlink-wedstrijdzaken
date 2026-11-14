-- #1915 — SQL Server-tegenhanger van public.sportlinkveldplannerblok (Postgres-migratie 050).
--
-- Een afgeleide kopie van wat de Sportlink-veldplanner nu toont: één rij per wedstrijdblok, met PublicMatchId als
-- sleutel per club. Bij elke planningsaanvraag wordt per sleutel de RijHash vergeleken en alleen het verschil geschreven;
-- Sportlink blijft leidend. ExternalMatchId is het bondswedstrijdnummer (his.matches.wedstrijdnummer): het wordt eenmalig
-- per sleutel bij Sportlink opgevraagd en is de brug naar onze eigen wedstrijden.
--
-- UTC: BijgewerktOp gebruikt GETUTCDATE(), nooit GETDATE() (#246).
CREATE TABLE [dbo].[SportlinkVeldplannerBlok] (
	[ClubCode]			NVARCHAR(20)	NOT NULL,
	[PublicMatchId]		NVARCHAR(50)	NOT NULL,
	[Datum]				DATE			NOT NULL,
	[Label]				NVARCHAR(300)	NOT NULL,
	[Veld]				NVARCHAR(100)	NOT NULL,
	[StartTijd]			NVARCHAR(5)		NOT NULL,
	[Veldafmeting]		DECIMAL(4, 2)	NOT NULL,
	[DuurMinuten]		INT				NOT NULL,
	[ExternalMatchId]	BIGINT			NULL,
	[RijHash]			NVARCHAR(64)	NOT NULL,
	[BijgewerktOp]		DATETIME2		NOT NULL DEFAULT GETUTCDATE(),
	CONSTRAINT [PK_SportlinkVeldplannerBlok] PRIMARY KEY CLUSTERED ([ClubCode] ASC, [PublicMatchId] ASC)
	)
GO

-- De planningsaanvraag leest en verwijdert per club en dag, het opruimen van oude rijen per club en datum.
CREATE NONCLUSTERED INDEX [IX_SportlinkVeldplannerBlok_ClubCode_Datum]
	ON [dbo].[SportlinkVeldplannerBlok] ([ClubCode] ASC, [Datum] ASC)
GO
