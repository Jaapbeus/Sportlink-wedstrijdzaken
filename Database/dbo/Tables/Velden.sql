CREATE TABLE [dbo].[Velden] (
    [VeldNummer]      INT           NOT NULL,
    [VeldNaam]        NVARCHAR(50)  NOT NULL,
    [VeldType]        NVARCHAR(20)  NOT NULL CONSTRAINT [DF_Velden_Type] DEFAULT 'kunstgras',
    [HeeftKunstlicht] BIT           NOT NULL,
    [Actief]          BIT           NOT NULL CONSTRAINT [DF_Velden_Actief] DEFAULT 1,
    CONSTRAINT [PK_Velden] PRIMARY KEY CLUSTERED ([VeldNummer] ASC)
);
