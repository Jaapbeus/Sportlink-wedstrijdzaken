-- 016_sportlink_dryrun_en_contractcheck.sql — #998 (epic #986)
--
-- 1. sportlinkdryrun — schakelaar in public.appsettings die de daadwerkelijke PUT/POST naar
--    Sportlink overslaat (zie SportlinkClubClient.PutMutationAsync). Standaard TRUE voor alle
--    (nieuwe) clubs — een club die de extension nog niet expliciet heeft ingesteld, mag nooit
--    per ongeluk echt schrijven. BESLIST door de eigenaar: de bestaande productierij die de
--    extension al AAN heeft staan (sportlinkextensionenabled = true) wordt in dezelfde migratie
--    expliciet op dryrun = false gezet — geen gedragswijziging voor de huidige productieclub.
-- 2. public.sportlinkcontractcheck — resultaat van de dagelijkse contract-check-timer
--    (SportlinkContractCheckTimerFunction): één read-call per dag die de vorm van de Sportlink
--    Match-respons controleert (SportlinkMatchContract), zodat een stille Sportlink-release ons
--    niet pas via een mislukte mutatie bereikt. Bewust GEEN hergebruik van
--    public.sportlinkmutationaudit (die tabel = "één rij per mutatiepoging") — een contract-check
--    is geen mutatie. Lowercase snake_case kolommen, UTC (now()), ClubCode-discriminator —
--    conform de Postgres-tier-casingregel in CLAUDE.md.

ALTER TABLE public.appsettings
    ADD COLUMN IF NOT EXISTS sportlinkdryrun boolean NOT NULL DEFAULT true;

UPDATE public.appsettings
   SET sportlinkdryrun = false
 WHERE sportlinkextensionenabled = true;

CREATE TABLE IF NOT EXISTS public.sportlinkcontractcheck (
    id                       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    clubcode                 VARCHAR(20) NOT NULL,
    rolnaam                  VARCHAR(50) NOT NULL,
    uitgevoerdop             TIMESTAMPTZ NOT NULL DEFAULT now(),
    isok                     BOOLEAN NOT NULL,
    httpstatus               INT NULL,
    afwijkendevelden         TEXT NULL,
    foutmeldingsamenvatting  VARCHAR(500) NULL
);

CREATE INDEX IF NOT EXISTS ix_sportlinkcontractcheck_clubcode_uitgevoerdop
    ON public.sportlinkcontractcheck (clubcode, uitgevoerdop DESC);
