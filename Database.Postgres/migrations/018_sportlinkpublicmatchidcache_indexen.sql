-- 018_sportlinkpublicmatchidcache_indexen.sql — #1122 (review epic #986, bevinding A1)
--
-- public.sportlinkpublicmatchidcache (migratie 014) had alleen de primaire sleutel
-- (wedstrijdcode, clubcode). Twee queries filteren op clubcode ZONDER wedstrijdcode en konden die
-- sleutel dus niet gebruiken:
--   - SportlinkContractCheckTimerFunction / SportlinkExtensieHealthFunction:
--       WHERE clubcode = @clubcode ORDER BY opgehaaldop DESC LIMIT 1
--   - SportlinkPublicMatchIdRepository.ZoekWedstrijdenBijPublicMatchIdsAsync (#1111):
--       WHERE c.clubcode = @clubcode AND c.publicmatchid = ANY(@ids)
-- De tabel groeit één rij per ooit opgehaalde wedstrijd (warmup-timer #1017), dus dit is een
-- toenemende volledige scan. Additief (IF NOT EXISTS), de vorige code draait ongestoord door (§57).

CREATE INDEX IF NOT EXISTS ix_sportlinkpublicmatchidcache_clubcode_opgehaaldop
    ON public.sportlinkpublicmatchidcache (clubcode, opgehaaldop DESC);

CREATE INDEX IF NOT EXISTS ix_sportlinkpublicmatchidcache_clubcode_publicmatchid
    ON public.sportlinkpublicmatchidcache (clubcode, publicmatchid);
