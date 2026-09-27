-- 028_sportlinkmutationaudit_notitie.sql — testnotitie gekoppeld aan een Sportlink-mutatiepoging
-- (#1320): de eigenaar-gestuurde productieproef met trace van validatie en bevestiging.
--
-- De eigenaar kan na een poging vastleggen wat er in Sportlink Club zelf zichtbaar was (bijv. of
-- de tegenstander een melding kreeg). Additief en nullable: bestaande rijen en bestaande audit-
-- afronding (LogPogingAsync/VoltooiAsync) blijven ongewijzigd.
--
-- RLS staat op deze tabel al aan (021_enable_row_level_security.sql); een ADD COLUMN raakt dat niet.

ALTER TABLE public.sportlinkmutationaudit
    ADD COLUMN IF NOT EXISTS notitie TEXT NULL;
