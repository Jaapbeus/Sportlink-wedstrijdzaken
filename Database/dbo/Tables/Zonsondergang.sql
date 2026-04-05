CREATE TABLE [dbo].[Zonsondergang] (
    [Datum]         DATE  NOT NULL,
    [Zonsondergang] TIME  NOT NULL,
    CONSTRAINT [PK_Zonsondergang] PRIMARY KEY CLUSTERED ([Datum] ASC)
);
