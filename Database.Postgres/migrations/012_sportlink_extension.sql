-- 012_sportlink_extension.sql — #988: Sportlink Web Extension (epic #986)
--
-- 1. sportlinkextensionenabled — schakelaar in public.appsettings, standaard UIT.
-- 2. public.sportlinkextensierollen — welke functionele rol (bv. 'Wedstrijdzaken') heeft een
--    eigen, smal-geschaald Sportlink-serviceaccount gekoppeld gekregen. Zie
--    docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §6: rol-gebaseerde service-accounts i.p.v.
--    één gedeelde credential, om privilege-escalatie via een toekomstige beperktere rol te
--    voorkomen. Geen live Sportlink-verificatie: sportlinkaccountnaam is handmatige invoer.

ALTER TABLE public.appsettings
    ADD COLUMN IF NOT EXISTS sportlinkextensionenabled boolean NOT NULL DEFAULT false;

CREATE TABLE IF NOT EXISTS public.sportlinkextensierollen (
    rolnaam              VARCHAR(50) PRIMARY KEY,
    laatstgekoppelddoor  VARCHAR(200) NULL,
    laatstgekoppeldop    TIMESTAMPTZ NULL,
    sportlinkaccountnaam VARCHAR(200) NULL,
    clubcode             VARCHAR(20) NOT NULL
);
