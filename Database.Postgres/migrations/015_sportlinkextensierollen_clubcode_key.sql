-- 015_sportlinkextensierollen_clubcode_key.sql  (#1062, corrigeert de aanpak van #1058)
--
-- WAAROM DIT EEN NIEUW BESTAND IS EN GEEN WIJZIGING VAN 012
-- #1058 paste de sleutel van public.sportlinkextensierollen aan door 012_sportlink_extension.sql
-- achteraf te bewerken. MigrationRunner legt per migratiebestand een SHA-256 vast en weigert een
-- bestand dat al is toegepast maar sindsdien gewijzigd — terecht, want de eerder toegepaste versie
-- valt niet met terugwerkende kracht te veranderen. Gevolg: op elke database waarop 012 al had
-- gedraaid (productie inbegrepen) stopte de runner daar, nog vóór 013 en 014 aan de beurt kwamen.
-- 012 is daarom teruggezet naar zijn oorspronkelijke inhoud en de wijziging staat hier.
--
-- WAT DEZE MIGRATIE DOET
-- De primaire sleutel gaat van (rolnaam) naar (rolnaam, clubcode). Reden ongewijzigd t.o.v. #1058:
-- dit schema draait altijd met minstens twee clubs (de echte club + de AllStars FC-demo, zie
-- CLAUDE.md "Deployment-model"), en elke club registreert zijn eigen koppeling voor dezelfde
-- rolnaam (bijvoorbeeld 'Wedstrijdzaken'). Zelfde patroon als public.sportlinkservicetokens (014).
--
-- Idempotent en veilig op beide uitgangssituaties:
--   * een database waarop 012 in de ÓUDE vorm draaide  -> de enkelvoudige sleutel wordt vervangen;
--   * een verse database waarop 012 in de NIEUWE vorm draaide (tussen #1058 en deze fix) -> de
--     samengestelde sleutel staat er al en er gebeurt niets.

DO $$
DECLARE
    huidige_sleutel TEXT;
    sleutel_kolommen TEXT;
BEGIN
    SELECT con.conname,
           (SELECT string_agg(att.attname, ',' ORDER BY att.attname)
            FROM unnest(con.conkey) AS k(attnum)
            JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = k.attnum)
      INTO huidige_sleutel, sleutel_kolommen
    FROM pg_constraint con
    WHERE con.conrelid = 'public.sportlinkextensierollen'::regclass
      AND con.contype = 'p';

    IF huidige_sleutel IS NULL THEN
        ALTER TABLE public.sportlinkextensierollen
            ADD PRIMARY KEY (rolnaam, clubcode);
        RAISE NOTICE 'Primaire sleutel (rolnaam, clubcode) toegevoegd.';

    ELSIF sleutel_kolommen = 'clubcode,rolnaam' THEN
        RAISE NOTICE 'Primaire sleutel staat al op (rolnaam, clubcode) — niets gedaan.';

    ELSE
        -- Van de enkelvoudige sleutel naar de samengestelde. Een rij met NULL in clubcode kan niet
        -- bestaan: de kolom is NOT NULL sinds 012.
        EXECUTE format('ALTER TABLE public.sportlinkextensierollen DROP CONSTRAINT %I', huidige_sleutel);
        ALTER TABLE public.sportlinkextensierollen
            ADD PRIMARY KEY (rolnaam, clubcode);
        RAISE NOTICE 'Primaire sleutel omgezet van (%) naar (rolnaam, clubcode).', sleutel_kolommen;
    END IF;
END $$;
