-- 054_speeltijden_helften.sql — #1962 (WZ-ADR-014): speeltijden met aantal helften, totaal zonder buffer,
-- en de KNVB-categorieën 35+ en VR30+.
-- SQL Server-tegenhanger: Database/dbo/Tables/Speeltijden.sql en Database/Script.PostDeployment1.sql.
--
-- 1. Kolom wedstrijdhelften (1 of 2, standaard 2). Toernooivormen zoals 35+ en VR30+ spelen één helft.
-- 2. wedstrijdtotaal = wedstrijdhelften × wedstrijdhelft + wedstrijdrust, zonder buffer. De buffer staat op de
--    optimalisatiepagina; tot nu toe telden de senioren 10 minuten buffer mee (115 in plaats van 105, #291).
--    Rijen zonder helftduur (wedstrijdhelft = 0, oude data van vóór 003) blijven ongemoeid.
-- 3. 35+ en VR30+ voor elke club die al speeltijden heeft: 1 × 20 minuten, geen rust, half veld, standaard
--    voorkeurstijd 20:00. Een club die de categorie al heeft, houdt zijn eigen rij. Zoals de #291-aanvulling
--    alleen voor clubs mét speeltijden: een democlub zonder rijen krijgt zijn volledige set (inclusief deze twee)
--    als kopie van de primaire club via scripts/migrations/003-seed-allstars-demo-matches-postgres.sql. Een rij
--    hier zou die kopie blokkeren (die draait alleen voor een democlub zonder speeltijden, #1246).
--
-- Geen nieuwe tabel, dus geen RLS-wijziging (public.speeltijden heeft RLS sinds 021).

ALTER TABLE public.speeltijden
    ADD COLUMN IF NOT EXISTS wedstrijdhelften INTEGER NOT NULL DEFAULT 2;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_speeltijden_wedstrijdhelften') THEN
        ALTER TABLE public.speeltijden
            ADD CONSTRAINT ck_speeltijden_wedstrijdhelften CHECK (wedstrijdhelften IN (1, 2));
    END IF;
END $$;

UPDATE public.speeltijden
SET wedstrijdtotaal = wedstrijdhelften * wedstrijdhelft + wedstrijdrust
WHERE wedstrijdhelft > 0
  AND wedstrijdtotaal <> wedstrijdhelften * wedstrijdhelft + wedstrijdrust;

-- Expressie-index voor de hoofdletterongevoelige categorie per club (AGENTS.md: een LOWER()-vergelijking krijgt een
-- index op dezelfde uitdrukking; review #1966 R2-F1). Dient de aanvulling hieronder en de bestaande LOWER()-lookups in
-- AdminSpeeltijdenRepository (#956). Bewust niet UNIQUE: een fork met al twee schrijfwijzen van één categorie zou de
-- migratie dan laten falen; productie heeft er geen.
CREATE INDEX IF NOT EXISTS ix_speeltijden_clubcode_lower_leeftijd
    ON public.speeltijden (clubcode, LOWER(leeftijd));

INSERT INTO public.speeltijden
    (leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust, wedstrijdhelften,
     standaardvoorkeurtijd, clubcode)
SELECT v.leeftijd, 0.50, 20, 20, 0, 1, TIME '20:00', a.clubcode
FROM (VALUES ('35+'), ('VR30+')) AS v(leeftijd)
CROSS JOIN (SELECT DISTINCT clubcode FROM public.speeltijden) a
-- Hoofdletterongevoelig per club (review #1966 R1-F2): een bestaande 'vr30+' is dezelfde categorie. De primary
-- key is hoofdlettergevoelig, dus ON CONFLICT alleen zou er een tweede rij 'VR30+' naast zetten; de planner leest
-- de speeltijden in een hoofdletterongevoelige lookup en dan wint willekeurig één van beide.
WHERE NOT EXISTS (
    SELECT 1 FROM public.speeltijden t
    WHERE LOWER(t.leeftijd) = LOWER(v.leeftijd) AND t.clubcode = a.clubcode)
ON CONFLICT (leeftijd, clubcode) DO NOTHING;
