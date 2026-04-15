-- KNVB speeldagenkalender — afgeleid weekend-overzicht per regio.
-- Bron: PDF's van https://www.knvb.nl/assist-wedstrijdsecretarissen/veldvoetbal/seizoensplanning/speeldagenkalenders
-- Vulling: handmatig per seizoen via Script.PostDeployment1.sql.
-- Doel: planner kan vooraf zien of een datum competitie/beker/inhaal/vrij is en welke
-- categorieen actief zijn. Sportlink (his.matches) blijft autoritatief voor concrete wedstrijden.
CREATE TABLE [dbo].[KnvbKalenderDag] (
    [Seizoen]          VARCHAR(9)    NOT NULL,                   -- '2025/2026'
    [Regio]            VARCHAR(20)   NOT NULL,                   -- 'West', 'Landelijk', 'NoordOost', 'Zuid', 'LandelijkJeugd'
    [Datum]            DATE          NOT NULL,                   -- speeldatum (zaterdag of bijzondere dag)
    [DagType]          VARCHAR(20)   NOT NULL,                   -- 'Competitie','Beker','Inhaal','Vrij','NC','Toernooi','Feestdag'
    [HeeftSenioren]    BIT           NOT NULL,                   -- senioren-categorieen actief (competitie/beker/inhaal/NC)
    [HeeftJeugd]       BIT           NOT NULL,                   -- O13-O19/O23 actief
    [HeeftMeiden]      BIT           NOT NULL,                   -- meiden / vrouwen actief
    [PupillenToernooi] BIT           NOT NULL CONSTRAINT [DF_KnvbKalenderDag_Pup] DEFAULT 0, -- vrijdag pupillen 7x7 toernooi
    [Schoolvakantie]   VARCHAR(10)   NULL,                       -- 'M','N','Z','MN','MZ','NZ','MNZ' (Midden/Noord/Zuid)
    [Feestdag]         NVARCHAR(50)  NULL,                       -- '2e Paasdag','Hemelvaartsdag','Pinksterzaterdag', etc.
    [Opmerking]        NVARCHAR(200) NULL,
    [Bron]             NVARCHAR(200) NULL,                       -- URL naar bron-PDF
    CONSTRAINT [PK_KnvbKalenderDag] PRIMARY KEY CLUSTERED ([Seizoen] ASC, [Regio] ASC, [Datum] ASC),
    CONSTRAINT [CK_KnvbKalenderDag_DagType] CHECK ([DagType] IN ('Competitie','Beker','Inhaal','Vrij','NC','Toernooi','Feestdag'))
);
GO

CREATE NONCLUSTERED INDEX [IX_KnvbKalenderDag_Datum]
    ON [dbo].[KnvbKalenderDag] ([Datum] ASC)
    INCLUDE ([Regio], [DagType]);
GO
