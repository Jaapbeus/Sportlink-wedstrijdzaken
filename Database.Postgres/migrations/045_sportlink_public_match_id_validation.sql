-- Only the new unique day+number lookup writes this witness. Existing and legacy-written mappings
-- remain NULL or become mismatched if a legacy writer changes publicmatchid.
ALTER TABLE public.sportlinkpublicmatchidcache
    ADD COLUMN IF NOT EXISTS verifiedpublicmatchid TEXT NULL;
