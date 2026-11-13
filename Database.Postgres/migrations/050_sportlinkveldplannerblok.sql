-- 050_sportlinkveldplannerblok.sql — #1915: de Planning koppelt wedstrijden aan de Sportlink-veldplanner op een
-- sleutel van Sportlink zelf (PublicMatchId) en niet meer op teamnaam.
--
-- public.sportlinkveldplannerblok is een afgeleide kopie van wat de veldplanner van Sportlink nu toont: één rij per
-- wedstrijdblok, met PublicMatchId als sleutel per club. Bij elke planningsaanvraag wordt per sleutel de rijhash
-- vergeleken en alleen het verschil geschreven; Sportlink blijft leidend, deze tabel is nooit een bron.
-- externalmatchid is het bondswedstrijdnummer (onze his.matches.wedstrijdnummer). Het veldplanner-antwoord draagt dat
-- niet; het wordt eenmalig per sleutel bij Sportlink opgevraagd en blijft daarna staan (onveranderlijk), en is de brug
-- naar onze eigen wedstrijden.
--
-- Expand/contract (docs/ARCHITECTUUR-DATABASE-TIERS.md §57): deploy.yml past deze migratie toe vóór de nieuwe code. De
-- migratie is uitsluitend additief (een nieuwe tabel); de vorige code kent de tabel niet en draait er ongestoord op.
--
-- Identifier-casing: lowercase, ongequote — conform docs/ARCHITECTUUR-DATABASE-TIERS.md §3.
-- RLS: aan in dezelfde migratie, zonder policies (#1198, AGENTS.md "Supabase").

CREATE TABLE IF NOT EXISTS public.sportlinkveldplannerblok (
    clubcode        VARCHAR(20)   NOT NULL,
    publicmatchid   VARCHAR(50)   NOT NULL,
    datum           DATE          NOT NULL,
    label           VARCHAR(300)  NOT NULL,
    veld            VARCHAR(100)  NOT NULL,
    starttijd       VARCHAR(5)    NOT NULL,
    veldafmeting    NUMERIC(4, 2) NOT NULL,
    duurminuten     INTEGER       NOT NULL,
    externalmatchid BIGINT        NULL,
    rijhash         VARCHAR(64)   NOT NULL,
    bijgewerktop    TIMESTAMPTZ   NOT NULL DEFAULT now(),
    CONSTRAINT pk_sportlinkveldplannerblok PRIMARY KEY (clubcode, publicmatchid)
);

-- De planningsaanvraag leest en verwijdert per club en dag, het opruimen van oude rijen per club en datum.
CREATE INDEX IF NOT EXISTS ix_sportlinkveldplannerblok_clubcode_datum
    ON public.sportlinkveldplannerblok (clubcode, datum);

ALTER TABLE public.sportlinkveldplannerblok ENABLE ROW LEVEL SECURITY;
