-- Triviale query die uitsluitend dient om een echte SQL-login uit te voeren.
-- Een login is wat Azure SQL Serverless auto-resume triggert; een kale TCP-verbinding
-- wordt door de gateway afgehandeld en bereikt de database nooit. Zie #624.
SELECT 1;
