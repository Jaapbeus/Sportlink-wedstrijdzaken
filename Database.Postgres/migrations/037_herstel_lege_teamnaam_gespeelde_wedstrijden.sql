-- #1561: herstel van de lege teamnaam bij gespeelde wedstrijden.
--
-- Een gespeelde wedstrijd komt alleen nog via /uitslagen in stg, zonder teamnaam. Tot #1547 overschreef
-- de upsert daarmee de complete his-rij; #1547 stopte dat en migratie 036 herstelde kaledatum en veld,
-- maar niet de teamnaam. Die bleef leeg (in productie 347 rijen), dus de kolom Team op /planning was
-- voor afgelopen speeldagen leeg en een verse sync herstelt dat niet: wat Sportlink niet meer levert,
-- wordt nooit meer aangevuld.
--
-- De eigen teamnaam is af te leiden: het is het team van de wedstrijd waarvan de club-relatiecode die
-- van de eigen club is. Die code is per club niet bekend in de code (geen club-specifieke waarden in
-- de broncode), maar wel in de data: de relatiecode die bij de al gevulde rijen hoort bij het team
-- dat als teamnaam staat. Alleen een eenduidige afleiding wordt toegepast:
--   - minstens één van beide teams draagt de eigen relatiecode. Bij een onderlinge wedstrijd van twee
--     eigen teams is het thuisteam de teamnaam: dat is wat /programma zelf doet (gemeten op
--     toekomstige onderlinge wedstrijden);
--   - een club zonder gevulde rijen of zonder relatiecodes (bijvoorbeeld de democlub) blijft ongemoeid.
-- Alleen rijen met een lege teamnaam worden aangeraakt; idempotent. his.* bestaat pas na de eerste sync.
DO $$
BEGIN
    IF to_regclass('his.matches') IS NOT NULL THEN
        UPDATE his.matches m
        SET teamnaam = CASE WHEN COALESCE(m.thuisteamclubrelatiecode, '') = e.code
                            THEN m.thuisteam ELSE m.uitteam END
        FROM (
            SELECT DISTINCT ON (clubcode) clubcode, code
            FROM (
                SELECT clubcode, code, COUNT(*) AS aantal
                FROM (
                    SELECT clubcode, thuisteamclubrelatiecode AS code
                    FROM his.matches
                    WHERE COALESCE(teamnaam, '') <> '' AND teamnaam = thuisteam
                      AND COALESCE(thuisteamclubrelatiecode, '') <> ''
                    UNION ALL
                    SELECT clubcode, uitteamclubrelatiecode
                    FROM his.matches
                    WHERE COALESCE(teamnaam, '') <> '' AND teamnaam = uitteam
                      AND COALESCE(uitteamclubrelatiecode, '') <> ''
                ) gevuld
                GROUP BY clubcode, code
            ) geteld
            ORDER BY clubcode, aantal DESC
        ) e
        WHERE m.clubcode = e.clubcode
          AND COALESCE(m.teamnaam, '') = ''
          AND (COALESCE(m.thuisteamclubrelatiecode, '') = e.code
               OR COALESCE(m.uitteamclubrelatiecode, '') = e.code)
          AND COALESCE(CASE WHEN COALESCE(m.thuisteamclubrelatiecode, '') = e.code
                            THEN m.thuisteam ELSE m.uitteam END, '') <> '';
    END IF;
END
$$;
