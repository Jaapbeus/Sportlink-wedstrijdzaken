-- 013_sportlink_club_integratie.sql — #991/#990 (epic #986): productie-tokenopslag + PublicMatchId-cache
--
-- 1. public.sportlinkservicetokens — het rotarende refresh_token per functionele rol, productie-
--    persistent. Besluit (zie docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §6 / issue #990):
--    eigen DB-tabel i.p.v. Key Vault (geen nieuwe Azure-resource, geen extra kosten) en i.p.v. een
--    Function App Setting via ARM-API (zou een aparte Azure AD-integratie met schrijfrechten op de
--    eigen Function App vereisen). Bevat een echt geheim — nooit via een endpoint teruggegeven of
--    gelogd.
-- 2. public.sportlinkpublicmatchidcache — resultaat van de #987-reverse-lookup
--    (MatchProgramOverview, matchend op ExternalMatchId = onze eigen wedstrijdnummer), zodat de
--    trage (12+ s), niet-club-gescoped lookup maar één keer per wedstrijd nodig is.

CREATE TABLE IF NOT EXISTS public.sportlinkservicetokens (
    rolnaam               VARCHAR(50) NOT NULL,
    clubcode               VARCHAR(20) NOT NULL,
    refreshtoken           TEXT NOT NULL,
    refreshtokenvervaltop  TIMESTAMPTZ NOT NULL,
    bijgewerktop           TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (rolnaam, clubcode)
);

CREATE TABLE IF NOT EXISTS public.sportlinkpublicmatchidcache (
    wedstrijdcode  BIGINT NOT NULL,
    clubcode        VARCHAR(20) NOT NULL,
    publicmatchid   VARCHAR(50) NOT NULL,
    opgehaaldop     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (wedstrijdcode, clubcode)
);
