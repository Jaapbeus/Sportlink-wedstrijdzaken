-- 054_speeltijden_helften.sql — #1962 (WZ-ADR-014): speeltijden met aantal helften, totaal zonder buffer,
-- en de KNVB-categorieën 35+ en VR30+.
-- SQL Server-tegenhanger: Database/dbo/Tables/Speeltijden.sql en Database/Script.PostDeployment1.sql.
--
-- 1. Kolom wedstrijdhelften (1 of 2, standaard 2). Toernooivormen zoals 35+ en VR30+ spelen één helft.
-- 2. wedstrijdtotaal = wedstrijdhelften × wedstrijdhelft + wedstrijdrust, zonder buffer. De buffer staat op de
--    optimalisatiepagina; tot nu toe telden de senioren 10 minuten buffer mee (115 in plaats van 105, #291).
--    Rijen zonder helftduur (wedstrijdhelft = 0, oude data van vóór 003) blijven ongemoeid.
-- 3. 35+ en VR30+ voor elke club in public.appsettings, ook de democlub: 1 × 20 minuten, geen rust,
--    half veld, standaard voorkeurstijd 20:00. Een club die de categorie al heeft, houdt zijn eigen rij.
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

INSERT INTO public.speeltijden
    (leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust, wedstrijdhelften,
     standaardvoorkeurtijd, clubcode)
SELECT v.leeftijd, 0.50, 20, 20, 0, 1, TIME '20:00', a.clubcode
FROM (VALUES ('35+'), ('VR30+')) AS v(leeftijd)
CROSS JOIN (SELECT DISTINCT clubcode FROM public.appsettings) a
ON CONFLICT (leeftijd, clubcode) DO NOTHING;
