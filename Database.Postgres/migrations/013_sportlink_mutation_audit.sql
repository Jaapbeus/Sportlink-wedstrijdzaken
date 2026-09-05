-- Sportlink mutation audit log (#991, #998)
CREATE TABLE IF NOT EXISTS public.sportlinkmutationaudit (
    id                      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    clubcode                VARCHAR(20)  NOT NULL,
    functionelerol          VARCHAR(50)  NOT NULL,
    triggerddoor            VARCHAR(200) NOT NULL,
    publicmatchid           VARCHAR(50)  NOT NULL,
    actie                   VARCHAR(100) NOT NULL,
    waardevoor              TEXT NULL,
    waardena                TEXT NULL,
    resultaat               VARCHAR(20)  NOT NULL DEFAULT 'Pending',
    foutmeldingsamenvatting VARCHAR(500) NULL,
    correlationid           VARCHAR(50)  NULL,
    tijdstip                TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_sportlinkmutationaudit_clubcode_tijdstip ON public.sportlinkmutationaudit (clubcode, tijdstip DESC);
CREATE INDEX IF NOT EXISTS ix_sportlinkmutationaudit_publicmatchid ON public.sportlinkmutationaudit (publicmatchid);
