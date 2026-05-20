CREATE TABLE [dbo].[TeamVoorkeurTijden] (
    [Id]            INT IDENTITY(1,1) NOT NULL,
    [TeamNaam]      NVARCHAR(100) NOT NULL,
    [DagVanWeek]    INT NOT NULL,           -- 6=zaterdag, 7=zondag, 1-5=doordeweeks
    [VoorkeurTijd]  TIME NOT NULL,
    [Prioriteit]    INT NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_Prioriteit] DEFAULT 5,
    [Actief]        BIT NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_Actief] DEFAULT 1,
    [ClubCode]      NVARCHAR(20) NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_ClubCode] DEFAULT 'VRC',
    [mta_inserted]  DATETIME2 NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_Inserted] DEFAULT GETUTCDATE(),
    [mta_modified]  DATETIME2 NOT NULL CONSTRAINT [DF_TeamVoorkeurTijden_Modified] DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_TeamVoorkeurTijden] PRIMARY KEY CLUSTERED ([Id] ASC)
);
