-- #1437: teller voor het wedstrijdnummer van zelf aangemaakte oefenwedstrijden (YYMMDD + volgnummer).
-- Eén rij per club en speeldag; atomair opgehoogd via MERGE ... WITH (HOLDLOCK) in
-- FunctionApp/Sportlink/SportlinkClubMatchRepository.cs. Postgres-tegenhanger:
-- Database.Postgres/migrations/031_wedstrijdnummerteller.sql.
CREATE TABLE [dbo].[WedstrijdnummerTeller] (
    [ClubCode]          NVARCHAR(20) NOT NULL,
    [Datum]             DATE         NOT NULL,
    [LaatsteVolgnummer] INT          NOT NULL,
    CONSTRAINT [PK_WedstrijdnummerTeller] PRIMARY KEY CLUSTERED ([ClubCode] ASC, [Datum] ASC)
) ON [PRIMARY]
