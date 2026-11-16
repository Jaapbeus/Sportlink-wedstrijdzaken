-- #1979: clubbrede starttijd voor de veldoptimalisatie.
-- NULL behoudt de bestaande per-categorie standaardtijden.
ALTER TABLE public.appsettings ADD COLUMN IF NOT EXISTS optimalisatieaanvang TIME NULL;
