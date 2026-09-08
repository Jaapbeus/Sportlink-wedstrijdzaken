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
	-- Samengestelde sleutel, niet alleen RolNaam: dit schema draait altijd met minstens twee clubs
	-- (de echte club + AllStars FC-demo, zie CLAUDE.md "Deployment-model"), en elke club registreert
	-- zijn eigen koppeling voor dezelfde rolnaam (bv. 'Wedstrijdzaken'). Zonder ClubCode in de sleutel
	-- botst de tweede club op de eerste (of overschrijft die, zie public.sportlinkextensierollen).
	CONSTRAINT [PK_SportlinkExtensieRollen] PRIMARY KEY CLUSTERED ([RolNaam] ASC, [ClubCode] ASC)
	)
