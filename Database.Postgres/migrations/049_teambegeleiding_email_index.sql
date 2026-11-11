-- 049_teambegeleiding_email_index.sql: expressie-index voor de afzender-opzoeking (#1862).
--
-- De opzoeking "bij welke teams staat dit e-mailadres?" (AfzenderTeamBron) vergelijkt
-- LOWER(TRIM(emailadres)) = @adres, zodat hoofdletters en spaties in het adres niet uitmaken. Zonder een index op
-- diezelfde uitdrukking is dat een sequentiele scan over de hele begeleidingslijst (architectuurinvariant:
-- LOWER()-vergelijking vraagt een expressie-index). SQL Server-tegenhanger: persisted computed column
-- [EmailadresGenormaliseerd] met index in Database/Script.PostDeployment1.sql en Database/avg/Tables/Teambegeleiding.sql.
--
-- Zuiver additief en niet uniek: bestaande code en gegevens blijven ongewijzigd. Geen nieuwe tabel, dus geen nieuwe
-- RLS-plicht (avg.teambegeleiding heeft RLS al, migratie 021). Het adres blijft persoonsgegeven: de index bevat
-- alleen de genormaliseerde waarde die de tabel al bevat, en de bestaande opruiming (CleanupTeambegeleiding)
-- verwijdert de rijen en daarmee de indexregels.
-- Volgnummer 049: 048 is al in gebruik door een andere openstaande migratie.
-- Gewone CREATE INDEX (CONCURRENTLY kan niet in een migratietransactie); de tabel is klein.
CREATE INDEX IF NOT EXISTS ix_teambegeleiding_email_lower
    ON avg.teambegeleiding (LOWER(TRIM(emailadres)));
