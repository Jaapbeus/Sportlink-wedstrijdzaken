-- #1266 — SQL Server-tegenhanger van public.sportlinkcontractcheck (Postgres-migratie 016, #998).
--
-- Doel: het resultaat van de dagelijkse contract-check bewaren. Die timer doet één read-call per
-- dag die de vorm van de Sportlink Match-respons controleert, zodat een stille Sportlink-release
-- ons niet pas bereikt via een mislukte mutatie.
--
-- Bewust GEEN hergebruik van dbo.SportlinkMutationAudit: die tabel betekent "één rij per
-- mutatiepoging", en een contract-check is geen mutatie.
--
-- UTC: UitgevoerdOp gebruikt GETUTCDATE(), nooit GETDATE() (#246).
CREATE TABLE [dbo].[SportlinkContractCheck] (
	[Id]						BIGINT			IDENTITY(1,1)	NOT NULL,
	[ClubCode]					NVARCHAR(20)	NOT NULL,
	[RolNaam]					NVARCHAR(50)	NOT NULL,
	[UitgevoerdOp]				DATETIME2		NOT NULL DEFAULT GETUTCDATE(),
	[IsOk]						BIT				NOT NULL,
	[HttpStatus]				INT				NULL,
	[AfwijkendeVelden]			NVARCHAR(MAX)	NULL,
	[FoutmeldingSamenvatting]	NVARCHAR(500)	NULL,
	CONSTRAINT [PK_SportlinkContractCheck] PRIMARY KEY CLUSTERED ([Id] ASC)
	)
GO

CREATE NONCLUSTERED INDEX [IX_SportlinkContractCheck_ClubCode_UitgevoerdOp]
	ON [dbo].[SportlinkContractCheck] ([ClubCode] ASC, [UitgevoerdOp] DESC)
GO
