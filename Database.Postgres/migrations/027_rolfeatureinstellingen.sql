-- 027_rolfeatureinstellingen.sql — #1341: per-club, per-rol instelbare zichtbaarheid van
-- Sportlink-acties (kleedkamers/scheidsrechter/veld toewijzen/wijzigen).
--
-- Generiek opgezet (ClubCode, RolNaam, FeatureKey, Enabled) — niet beperkt tot deze drie
-- Sportlink-acties, herbruikbaar voor toekomstige per-rol-instellingen (besluit eigenaar,
-- 2026-09-26). Geen rij voor een combinatie betekent UITGESCHAKELD (fail-closed) — de veiligere
-- default voor een autorisatie-instelling. 'admin' heeft altijd alles aan en komt hier bewust
-- nooit in voor; dit is uitsluitend voor rollen waarvoor de toggle iets betekent.
--
-- RLS aan in dezelfde migratie, conform de regel uit #1198/§65 van
-- docs/ARCHITECTUUR-DATABASE-TIERS.md — geen policies nodig (zie die regel voor de reden).

CREATE TABLE IF NOT EXISTS public.rolfeatureinstellingen (
    clubcode    VARCHAR(20)  NOT NULL,
    rolnaam     VARCHAR(50)  NOT NULL,
    featurekey  VARCHAR(100) NOT NULL,
    enabled     BOOLEAN      NOT NULL DEFAULT false,
    PRIMARY KEY (clubcode, rolnaam, featurekey)
);

ALTER TABLE public.rolfeatureinstellingen ENABLE ROW LEVEL SECURITY;
