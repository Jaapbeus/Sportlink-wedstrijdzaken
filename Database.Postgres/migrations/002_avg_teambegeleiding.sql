-- 002_avg_teambegeleiding.sql — Postgres-equivalent van avg.Teambegeleiding/avg.ImportLog (#824).
--
-- Bevat AVG/GDPR-persoonsgegevens (contactgegevens van teambegeleiders) zodra een club deze tabel
-- daadwerkelijk vult — dit migratiebestand zelf bevat en zal nooit voorbeelddata bevatten, alleen
-- structuur. Beperk SELECT-rechten in productie tot bevoegde gebruikers en rollen (zelfde regel als
-- de SQL Server-tegenhanger).
--
-- mta_imported/importdatum zijn bewust TIMESTAMPTZ, niet TIMESTAMP — zelfde les als #821's
-- MigrationRunner (#851 vond dat een naïeve TIMESTAMP + NOW() de sessietijdzone gebruikt).

CREATE SCHEMA IF NOT EXISTS avg;

CREATE TABLE IF NOT EXISTS avg.teambegeleiding (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    team VARCHAR(100) NULL,
    leeftijdscategorieteam VARCHAR(50) NULL,
    teamrol VARCHAR(100) NULL,
    naam VARCHAR(300) NULL,
    emailadres VARCHAR(200) NULL,
    telefoonnummer VARCHAR(50) NULL,
    mta_imported TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    clubcode VARCHAR(20) NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS avg.importlog (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    importdatum TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    aantalrijen INTEGER NOT NULL,
    csvbestand VARCHAR(500) NULL,
    importerendedoor VARCHAR(200) NULL,
    duur_ms INTEGER NULL,
    clubcode VARCHAR(20) NOT NULL DEFAULT ''
);
