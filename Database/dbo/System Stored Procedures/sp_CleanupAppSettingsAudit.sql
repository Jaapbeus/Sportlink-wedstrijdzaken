-- #781 (AVG artikel 5 lid 1 sub e — opslagbeperking).
--
-- dbo.AppSettingsAudit logt elke instellingenwijziging zonder bewaartermijn. Twee kolommen zijn
-- persoonsgegevens: [GewijzigdDoor] (Entra-gebruikersnaam/UPN van de beheerder) en
-- [OudeWaarde]/[NieuweWaarde] (kunnen e-mailadressen bevatten, bijv. bij GraphMailbox of
-- EmailReviewRecipient).
--
-- BEWAARTERMIJN — UITGANGSPUNT, GEEN DEFINITIEF BELEID:
-- 730 dagen (24 maanden) is gekozen als gangbare, proportionele bewaartermijn voor een
-- wijzigingenlog van beheerinstellingen (traceerbaarheid van configuratiewijzigingen is het
-- legitieme doel). Dit is een aanname — de daadwerkelijke bewaartermijn is een beleidskeuze van de
-- repo-eigenaar. De waarde staat configureerbaar in [dbo].[AppSettings].[AppSettingsAuditBewaarDagen]
-- (default 730) zodat de eigenaar dit kan aanpassen zonder redeploy.
--
-- ENKELE FASE (bewust géén anonimiseer-fase zoals bij planner.EmailVerwerking/avg.ImportLog):
-- het doel van dit auditlog IS "wie heeft wat gewijzigd" — GewijzigdDoor halverwege de
-- bewaartermijn anonimiseren zou die traceerbaarheid ondermijnen zonder het AVG-risico wezenlijk
-- te verkleinen (de tabel is toch al alleen inzichtelijk voor beheerders via SQL, geen publieke
-- blootstelling). Een enkele DELETE na de volledige bewaartermijn is daarom proportioneler en
-- eenvoudiger te verifiëren dan een tweetraps-aanpak.
--
-- Er wordt bewust NIET per ClubCode gefilterd: dit is een deployment-brede technische
-- retentie-instelling, geen per-club-instelling (vgl. het deployment-model: één productieclub +
-- AllStars FC-democlub per fork, zie CLAUDE.md).
CREATE PROCEDURE [dbo].[sp_CleanupAppSettingsAudit]
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @BewaarDagen INT;

    -- Primaire club (niet de ALLSTARS-democlub) is leidend voor deployment-brede instellingen,
    -- zelfde patroon als elders in Script.PostDeployment1.sql (#598/#740).
    SELECT TOP 1 @BewaarDagen = [AppSettingsAuditBewaarDagen]
    FROM [dbo].[AppSettings]
    WHERE [ClubCode] <> 'ALLSTARS'
    ORDER BY [ClubCode];

    -- Vangnet: alleen de democlub aanwezig (bijv. een verse fork vóór de eerste echte
    -- instellingen-configuratie), of de kolom bevat NULL door een pre-migratie rij.
    IF @BewaarDagen IS NULL
        SELECT TOP 1 @BewaarDagen = [AppSettingsAuditBewaarDagen]
        FROM [dbo].[AppSettings]
        ORDER BY [ClubCode];

    -- Kolom ontbreekt (nog niet gemigreerd) of bevat een onzinnige waarde: val terug op de
    -- gedocumenteerde default in plaats van nooit op te ruimen.
    IF @BewaarDagen IS NULL OR @BewaarDagen <= 0
        SET @BewaarDagen = 730;

    DELETE FROM [dbo].[AppSettingsAudit]
    WHERE [Tijdstip] < DATEADD(DAY, -@BewaarDagen, GETUTCDATE());
END;
