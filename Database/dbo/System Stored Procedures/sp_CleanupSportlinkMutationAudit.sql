-- #1114 (AVG artikel 5 lid 1 sub e — opslagbeperking), epic #986.
--
-- dbo.SportlinkMutationAudit (#991/#998) legt bij elke Sportlink-mutatiepoging een rij vast,
-- inclusief [TriggerdDoor]: het e-mailadres/UPN van de beheerder die de actie triggerde — een
-- persoonsgegeven. Anders dan dbo.AppSettingsAudit (sp_CleanupAppSettingsAudit, #781) had deze tabel
-- geen bewaartermijn: rijen bleven voor altijd staan. Tijdens #998 gesignaleerd als apart issue.
--
-- BEWAARTERMIJN — UITGANGSPUNT, GEEN DEFINITIEF BELEID:
-- 365 dagen (één seizoen plus marge). Het doel van dit log is operationele traceerbaarheid van
-- wijzigingen die deze app in Sportlink doorvoert; korter dan AppSettingsAudit (730) omdat dit log
-- per mutatie groeit en Sportlinks eigen log de mutatie zelf óók bewaart. De eigenaar (DPO-rol)
-- stelt de definitieve termijn vast in [dbo].[AppSettings].[SportlinkMutationAuditBewaarDagen] —
-- geen redeploy nodig.
--
-- ENKELE FASE, geen anonimiseer-fase — zelfde redenering als sp_CleanupAppSettingsAudit: het doel
-- van dit log IS "wie heeft wat gewijzigd". Een rij met Resultaat 'Pending' (mutatie nog onderweg)
-- is nooit ouder dan seconden, dus de leeftijdsgrens raakt geen actieve rij.
--
-- Deployment-brede instelling, bewust niet per ClubCode gefilterd (één productieclub + AllStars FC
-- per fork, zie CLAUDE.md).
CREATE PROCEDURE [dbo].[sp_CleanupSportlinkMutationAudit]
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @BewaarDagen INT;

    -- Primaire club (niet de ALLSTARS-democlub) is leidend voor deployment-brede instellingen,
    -- zelfde patroon als sp_CleanupAppSettingsAudit (#598/#740).
    SELECT TOP 1 @BewaarDagen = [SportlinkMutationAuditBewaarDagen]
    FROM [dbo].[AppSettings]
    WHERE [ClubCode] <> 'ALLSTARS'
    ORDER BY [ClubCode];

    -- Vangnet: alleen de democlub aanwezig, of de kolom bevat NULL door een pre-migratie rij.
    IF @BewaarDagen IS NULL
        SELECT TOP 1 @BewaarDagen = [SportlinkMutationAuditBewaarDagen]
        FROM [dbo].[AppSettings]
        ORDER BY [ClubCode];

    -- Ontbrekende of onzinnige waarde: val terug op de gedocumenteerde default in plaats van nooit
    -- op te ruimen — een configuratiefout mag niet stilzwijgend in een AVG-overtreding ontaarden.
    IF @BewaarDagen IS NULL OR @BewaarDagen <= 0
        SET @BewaarDagen = 365;

    DELETE FROM [dbo].[SportlinkMutationAudit]
    WHERE [Tijdstip] < DATEADD(DAY, -@BewaarDagen, GETUTCDATE());
END;
