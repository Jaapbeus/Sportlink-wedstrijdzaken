-- 004_appsettingsaudit.sql — Postgres-tegenhanger van dbo.AppSettingsAudit (#887's
-- AdminTemplatesFunction schrijft hier direct naar, zonder tussenliggende repository).
--
-- #781 (AVG art. 5 lid 1 sub e, bewaartermijn): de opschoning zelf (sp_CleanupAppSettingsAudit) is
-- een van de "resterende procedures" uit #861 — deze migratie levert alleen de tabel.

CREATE TABLE IF NOT EXISTS public.appsettingsaudit (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tijdstip TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    gewijzigddoor VARCHAR(100) NOT NULL,
    veld VARCHAR(100) NOT NULL,
    oudewaarde TEXT NULL,
    nieuwewaarde TEXT NULL,
    clubcode VARCHAR(20) NOT NULL
);
