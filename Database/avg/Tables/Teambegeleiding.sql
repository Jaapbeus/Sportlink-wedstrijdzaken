-- Bevat alleen de functioneel essentiële velden uit de Sportlink teambegeleiding export.
-- Naam is een samengesteld veld: Roepnaam [Tussenvoegsel] Achternaam.
-- Telefoonnummer = Mobiel nummer als gevuld, anders Telefoonnummer.
-- 🚨 AVG/GDPR: beperk SELECT-rechten tot bevoegde gebruikers en rollen.
CREATE TABLE [avg].[Teambegeleiding] (
    [Id]                     INT            IDENTITY (1, 1) NOT NULL,
    [Team]                   NVARCHAR (100) NULL,
    [LeeftijdscategorieTeam] NVARCHAR (50)  NULL,
    [Teamrol]                NVARCHAR (100) NULL,
    [Naam]                   NVARCHAR (300) NULL,
    [Emailadres]             NVARCHAR (200) NULL,
    [Telefoonnummer]         NVARCHAR (50)  NULL,
    [mta_imported]           DATETIME       CONSTRAINT [DF_avg_Teambegeleiding_mta_imported] DEFAULT (GETDATE()) NOT NULL,
    CONSTRAINT [PK_avg_Teambegeleiding] PRIMARY KEY CLUSTERED ([Id] ASC)
);
