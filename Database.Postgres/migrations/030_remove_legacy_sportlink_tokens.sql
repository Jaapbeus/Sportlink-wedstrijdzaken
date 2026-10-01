-- #1411: the encrypted auto-login store is the only supported Sportlink credential/token store.
-- Legacy refresh tokens are no longer read or accepted by the application.
DROP TABLE IF EXISTS public.sportlinkservicetokens;
