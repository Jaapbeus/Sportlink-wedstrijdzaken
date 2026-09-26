-- #1341: per-club, per-rol instelbare zichtbaarheid van Sportlink-acties (kleedkamers/
-- scheidsrechter/veld toewijzen/wijzigen). Generiek opgezet (ClubCode, RolNaam, FeatureKey,
-- Enabled) — niet beperkt tot deze drie acties, herbruikbaar voor toekomstige per-rol-
-- instellingen (besluit eigenaar, 2026-09-26). Geen rij voor een combinatie betekent
-- UITGESCHAKELD (fail-closed). 'admin' heeft altijd alles aan en komt hier nooit in voor.
CREATE TABLE [dbo].[RolFeatureInstellingen] (
	[ClubCode]   NVARCHAR(20)  NOT NULL, -- geen DEFAULT: clubnaam hoort niet in het schema (#598)
	[RolNaam]    NVARCHAR(50)  NOT NULL,
	[FeatureKey] NVARCHAR(100) NOT NULL,
	[Enabled]    BIT           NOT NULL DEFAULT 0,
	CONSTRAINT [PK_RolFeatureInstellingen] PRIMARY KEY CLUSTERED ([ClubCode] ASC, [RolNaam] ASC, [FeatureKey] ASC)
	)
