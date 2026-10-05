-- #1547: twee herstelstappen op de dynamisch beheerde ETL-tabellen. Beide zijn idempotent, en
-- beide slaan stil over als de tabel nog niet bestaat: his.* wordt pas door de eerste sync
-- aangemaakt (PostgresSchemaGenerator, #818), niet door een migratie.
--
-- 1. Business key van his.matchdetails: wedstrijdcode → interncode.
--    "wedstrijdcode" uit /wedstrijd-informatie is Sportlinks wedstrijdNUMMER, en dat is niet uniek:
--    clubwedstrijden hebben vaak gewoon nummer 1. Detailrijen van verschillende wedstrijden
--    overschreven elkaar daardoor, of werden in stg overgeslagen. interncode is gelijk aan
--    his.matches.wedstrijdcode en wél uniek. CREATE TABLE IF NOT EXISTS in de schemagenerator past
--    een bestaande tabel niet aan, dus de gegenereerde kolom wordt hier omgebouwd. De expressie is
--    letterlijk wat PostgresSchemaGenerator.BuildBusinessKeyExpression voor ["interncode"] maakt.
--    Eventuele dubbele interncodes (productie had er bij het schrijven nul) worden eerst
--    teruggebracht tot de meest recente rij; de volgende sync vult de details sowieso opnieuw.
--
-- 2. Gespeelde wedstrijden die de reconciliatie ten onrechte als verwijderd markeerde.
--    /programma laat een wedstrijd vallen zodra hij gespeeld is; de reconciliatie keek tot #1547
--    ook naar datums vóór vandaag en markeerde daardoor elke week de wedstrijden van de afgelopen
--    speeldag als verwijderd. Kenmerk: gemarkeerd ná de wedstrijddatum. Een wedstrijd die Sportlink
--    vóór zijn datum schrapte, blijft gemarkeerd. Toekomstige wedstrijden die na een haperende run
--    terugkwamen herstelt de upsert zelf bij de eerstvolgende sync.
--
-- 3. Gespeelde wedstrijden zonder datum en veld.
--    Een gespeelde wedstrijd kwam alleen nog via /uitslagen in stg, zonder kaledatum en veld, en de
--    upsert overschreef de his-rij daarmee. De sync vult die kolommen voortaan aan uit his; deze stap
--    repareert wat al leeg is: kaledatum uit de lokale datum van wedstrijddatum, veld uit de
--    wedstrijd-informatie van Sportlink (his.matchdetails.veldnaam, via interncode).
DO $$
BEGIN
    IF to_regclass('his.matchdetails') IS NOT NULL
       AND NOT EXISTS (
           SELECT 1 FROM information_schema.columns
           WHERE table_schema = 'his' AND table_name = 'matchdetails'
             AND column_name = 'bk_matchdetails'
             AND generation_expression LIKE '%interncode%')
    THEN
        DELETE FROM his.matchdetails a
        USING his.matchdetails b
        WHERE a.interncode IS NOT DISTINCT FROM b.interncode
          AND (a.mta_modified, a.ctid) < (b.mta_modified, b.ctid);

        DROP INDEX IF EXISTS his."UQ_matchdetails_bk";
        ALTER TABLE his.matchdetails DROP COLUMN IF EXISTS bk_matchdetails;
        ALTER TABLE his.matchdetails
            ADD COLUMN bk_matchdetails TEXT GENERATED ALWAYS AS (COALESCE("interncode"::text, '')) STORED;
        CREATE UNIQUE INDEX IF NOT EXISTS "UQ_matchdetails_bk" ON his.matchdetails (bk_matchdetails);
    END IF;

    IF to_regclass('his.matches') IS NOT NULL THEN
        UPDATE his.matches
        SET kaledatum = left(wedstrijddatum, 10) || ' 00:00:00.00'
        WHERE kaledatum IS NULL
          AND wedstrijddatum ~ '^\d{4}-\d{2}-\d{2}';

        UPDATE his.matches
        SET mta_deleted = NULL
        WHERE mta_deleted IS NOT NULL
          AND kaledatum ~ '^\d{4}-\d{2}-\d{2}'
          AND (mta_deleted AT TIME ZONE 'Europe/Amsterdam')::date > left(kaledatum, 10)::date;

        IF to_regclass('his.matchdetails') IS NOT NULL THEN
            UPDATE his.matches m
            SET veld = md.veldnaam
            FROM his.matchdetails md
            WHERE md.interncode = m.wedstrijdcode
              AND m.veld IS NULL
              AND COALESCE(md.veldnaam, '') <> '';
        END IF;
    END IF;
END
$$;
