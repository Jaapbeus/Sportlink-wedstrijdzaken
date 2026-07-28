CREATE TABLE [dbo].[Speeltijden] (
    [Leeftijd]        NVARCHAR(10) NOT NULL,
    [Veldafmeting]    DECIMAL(4, 2) NOT NULL,
    [WedstrijdTotaal] INT          NOT NULL,
    [WedstrijdHelft]  INT          NOT NULL,
    [WedstrijdRust]   INT          NOT NULL,
    -- Standaard voorkeurstijd per leeftijdscategorie (#666). Wordt gebruikt als een team géén eigen
    -- rij in dbo.TeamVoorkeurTijden heeft voor de speeldag. NULL = geen streeftijd; de planner valt
    -- dan terug op het eerst beschikbare slot. Per club instelbaar via Beheer → Speeltijden.
    [StandaardVoorkeurTijd] TIME    NULL,
    [ClubCode]        NVARCHAR(20)  NOT NULL,
    CONSTRAINT [PK_Speeltijden] PRIMARY KEY CLUSTERED ([Leeftijd] ASC, [ClubCode] ASC)
) ON [PRIMARY]
