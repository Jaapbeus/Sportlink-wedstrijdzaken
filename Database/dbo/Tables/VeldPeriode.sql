-- #581: een periode is een herbruikbaar regime (bijv. "Zomerstop" of "Competitie") met een
-- vaste geldigheidsrange. VeldBeschikbaarheid-rijen kunnen naar een periode verwijzen via
-- PeriodeId; rijen zonder PeriodeId (NULL) blijven het standaardregime en gelden buiten elke
-- actieve periode — dat is precies het gedrag van vóór deze feature (achterwaartse compatibiliteit,
-- harde eis uit #581).
CREATE TABLE [dbo].[VeldPeriode] (
    [Id]       INT          IDENTITY(1,1) NOT NULL,
    [Naam]     NVARCHAR(50) NOT NULL,
    [DatumVan] DATE         NOT NULL,
    [DatumTot] DATE         NOT NULL,
    [Actief]   BIT          NOT NULL CONSTRAINT [DF_VeldPeriode_Actief] DEFAULT 1,
    [ClubCode] NVARCHAR(20) NOT NULL, -- geen DEFAULT: clubnaam hoort niet in het schema (#598)
    CONSTRAINT [PK_VeldPeriode] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [CK_VeldPeriode_Datums] CHECK ([DatumTot] >= [DatumVan])
);
