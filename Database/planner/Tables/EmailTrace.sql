-- #1568 deel B: permanente, PII-arme beslissingstrace per verwerkt e-mailbericht.
-- Postgres-tegenhanger: planner.emailtrace (Database.Postgres/migrations/038_planner_emailtrace.sql).
-- Bewust GEEN foreign key naar [planner].[EmailVerwerking]: de trace overleeft de verwerking
-- (die wordt na 30 dagen geanonimiseerd en na 90 dagen verwijderd; zie sp_CleanupEmailVerwerking).
CREATE TABLE [planner].[EmailTrace] (
    [Id]              BIGINT         IDENTITY (1, 1) NOT NULL,
    [VerwerkingId]    INT            NOT NULL,
    [ClubCode]        NVARCHAR (20)  NOT NULL CONSTRAINT [CK_EmailTrace_ClubCode] CHECK (LEN([ClubCode]) > 0),
    [Aangemaakt]      DATETIME2      NOT NULL CONSTRAINT [DF_EmailTrace_Aangemaakt] DEFAULT (GETUTCDATE()),
    [VerzoekType]     NVARCHAR (50)  NOT NULL,
    [Zekerheid]       NVARCHAR (10)  NOT NULL CONSTRAINT [CK_EmailTrace_Zekerheid] CHECK ([Zekerheid] IN (N'Zeker', N'Onzeker', N'Mislukt')),
    [SjabloonSleutel] NVARCHAR (60)  NULL,
    [TraceJson]       NVARCHAR (MAX) NOT NULL CONSTRAINT [CK_EmailTrace_TraceJson] CHECK (ISJSON([TraceJson]) = 1),
    [AppVersie]       NVARCHAR (20)  NOT NULL,
    CONSTRAINT [PK_EmailTrace] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [UQ_EmailTrace_VerwerkingId] UNIQUE ([VerwerkingId])
);
