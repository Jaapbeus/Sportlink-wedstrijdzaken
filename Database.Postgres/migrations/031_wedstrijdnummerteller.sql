-- #1437: teller voor het wedstrijdnummer van zelf aangemaakte oefenwedstrijden: YYMMDD + een
-- tweecijferig volgnummer per speeldag (26100201, 26100202, ...). Eén rij per club en speeldag;
-- het volgnummer wordt atomair opgehoogd met INSERT ... ON CONFLICT DO UPDATE (zie
-- FunctionApp.Postgres/Sportlink/SportlinkClubMatchRepository.cs). SQL Server-tegenhanger:
-- Database/dbo/Tables/WedstrijdnummerTeller.sql.
CREATE TABLE IF NOT EXISTS public.wedstrijdnummerteller (
    clubcode          VARCHAR(20) NOT NULL,
    datum             DATE        NOT NULL,
    laatstevolgnummer INTEGER     NOT NULL,
    PRIMARY KEY (clubcode, datum)
);

-- #1198/#1220: elke tabel in public krijgt RLS in dezelfde migratie (geen policies: de FunctionApp-rol omzeilt RLS).
ALTER TABLE public.wedstrijdnummerteller ENABLE ROW LEVEL SECURITY;
