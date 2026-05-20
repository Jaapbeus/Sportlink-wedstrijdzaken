CREATE TABLE [dbo].[Speeltijden] (
    [Leeftijd]        NVARCHAR(10) NOT NULL,
    [Veldafmeting]    DECIMAL(4, 2) NOT NULL,
    [WedstrijdTotaal] INT          NOT NULL,
    [WedstrijdHelft]  INT          NOT NULL,
    [WedstrijdRust]   INT          NOT NULL,
    [ClubCode]        NVARCHAR(20)  NOT NULL CONSTRAINT [DF_Speeltijden_ClubCode] DEFAULT 'VRC', -- migratie-backwards-compat; inserts geven altijd expliciet ClubCode mee
    CONSTRAINT [PK_Speeltijden] PRIMARY KEY CLUSTERED ([Leeftijd] ASC)
) ON [PRIMARY]
