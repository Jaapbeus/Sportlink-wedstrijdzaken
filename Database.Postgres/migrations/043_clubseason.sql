-- 043_clubseason.sql — issue 1715: de seizoensstartmaand is per club instelbaar
-- (public.appsettings.seasonstartmonth, standaard 7 = juli) en de seizoenen worden per club bijgehouden.
-- Voorheen was public.season clubneutraal en werd het seizoen uit MIN(seasonstartmonth) over alle clubs
-- afgeleid (migratie 008, PostgresSeasonProcedures); dat is een gok zodra twee clubs in één database een
-- verschillende maand hebben. Nu leest elke consument de rijen van zijn eigen club uit public.clubseason.
--
-- Expand/contract (Codex-review #1743 ronde 1, docs/ARCHITECTUUR-DATABASE-TIERS.md §57): deploy.yml past
-- deze migratie toe vóór de nieuwe code, dus de vorige code draait er nog even op. Die schrijft in
-- public.season met ON CONFLICT (name) en zonder clubcode. Deze migratie is daarom uitsluitend additief:
-- een nieuwe tabel, public.season blijft onaangeroerd (vorm, sleutel ux_season_name en rijen). De oude
-- tabel wordt na deze release door geen code meer gelezen of geschreven; het opruimen ervan is een
-- aparte, latere release (contract-stap, zie §84).
--
-- Gedrag blijft gelijk voor bestaande installaties: elke club krijgt eenmalig dezelfde seizoensnamen als
-- in public.season, maar met de datums uit de eigen startmaand (de kolomdefault 7 geeft dus dezelfde
-- datums als de oude MIN()). Alleen voor een club die nog geen enkele rij heeft, zodat een herhaalde
-- uitvoering niets verandert aan een club waarvan de seizoenen inmiddels opnieuw zijn opgebouwd.
--
-- Identifier-casing: lowercase, ongequote — conform docs/ARCHITECTUUR-DATABASE-TIERS.md §3.
-- RLS: aan in dezelfde migratie, zonder policies (#1198, AGENTS.md "Supabase").

CREATE TABLE IF NOT EXISTS public.clubseason (
    clubcode  VARCHAR(20) NOT NULL,
    name      VARCHAR(9)  NOT NULL,
    datefrom  DATE        NOT NULL,
    dateuntil DATE        NOT NULL,
    CONSTRAINT pk_clubseason PRIMARY KEY (clubcode, name)
);

ALTER TABLE public.clubseason ENABLE ROW LEVEL SECURITY;

-- De naam ("2026-2027") bepaalt het startjaar. Clubs met een ongeldige maand worden overgeslagen: de
-- seizoensprocedure zaait die bij de eerstvolgende sync met de standaardmaand.
INSERT INTO public.clubseason (clubcode, name, datefrom, dateuntil)
SELECT a.clubcode,
       s.name,
       make_date(left(s.name, 4)::integer, a.seasonstartmonth, 1),
       make_date(left(s.name, 4)::integer + 1, a.seasonstartmonth, 1) - 1
FROM public.season s
CROSS JOIN public.appsettings a
WHERE s.name ~ '^[0-9]{4}-[0-9]{4}$'
  AND a.seasonstartmonth BETWEEN 1 AND 12
  AND NOT EXISTS (SELECT 1 FROM public.clubseason c WHERE c.clubcode = a.clubcode)
ON CONFLICT (clubcode, name) DO NOTHING;
