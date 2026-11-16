-- #1977: één bron per club. De import is bewust een aparte, handmatige stap.
ALTER TABLE public.appsettings
    ADD COLUMN IF NOT EXISTS emailverwerkingingeschakeld boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS emailreviewmodusactief boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS emailreviewontvanger varchar(320) NULL,
    ADD COLUMN IF NOT EXISTS emailinstellingengeimporteerd boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS emailinstellingenbeheerdergewijzigd boolean NOT NULL DEFAULT false;
