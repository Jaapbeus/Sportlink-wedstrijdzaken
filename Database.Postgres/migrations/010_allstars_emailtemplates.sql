-- 010_allstars_emailtemplates.sql — twee demo-e-mailsjablonen voor AllStars FC (#911).
--
-- WAAROM EEN NIEUW BESTAND EN GEEN AANVULLING OP 006_allstars_demodata.sql:
-- MigrationRunner legt van elk toegepast bestand de SHA-256 vast en faalt hard zodra een reeds
-- toegepast bestand later een andere checksum heeft ("Migratie '...' is al toegepast met een andere
-- checksum"). Migratie 006 aanpassen zou dus élke bestaande installatie bij de volgende deploy laten
-- omvallen. Nieuwe rijen horen in een nieuw migratiebestand — dat is precies waar die guard voor is.
--
-- WAAROM DEZE RIJEN BESTAAN:
-- GET /api/beheer/templates leest uitsluitend deze tabel en voegt geen standaardteksten uit code
-- toe. Op een verse database gaf het endpoint daardoor een lege lijst, op BEIDE tiers — geen
-- Postgres-regressie, maar wel een blinde vlek: de zelftest (#909, poort G6) kon twee heel
-- verschillende situaties niet uit elkaar houden, want ze zien er allebei uit als '[]':
--   1. de seed levert (terecht) geen sjablonen, en
--   2. de sjabloonquery valt stil door een kolom-/casingfout — het defect dat #855 elders wél
--      opleverde.
-- De assertie stond daarom op 'blocked'. Met deze rijen meet ze weer iets.
--
-- De teksten zijn letterlijk de hardcoded standaarden uit BlazorAdmin/Pages/EmailTemplates.razor
-- (OnTemplateKeyChange), zodat de demodata toont wat een beheerder bij 'Terugzetten naar standaard'
-- ook krijgt — niet een derde, afwijkende variant. Dezelfde twee sleutels en teksten staan in het
-- #911-blok van Database/Script.PostDeployment1.sql, zodat de assertie op beide tiers hetzelfde
-- meet; dat is de hele reden dat dit issue bestond.
--
-- AVG: geen persoonsgegevens — uitsluitend sjabloontekst met {{placeholders}}. Alleen rijen met
-- clubcode = 'ALLSTARS', conform de vastgelegde uitzondering in CLAUDE.md.
--
-- Identifier-casing: lowercase, ongequote — conform docs/ARCHITECTUUR-DATABASE-TIERS.md §3.

DO $$
DECLARE
    demo_club CONSTANT VARCHAR(20) := 'ALLSTARS';
BEGIN
    -- Alleen seeden als de democlub bestaat — een fork die hem weghaalt houdt een schone database.
    IF EXISTS (SELECT 1 FROM public.appsettings WHERE clubcode = demo_club) THEN

        -- Idempotent per sleutel: een sjabloon dat een beheerder zelf heeft aangepast wordt niet
        -- overschreven. Dat is bewust anders dan een ON CONFLICT DO UPDATE zou doen.
        INSERT INTO public.emailtemplateinstellingen (templatekey, onderwerp, bodytemplate, actief, clubcode)
        SELECT bron.templatekey, bron.onderwerp, bron.bodytemplate, bron.actief, bron.clubcode
        FROM (VALUES
            ('bevestiging',
             'Bevestiging wedstrijd {{datum}} — {{team}} vs {{tegenstander}}',
             E'Beste {{aanhef}},\n\nHierbij bevestigen wij de wedstrijd op {{datum}} om {{aanvangstijd}}.\n\nThuisteam: {{team}}\nTegenstander: {{tegenstander}}\n\nTot dan!',
             TRUE, demo_club),
            ('buiten_scope',
             'Uw bericht ontvangen',
             E'Beste {{voornaam}},\n\nBedankt voor uw bericht. Uw vraag valt buiten het bereik van de automatische verwerking. Neem contact op met de club voor verdere hulp.',
             TRUE, demo_club)
        ) AS bron(templatekey, onderwerp, bodytemplate, actief, clubcode)
        WHERE NOT EXISTS (
            SELECT 1 FROM public.emailtemplateinstellingen t
            WHERE t.clubcode = bron.clubcode AND t.templatekey = bron.templatekey
        );

    END IF;
END $$;
