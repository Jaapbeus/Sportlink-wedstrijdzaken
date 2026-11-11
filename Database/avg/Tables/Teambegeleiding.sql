-- Bevat alleen de functioneel essentiële velden uit de Sportlink teambegeleiding export.
-- Naam is een samengesteld veld: Roepnaam [Tussenvoegsel] Achternaam.
-- Telefoonnummer = Mobiel nummer als gevuld, anders Telefoonnummer.
-- 🚨 AVG/GDPR: beperk SELECT-rechten tot bevoegde gebruikers en rollen.
CREATE TABLE [avg].[Teambegeleiding] (
    [Id]                     INT            IDENTITY (1, 1) NOT NULL,
    [Team]                   NVARCHAR (100) NULL,
    [LeeftijdscategorieTeam] NVARCHAR (50)  NULL,
    [Teamrol]                NVARCHAR (100) NULL,
    [Functie]                NVARCHAR (150) NULL,
    [Naam]                   NVARCHAR (300) NULL,
    [Emailadres]             NVARCHAR (200) NULL,
    [Telefoonnummer]         NVARCHAR (50)  NULL,
    [mta_imported]           DATETIME       CONSTRAINT [DF_avg_Teambegeleiding_mta_imported] DEFAULT (GETUTCDATE()) NOT NULL,
    [ClubCode]               NVARCHAR (20)  NOT NULL CONSTRAINT [DF_avg_Teambegeleiding_ClubCode] DEFAULT '',
    CONSTRAINT [PK_avg_Teambegeleiding] PRIMARY KEY CLUSTERED ([Id] ASC)
);
GO

-- #1862: de opzoeking "bij welke teams staat dit e-mailadres?" (AfzenderTeamBron) vergelijkt
-- LOWER(LTRIM(RTRIM(Emailadres))) = @adres. Een persisted computed column met index is de SQL Server-tegenhanger van
-- Postgres' expressie-index (Database.Postgres/migrations/048_teambegeleiding_email_index.sql): SQL Server matcht de
-- expressie uit de query automatisch, dus de querytekst blijft gelijk. Zuiver additief en niet uniek. Idempotent in
-- Database/Script.PostDeployment1.sql. Vereist SET QUOTED_IDENTIFIER ON.
ALTER TABLE [avg].[Teambegeleiding]
    ADD [EmailadresGenormaliseerd] AS LOWER(LTRIM(RTRIM([Emailadres]))) PERSISTED;
GO

CREATE NONCLUSTERED INDEX [IX_avg_Teambegeleiding_EmailadresGenormaliseerd]
    ON [avg].[Teambegeleiding] ([EmailadresGenormaliseerd])
    INCLUDE ([Team], [ClubCode]);
GO
