-- 043_season_per_club.sql — issue 1715: de seizoensstartmaand is per club instelbaar
-- (public.appsettings.seasonstartmonth, standaard 7 = juli) en public.season wordt per club
-- bijgehouden. Voorheen was de tabel clubneutraal en werd het seizoen uit MIN(seasonstartmonth)
-- over alle clubs afgeleid (migratie 008, PostgresSeasonProcedures); dat is een gok zodra twee clubs
-- in één database een verschillende maand hebben. Nu leest elke consument de rijen van zijn eigen club.
--
-- Gedrag blijft gelijk voor bestaande installaties: elke club krijgt dezelfde seizoensnamen als
-- voorheen, maar met de datums uit de eigen startmaand (de kolomdefault 7 geeft dus dezelfde datums
-- als de oude MIN()). De tabel is afgeleide data zonder verwijzingen vanuit andere tabellen.
--
-- Identifier-casing: lowercase, ongequote — conform docs/ARCHITECTUUR-DATABASE-TIERS.md §3.
-- Geen nieuwe tabel, dus geen aanvullende RLS: public.season heeft ENABLE ROW LEVEL SECURITY sinds 021.

ALTER TABLE public.season ADD COLUMN IF NOT EXISTS clubcode VARCHAR(20) NULL;

-- De oude uniciteit op alleen de naam laat twee clubs met hetzelfde seizoen niet toe.
ALTER TABLE public.season DROP CONSTRAINT IF EXISTS ux_season_name;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public.season'::regclass AND conname = 'ux_season_clubcode_name')
    THEN
        ALTER TABLE public.season ADD CONSTRAINT ux_season_clubcode_name UNIQUE (clubcode, name);
    END IF;
END $$;

-- Elke bestaande (clubloze) seizoensrij wordt per club herberekend uit de eigen startmaand; de naam
-- ("2026-2027") bepaalt het startjaar. Clubs met een ongeldige maand worden overgeslagen: de
-- seizoensprocedure zaait die bij de eerstvolgende sync met de standaardmaand.
INSERT INTO public.season (clubcode, name, datefrom, dateuntil)
SELECT a.clubcode,
       s.name,
       make_date(left(s.name, 4)::integer, a.seasonstartmonth, 1),
       make_date(left(s.name, 4)::integer + 1, a.seasonstartmonth, 1) - 1
FROM public.season s
CROSS JOIN public.appsettings a
WHERE s.clubcode IS NULL
  AND s.name ~ '^[0-9]{4}-[0-9]{4}$'
  AND a.seasonstartmonth BETWEEN 1 AND 12
ON CONFLICT (clubcode, name) DO NOTHING;

DELETE FROM public.season WHERE clubcode IS NULL;

ALTER TABLE public.season ALTER COLUMN clubcode SET NOT NULL;
