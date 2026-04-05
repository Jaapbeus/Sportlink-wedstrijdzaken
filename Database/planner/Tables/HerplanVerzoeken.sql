CREATE TABLE [planner].[HerplanVerzoeken] (
    [Id]                    INT            IDENTITY(1,1) NOT NULL,
    [Wedstrijdcode]         BIGINT         NOT NULL,
    [HuidigeWedstrijd]      NVARCHAR(200)  NOT NULL,
    [HuidigeDatum]          DATE           NOT NULL,
    [HuidigeAanvangsTijd]   TIME           NOT NULL,
    [HuidigeVeldNaam]       NVARCHAR(50)   NULL,
    [GewensteAanvangsTijd]  TIME           NOT NULL,
    [GewenstVeldNummer]     INT            NULL,
    [Status]                NVARCHAR(20)   NOT NULL CONSTRAINT [DF_HerplanVerzoeken_Status] DEFAULT 'Aangevraagd',
    [AangevraagdDoor]       NVARCHAR(200)  NULL,
    [Opmerking]             NVARCHAR(500)  NULL,
    [mta_inserted]          DATETIME       NOT NULL CONSTRAINT [DF_HerplanVerzoeken_Ins] DEFAULT GETDATE(),
    [mta_modified]          DATETIME       NOT NULL CONSTRAINT [DF_HerplanVerzoeken_Mod] DEFAULT GETDATE(),
    CONSTRAINT [PK_HerplanVerzoeken] PRIMARY KEY CLUSTERED ([Id] ASC)
);
