-- 026_appsettings_theme_modes.sql — volledig kleurenpalet per modus (licht/donker) op
-- public.appsettings. Onderdeel van #1254, binnen epic #1249.
--
-- Bewust geen kolom per kleur maar één JSON-document per modus: het aantal kleuren groeit met dit
-- epic nog, en een kolom per kleur zou bij elke uitbreiding een nieuwe migratie in twee tiers
-- betekenen. Zie "Thema-logica gedeeld" (#1248) in docs/ARCHITECTUUR-DATABASE-TIERS.md voor de
-- reden dat de bijbehorende C#-logica op één plek staat.
--
-- Additief: de vier platte themecolor*-kolommen blijven de terugval voor clubs die nog geen
-- licht/donker-set hebben ingesteld, dus bestaande installaties merken hier niets van.
--
-- RLS staat op deze tabel al aan (021_enable_row_level_security.sql); een ADD COLUMN raakt dat niet.

ALTER TABLE public.appsettings
    ADD COLUMN IF NOT EXISTS themecolorslightjson TEXT NULL,
    ADD COLUMN IF NOT EXISTS themecolorsdarkjson TEXT NULL;
