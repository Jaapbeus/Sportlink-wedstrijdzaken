-- #988: bijhouden welke functionele webapp-rol (bv. 'Wedstrijdzaken') een eigen, smal-geschaald
-- Sportlink-serviceaccount gekoppeld heeft gekregen. Geen live Sportlink-verificatie — SportlinkAccountNaam
-- is een handmatig ingevuld vrij tekstveld bij het registreren van de koppeling. Zie
-- docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §6 voor de architectuurbeslissing (rol-gebaseerde
-- service-accounts i.p.v. één gedeelde credential, om privilege-escalatie te voorkomen).
CREATE TABLE [dbo].[SportlinkExtensieRollen] (
	[RolNaam]              NVARCHAR(50)   NOT NULL,
	[LaatstGekoppeldDoor]  NVARCHAR(200)  NULL,
	[LaatstGekoppeldOp]    DATETIME2      NULL,
	[SportlinkAccountNaam] NVARCHAR(200)  NULL,
	[ClubCode]             NVARCHAR(20)   NOT NULL, -- geen DEFAULT: clubnaam hoort niet in het schema (#598)
	CONSTRAINT [PK_SportlinkExtensieRollen] PRIMARY KEY CLUSTERED ([RolNaam] ASC)
	)
