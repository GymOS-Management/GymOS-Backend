-- Cluster-level setup, run once against an empty data directory.
--
-- Deliberately minimal. Every extension is code running inside the database with high privilege,
-- so each one earns its place by being needed — and nothing in GymOS needs one yet:
--
--   * uuid-ossp / pgcrypto — not needed. Ids are client-assigned Guids
--     (GymOSModelConventions.ApplyClientAssignedKeys), so the database never generates one, and
--     hashing happens in the application with Argon2id (AD-4).
--   * citext — considered and rejected for gym slugs and plan codes. Lowercase-by-construction
--     plus a unique index and a CHECK constraint gives the same guarantee without requiring an
--     extension to be present in every environment, including managed ones. See
--     docs/BusinessRules/Gyms.md.
--   * pg_stat_statements — genuinely useful, but it needs shared_preload_libraries and a server
--     restart, so it belongs with the observability work (AD-38) rather than being switched on
--     speculatively here.
--
-- This file exists so that the mechanism is present, tested, and documented before something
-- actually needs it — adding an extension later should be one reviewed line, not a discovery that
-- init scripts do not re-run on an existing volume.

-- A timestamp in the server log, so `docker compose logs postgres` shows unambiguously that
-- initialisation ran and when. Harmless, and it makes a first-boot problem easy to place.
DO $$
BEGIN
    RAISE NOTICE 'GymOS: initdb scripts applied at % (UTC)', now() AT TIME ZONE 'UTC';
END
$$;
