-- 001_baseline.sql — eerste Postgres-baseline-migratie (#821).
--
-- LET OP — DIT IS BEWUST GEEN VOLLEDIGE SCHEMA-BASELINE.
-- Dekt uitsluitend de tabellen waar tot nu toe (#819) al een Postgres-tegenhanger voor is
-- gebouwd: dbo.AppSettings, dbo.Velden, dbo.Speeltijden en planner.GeplandeWedstrijden — de vier
-- configuratietabellen die de planner-kernview nodig heeft. Alle overige SQL Server-tabellen met
-- een dbo/planner/his/stg/mta/pub-schema (AppSettingsAudit, Teams, TeamAliassen,
-- VeldBeschikbaarheid, VeldTraining, TeamVoorkeurTijden, TeamRegels, UitgeslotenEmailAdressen,
-- EmailTemplateInstellingen, Teambegeleiding, Season, DateTable, en meer) hebben nog GEEN
-- Postgres-equivalent. Dit is een expliciete, bewuste scope-beperking — zie de PR-beschrijving en
-- de toelichting op issue #821 voor de volledige lijst ontbrekende tabellen (#851 bracht deze lijst
-- tijdens het ontwerp van de epic-brede zelftest boven water).
--
-- De ETL-staging/history-tabellen (his.matches, his.teams, his.matchdetails en hun stg-tegenhangers)
-- horen hier NIET thuis: die worden dynamisch beheerd door Database.Postgres/PostgresSchemaGenerator
-- (#818), aangeroepen door de sync-pipeline zelf, niet via een migratiebestand.
--
-- Identifier-casing: lowercase, ongequote — conform docs/ARCHITECTUUR-DATABASE-TIERS.md §3.
-- Schema: 'public' in plaats van SQL Server's 'dbo' (zelfde document) — 'planner' is een
-- betekenisvolle domeinschemanaam en blijft gelijk aan de SQL Server-tier.

CREATE SCHEMA IF NOT EXISTS planner;

CREATE TABLE IF NOT EXISTS public.appsettings (
    clubcode VARCHAR(20) NOT NULL,
    accommodatie TEXT NULL,
    syncenabled BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE IF NOT EXISTS public.velden (
    veldnummer INTEGER NOT NULL PRIMARY KEY,
    veldnaam VARCHAR(50) NOT NULL,
    actief BOOLEAN NOT NULL DEFAULT TRUE,
    clubcode VARCHAR(20) NOT NULL
);

CREATE TABLE IF NOT EXISTS public.speeltijden (
    leeftijd VARCHAR(10) NOT NULL,
    veldafmeting DECIMAL(4, 2) NOT NULL,
    wedstrijdtotaal INTEGER NOT NULL,
    clubcode VARCHAR(20) NOT NULL,
    PRIMARY KEY (leeftijd, clubcode)
);

CREATE TABLE IF NOT EXISTS planner.geplandewedstrijden (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    datum DATE NOT NULL,
    aanvangstijd TIME NOT NULL,
    eindtijd TIME NOT NULL,
    veldnummer INTEGER NOT NULL,
    velddeelgebruik DECIMAL(4, 2) NOT NULL DEFAULT 1.00,
    leeftijdscategorie VARCHAR(10) NULL,
    teamnaam VARCHAR(100) NULL,
    tegenstander VARCHAR(100) NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Te bevestigen',
    isvervallen BOOLEAN NOT NULL DEFAULT FALSE,
    sportlinkwedstrijdcode BIGINT NULL,
    clubcode VARCHAR(20) NOT NULL
);
