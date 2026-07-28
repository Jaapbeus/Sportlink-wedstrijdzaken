-- Koppeling van een aangetroffen teamnaam-schrijfwijze aan zijn canonieke team (#692, #696, #700).
--
-- Twee soorten rijen, met hetzelfde doel maar een andere herkomst:
--   Bron = 'Sync'                → elke schrijfwijze die in his.matches/his.teams voorkomt. Hiermee
--                                  wordt het zoeken van een wedstrijd een exacte join op de ruwe
--                                  naam, zonder de C#-normalisatie in T-SQL te hoeven nabouwen.
--   Bron = 'AiDisambiguatie' /
--          'CoordinatorCorrectie' → geleerd uit e-mail of handmatig toegevoegd. Deze staan op
--                                  'pending' tot een coördinator ze goedkeurt, zodat een foutieve
--                                  gok zich niet kan vastzetten (vgl. planner.ClassificatieCorrectie).
--
-- Alleen 'validated' rijen worden vertrouwd bij teamherkenning.
--
-- Uniek op (ClubCode, RuweTekst) en NIET op de genormaliseerde sleutel: meerdere ruwe schrijfwijzen
-- horen juist naar dezelfde sleutel te normaliseren ("[club] JO10-1" en "[club] O10-1" zijn hetzelfde
-- team), dus uniciteit op die sleutel zou de tweede schrijfwijze weigeren. De genormaliseerde kolom
-- heeft een gewone index voor de lookup vanuit de resolver.
CREATE TABLE [dbo].[TeamAliassen] (
    [Id]                        INT IDENTITY(1,1) NOT NULL,
    [ClubCode]                  NVARCHAR(20)  NOT NULL,
    [RuweTekst]                 NVARCHAR(200) NOT NULL,   -- exact zoals aangetroffen, bijv. "13-1" of "J013 1"
    [RuweTekstGenormaliseerd]   NVARCHAR(200) NOT NULL,   -- na TeamNaamNormalisatie, voor snelle lookup
    [TeamId]                    INT NOT NULL,
    [Bron]                      NVARCHAR(20)  NOT NULL,   -- 'Sync' | 'AiDisambiguatie' | 'CoordinatorCorrectie'
    [Status]                    NVARCHAR(20)  NOT NULL CONSTRAINT [DF_TeamAliassen_Status] DEFAULT 'pending', -- pending|validated|rejected
    [AantalKeerGebruikt]        INT NOT NULL CONSTRAINT [DF_TeamAliassen_AantalKeerGebruikt] DEFAULT 0,
    [mta_inserted]              DATETIME NOT NULL CONSTRAINT [DF_TeamAliassen_Inserted] DEFAULT GETUTCDATE(),
    [mta_modified]              DATETIME NOT NULL CONSTRAINT [DF_TeamAliassen_Modified] DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_TeamAliassen] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_TeamAliassen_Teams] FOREIGN KEY ([TeamId]) REFERENCES [dbo].[Teams]([TeamId]),
    CONSTRAINT [UQ_TeamAliassen_Club_RuweTekst] UNIQUE ([ClubCode], [RuweTekst])
);
GO

CREATE NONCLUSTERED INDEX [IX_TeamAliassen_Club_Genormaliseerd]
    ON [dbo].[TeamAliassen] ([ClubCode], [RuweTekstGenormaliseerd])
    INCLUDE ([TeamId], [Status]);
