-- 003_admin_tables.sql — Postgres-tegenhangers voor de tabellen die de Admin-endpoints van #887
-- nodig hebben, bovenop de vier uit 001_baseline.sql.
--
-- Conventies (ARCHITECTUUR-DATABASE-TIERS.md §3, ongewijzigd t.o.v. 001/002):
--   dbo -> public, schema's als planner blijven gelijk; alles lowercase en ongequote.
--   NVARCHAR(n) -> VARCHAR(n), NVARCHAR(MAX) -> TEXT, BIT -> BOOLEAN, DATETIME/DATETIME2 -> TIMESTAMPTZ
--   met DEFAULT NOW() (nooit een naive TIMESTAMP — #851's B2).
--
-- dbo.AppSettings had bij 001_baseline.sql alleen de 3 kolommen die de planner-kernview nodig had;
-- deze migratie vult de rest aan via ALTER TABLE zodat AdminSettingsFunction/AdminThemeFunction/
-- AdminSyncFunction er volledig op kunnen draaien.
ALTER TABLE public.appsettings
    ADD COLUMN IF NOT EXISTS clubname VARCHAR(100) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS sportlinkapiurl VARCHAR(100) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS sportlinkclientid VARCHAR(50) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS seasonstartmonth INTEGER NOT NULL DEFAULT 7,
    ADD COLUMN IF NOT EXISTS lastsynctimestamp TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS fetchschedule VARCHAR(50) NOT NULL DEFAULT '0 0 4 * * *',
    ADD COLUMN IF NOT EXISTS plannerafzendernaam VARCHAR(100) NULL,
    ADD COLUMN IF NOT EXISTS coordinatornaam VARCHAR(100) NULL,
    ADD COLUMN IF NOT EXISTS coordinatorfunctie VARCHAR(100) NULL,
    ADD COLUMN IF NOT EXISTS planneremailadres VARCHAR(200) NULL,
    ADD COLUMN IF NOT EXISTS herplandeadlinedagen INTEGER NULL,
    ADD COLUMN IF NOT EXISTS bufferminuten INTEGER NULL,
    ADD COLUMN IF NOT EXISTS emailvoetnoot TEXT NULL,
    ADD COLUMN IF NOT EXISTS accommodatieplaats VARCHAR(100) NULL,
    ADD COLUMN IF NOT EXISTS accommodatielatitude DOUBLE PRECISION NULL,
    ADD COLUMN IF NOT EXISTS accommodatielongitude DOUBLE PRECISION NULL,
    ADD COLUMN IF NOT EXISTS userealtimeapi BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS themecolorprimary VARCHAR(7) NULL,
    ADD COLUMN IF NOT EXISTS themecolorsecondary VARCHAR(7) NULL,
    ADD COLUMN IF NOT EXISTS themecoloraccent VARCHAR(7) NULL,
    ADD COLUMN IF NOT EXISTS themecolortextonprimary VARCHAR(7) NULL,
    ADD COLUMN IF NOT EXISTS themeclubwebsiteurl VARCHAR(300) NULL,
    ADD COLUMN IF NOT EXISTS knvbpdfbijlageingeschakeld BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS knvbstandaardregio VARCHAR(20) NULL,
    ADD COLUMN IF NOT EXISTS appsettingsauditbewaardagen INTEGER NOT NULL DEFAULT 730;

-- dbo.Velden had bij 001_baseline.sql alleen veldnummer/veldnaam/actief/clubcode.
ALTER TABLE public.velden
    ADD COLUMN IF NOT EXISTS veldtype VARCHAR(20) NOT NULL DEFAULT 'kunstgras',
    ADD COLUMN IF NOT EXISTS heeftkunstlicht BOOLEAN NOT NULL DEFAULT FALSE;

-- dbo.Speeltijden had bij 001_baseline.sql alleen leeftijd/veldafmeting/wedstrijdtotaal/clubcode.
ALTER TABLE public.speeltijden
    ADD COLUMN IF NOT EXISTS wedstrijdhelft INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS wedstrijdrust INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS standaardvoorkeurtijd TIME NULL;

-- dbo.Teams — canonieke teamidentiteit (#692, #696). Losstaand van his.teams (ETL-schema).
CREATE TABLE IF NOT EXISTS public.teams (
    teamid INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    clubcode VARCHAR(20) NOT NULL,
    teamnaam VARCHAR(100) NOT NULL,
    teamnaamgenormaliseerd VARCHAR(100) NOT NULL,
    leeftijdscategorie VARCHAR(50) NULL,
    leeftijdnummer INTEGER NULL,
    teamnummer INTEGER NULL,
    bkteams VARCHAR(100) NULL,
    isactief BOOLEAN NOT NULL DEFAULT TRUE,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    mta_modified TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (clubcode, teamnaam),
    UNIQUE (clubcode, teamnaamgenormaliseerd)
);

-- dbo.TeamAliassen (#692, #696, #700).
CREATE TABLE IF NOT EXISTS public.teamaliassen (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    clubcode VARCHAR(20) NOT NULL,
    ruwetekst VARCHAR(200) NOT NULL,
    ruwetekstgenormaliseerd VARCHAR(200) NOT NULL,
    teamid INTEGER NOT NULL REFERENCES public.teams(teamid),
    bron VARCHAR(20) NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'pending',
    aantalkeergebruikt INTEGER NOT NULL DEFAULT 0,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    mta_modified TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (clubcode, ruwetekst)
);
CREATE INDEX IF NOT EXISTS ix_teamaliassen_club_genormaliseerd
    ON public.teamaliassen (clubcode, ruwetekstgenormaliseerd);

-- dbo.TeamRegels
CREATE TABLE IF NOT EXISTS public.teamregels (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    teamnaam VARCHAR(100) NOT NULL,
    regeltype VARCHAR(50) NOT NULL,
    waardeminuten INTEGER NULL,
    waardeveldnummer INTEGER NULL,
    waardetijd TIME NULL,
    prioriteit INTEGER NOT NULL DEFAULT 0,
    actief BOOLEAN NOT NULL DEFAULT TRUE,
    opmerking VARCHAR(500) NULL,
    clubcode VARCHAR(20) NOT NULL
);

-- dbo.TeamVoorkeurTijden
CREATE TABLE IF NOT EXISTS public.teamvoorkeurtijden (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    teamnaam VARCHAR(100) NOT NULL,
    dagvanweek INTEGER NOT NULL,
    voorkeurtijd TIME NOT NULL,
    prioriteit INTEGER NOT NULL DEFAULT 5,
    actief BOOLEAN NOT NULL DEFAULT TRUE,
    clubcode VARCHAR(20) NOT NULL,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    mta_modified TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- dbo.UitgeslotenEmailAdressen
CREATE TABLE IF NOT EXISTS public.uitgeslotenemailadressen (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    emailadres VARCHAR(200) NOT NULL,
    omschrijving VARCHAR(500) NULL,
    actief BOOLEAN NOT NULL DEFAULT TRUE,
    clubcode VARCHAR(20) NOT NULL,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (emailadres, clubcode)
);

-- dbo.VeldPeriode (#581) — moet vóór VeldBeschikbaarheid bestaan (FK).
CREATE TABLE IF NOT EXISTS public.veldperiode (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    naam VARCHAR(50) NOT NULL,
    datumvan DATE NOT NULL,
    datumtot DATE NOT NULL,
    actief BOOLEAN NOT NULL DEFAULT TRUE,
    clubcode VARCHAR(20) NOT NULL,
    CHECK (datumtot >= datumvan)
);

-- dbo.VeldBeschikbaarheid
CREATE TABLE IF NOT EXISTS public.veldbeschikbaarheid (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    veldnummer INTEGER NOT NULL REFERENCES public.velden(veldnummer),
    dagvanweek INTEGER NOT NULL,
    beschikbaarvanaf TIME NOT NULL,
    beschikbaartot TIME NOT NULL,
    gebruikzonsondergang BOOLEAN NOT NULL DEFAULT FALSE,
    periodeid INTEGER NULL REFERENCES public.veldperiode(id),
    clubcode VARCHAR(20) NOT NULL
);

-- dbo.VeldTraining
CREATE TABLE IF NOT EXISTS public.veldtraining (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    veldnummer INTEGER NOT NULL REFERENCES public.velden(veldnummer),
    dagvanweek INTEGER NOT NULL,
    vantijd TIME NOT NULL,
    tottijd TIME NOT NULL,
    omschrijving VARCHAR(100) NULL,
    actief BOOLEAN NOT NULL DEFAULT TRUE,
    clubcode VARCHAR(20) NOT NULL
);

-- dbo.EmailTemplateInstellingen
CREATE TABLE IF NOT EXISTS public.emailtemplateinstellingen (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    templatekey VARCHAR(100) NOT NULL,
    onderwerp VARCHAR(500) NOT NULL,
    bodytemplate TEXT NOT NULL,
    actief BOOLEAN NOT NULL DEFAULT TRUE,
    clubcode VARCHAR(20) NOT NULL,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    mta_modified TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (templatekey, clubcode)
);

-- planner.EmailVerwerking (log + AI-verwerkingsstatusmachine)
CREATE TABLE IF NOT EXISTS planner.emailverwerking (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    messageid VARCHAR(500) NOT NULL UNIQUE,
    conversationid VARCHAR(500) NULL,
    afzender VARCHAR(200) NOT NULL,
    onderwerp VARCHAR(500) NOT NULL,
    ontvangstdatum TIMESTAMPTZ NOT NULL,
    emailbody TEXT NULL,
    verzoektype VARCHAR(50) NOT NULL,
    geextraheerdedata TEXT NULL,
    plannerresponse TEXT NULL,
    antwoordemail TEXT NULL,
    verstuurdnaar VARCHAR(1000) NULL,
    isbeantwoord BOOLEAN NOT NULL DEFAULT FALSE,
    verzendpogingoputc TIMESTAMPTZ NULL,
    status VARCHAR(30) NOT NULL DEFAULT 'Ontvangen',
    foutmelding VARCHAR(1000) NULL,
    isreplyoponsantwoord BOOLEAN NULL,
    replyopverwerkingid INTEGER NULL,
    pogingen INTEGER NOT NULL DEFAULT 0,
    clubcode VARCHAR(20) NOT NULL CHECK (LENGTH(clubcode) > 0),
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    mta_modified TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- planner.ClassificatieCorrectie ("leermomenten" — AdminLeermomentenFunction)
CREATE TABLE IF NOT EXISTS planner.classificatiecorrectie (
    id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    origineleverwerkingid INTEGER NOT NULL REFERENCES planner.emailverwerking(id),
    correctionverwerkingid INTEGER NOT NULL REFERENCES planner.emailverwerking(id),
    origineelverzoektype VARCHAR(50) NOT NULL,
    afgeleidjuisttype VARCHAR(50) NULL,
    originelesamenvatting VARCHAR(500) NULL,
    correctiesamenvatting VARCHAR(500) NULL,
    isgevalideerd BOOLEAN NOT NULL DEFAULT FALSE,
    isafgewezen BOOLEAN NOT NULL DEFAULT FALSE,
    clubcode VARCHAR(20) NOT NULL,
    mta_inserted TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    mta_modified TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (origineleverwerkingid, correctionverwerkingid)
);
