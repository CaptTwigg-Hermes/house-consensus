DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name='postgis') THEN
    CREATE EXTENSION IF NOT EXISTS postgis;
  END IF;
END $$;
DO $$ BEGIN CREATE TYPE listing_state AS ENUM ('active','filter_rejected','ai_rejected','manually_rejected','restored','archived'); EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE TYPE override_action AS ENUM ('restore','reject'); EXCEPTION WHEN duplicate_object THEN NULL; END $$;
-- Core tables match the application EF migration. CREATE IF NOT EXISTS only bootstraps isolated exporter tests;
-- production runs application migrations first.
CREATE TABLE IF NOT EXISTS listings (
    "Id" uuid PRIMARY KEY, "ExternalId" text NOT NULL, "Address" text NOT NULL,
    "City" text, "Price" numeric(14,2), "FamilyFitScore" double precision,
    "State" listing_state NOT NULL, "AiAssessed" boolean NOT NULL,
    "AiConfidence" double precision, "AiEvidence" text, "ModelVersion" text,
    "RuleVersion" text, "SourceUrl" text, "ImportedAt" timestamptz NOT NULL,
    "ArchivedAt" timestamptz, "PreviewImageUrl" text, "LivingArea" integer,
    "LotArea" integer, "Rooms" integer, "YearBuilt" integer, "Bathrooms" integer,
    "Bedrooms" integer, "Floors" integer, "EnergyLabel" text, "Quiet" boolean,
    "RoadNoiseDb" double precision, "RailNoiseDb" double precision, "AirNoiseDb" double precision,
    "RoadNoiseStatus" character varying(20), "RoadNoiseLnightDb" double precision, "RoadNoiseLnightStatus" character varying(20),
    "RailNoiseStatus" character varying(20), "RailNoiseLnightDb" double precision, "RailNoiseLnightStatus" character varying(20),
    "AirNoiseStatus" character varying(20), "AirNoiseLnightDb" double precision, "AirNoiseLnightStatus" character varying(20),
    "BuildableHeadroom" integer, "GroundFloorBedroom" boolean,
    "SeparateEntrance" boolean, "SecondKitchen" boolean, "PrivacyScore" integer,
    "FamilyPrivacyScore" double precision, "KidsSpaceScore" double precision,
    "GardenScore" double precision, "SharedLivingScore" double precision,
    "PracticalScore" double precision, "FamilyPrivacyWeight" double precision,
    "KidsSpaceWeight" double precision, "GardenWeight" double precision,
    "SharedLivingWeight" double precision, "PracticalWeight" double precision,
    "ScoreRuleVersion" text, "ScoreCoveragePct" double precision,
    "FamilyPrivacyAvailable" boolean, "ScoreNotesJson" text,
    "Latitude" double precision, "Longitude" double precision,
    "MonthlyExpense" integer, "DaysOnMarket" integer, "CommuteMinutes" integer, "CommuteJson" text,
    "BuildableStatus" text, "Condition" text, "GardenOrientation" text, "MultigenFit" text,
    "PostalCode" text, "Preferred" boolean, "IsNew" boolean, "FirstSeenAt" timestamptz,
    "FamilyUnits" text, "LearningRuleVersion" text
);
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "PreviewImageUrl" text;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "LivingArea" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "LotArea" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Rooms" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "YearBuilt" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Bathrooms" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Bedrooms" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Floors" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "EnergyLabel" text;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Quiet" boolean;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RoadNoiseDb" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RailNoiseDb" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "AirNoiseDb" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RoadNoiseStatus" character varying(20);
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RoadNoiseLnightDb" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RoadNoiseLnightStatus" character varying(20);
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RailNoiseStatus" character varying(20);
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RailNoiseLnightDb" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RailNoiseLnightStatus" character varying(20);
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "AirNoiseStatus" character varying(20);
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "AirNoiseLnightDb" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "AirNoiseLnightStatus" character varying(20);
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='CK_listings_noise_statuses') THEN
    ALTER TABLE listings ADD CONSTRAINT "CK_listings_noise_statuses" CHECK (
      ("RoadNoiseStatus" IS NULL OR "RoadNoiseStatus" IN ('covered','no_contour','unavailable','stale','error')) AND
      ("RoadNoiseLnightStatus" IS NULL OR "RoadNoiseLnightStatus" IN ('covered','no_contour','unavailable','stale','error')) AND
      ("RailNoiseStatus" IS NULL OR "RailNoiseStatus" IN ('covered','no_contour','unavailable','stale','error')) AND
      ("RailNoiseLnightStatus" IS NULL OR "RailNoiseLnightStatus" IN ('covered','no_contour','unavailable','stale','error')) AND
      ("AirNoiseStatus" IS NULL OR "AirNoiseStatus" IN ('covered','no_contour','unavailable','stale','error')) AND
      ("AirNoiseLnightStatus" IS NULL OR "AirNoiseLnightStatus" IN ('covered','no_contour','unavailable','stale','error'))
    );
  END IF;
END $$;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "BuildableHeadroom" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "GroundFloorBedroom" boolean;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "SeparateEntrance" boolean;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "SecondKitchen" boolean;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "PrivacyScore" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "FamilyPrivacyScore" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "KidsSpaceScore" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "GardenScore" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "SharedLivingScore" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "PracticalScore" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "FamilyPrivacyWeight" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "KidsSpaceWeight" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "GardenWeight" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "SharedLivingWeight" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "PracticalWeight" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ScoreRuleVersion" text;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ScoreCoveragePct" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "FamilyPrivacyAvailable" boolean;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ScoreNotesJson" text;
ALTER TABLE listings ALTER COLUMN "FamilyFitScore" DROP NOT NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "CanonicalUrl" character varying(2048);
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "NormalizedAddress" character varying(500);
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "IsManuallyAdded" boolean NOT NULL DEFAULT false;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ManuallyAddedById" uuid;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ManuallyAddedAt" timestamptz;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ManualLifecycleProtected" boolean NOT NULL DEFAULT false;
-- The isolated exporter-test bootstrap has no members table. Production FK/index ownership
-- remains in the application migration 202607310002_AddManualListingsAndGuidedVoting.
WITH normalized AS (
  SELECT "Id", replace(lower(regexp_replace(btrim("Address"), '\s+', ' ', 'g')), ' ,', ',') AS value,
         row_number() OVER (PARTITION BY replace(lower(regexp_replace(btrim("Address"), '\s+', ' ', 'g')), ' ,', ',') ORDER BY "ImportedAt", "Id") AS rank
  FROM listings WHERE "NormalizedAddress" IS NULL
)
UPDATE listings l SET "NormalizedAddress"=n.value FROM normalized n
WHERE l."Id"=n."Id" AND n.rank=1 AND length(n.value) <= 500
  AND NOT EXISTS (SELECT 1 FROM listings occupied WHERE occupied."NormalizedAddress"=n.value);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_listings_CanonicalUrl" ON listings ("CanonicalUrl") WHERE "CanonicalUrl" IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_listings_NormalizedAddress" ON listings ("NormalizedAddress") WHERE "NormalizedAddress" IS NOT NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Latitude" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Longitude" double precision;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "MonthlyExpense" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "DaysOnMarket" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "CommuteMinutes" integer;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "CommuteJson" text;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "BuildableStatus" text;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Condition" text;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "GardenOrientation" text;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "MultigenFit" text;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "LearningRuleVersion" text;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "PostalCode" text;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Preferred" boolean;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "IsNew" boolean;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "FirstSeenAt" timestamptz;
UPDATE listings SET "FirstSeenAt"=COALESCE("FirstSeenAt","ImportedAt" - interval '120 hours');
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM pg_extension WHERE extname='postgis') THEN
    EXECUTE 'ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Location" geometry(Point,4326)';
    EXECUTE 'CREATE INDEX IF NOT EXISTS "IX_listings_Location" ON listings USING gist ("Location")';
    EXECUTE 'UPDATE listings SET "Location"=ST_SetSRID(ST_MakePoint("Longitude","Latitude"),4326) WHERE "Latitude" BETWEEN -90 AND 90 AND "Longitude" BETWEEN -180 AND 180';
  END IF;
END $$;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "FamilyUnits" text;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='CK_listings_family_score_contract') THEN
    ALTER TABLE listings ADD CONSTRAINT "CK_listings_family_score_contract" CHECK (
      ("FamilyFitScore" IS NULL OR "FamilyFitScore" BETWEEN 0 AND 100) AND
      ("PrivacyScore" IS NULL OR "PrivacyScore" BETWEEN 1 AND 5) AND
      ("FamilyPrivacyScore" IS NULL OR "FamilyPrivacyScore" BETWEEN 0 AND 100) AND
      ("KidsSpaceScore" IS NULL OR "KidsSpaceScore" BETWEEN 0 AND 100) AND
      ("GardenScore" IS NULL OR "GardenScore" BETWEEN 0 AND 100) AND
      ("SharedLivingScore" IS NULL OR "SharedLivingScore" BETWEEN 0 AND 100) AND
      ("PracticalScore" IS NULL OR "PracticalScore" BETWEEN 0 AND 100) AND
      ("ScoreCoveragePct" IS NULL OR "ScoreCoveragePct" BETWEEN 0 AND 100)
    );
  END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_listings_ExternalId" ON listings ("ExternalId");
CREATE TABLE IF NOT EXISTS listing_overrides (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    "ListingId" uuid NOT NULL REFERENCES listings("Id") ON DELETE RESTRICT,
    "OwnerId" uuid NOT NULL, "Action" override_action NOT NULL,
    "Reason" text, "CreatedAt" timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS delisted_listings (
    external_id text PRIMARY KEY,
    source_url text,
    verified_at timestamptz NOT NULL,
    verification_method text NOT NULL DEFAULT 'http_404'
);

CREATE TABLE IF NOT EXISTS export_runs (
    run_id text PRIMARY KEY, source_scope text NOT NULL,
    fetched_at timestamptz NOT NULL, completed_at timestamptz,
    completion_ordinal bigint,
    snapshot_count integer NOT NULL, manifest_sha256 text NOT NULL,
    source_config_sha256 text,
    reconciliation_status text NOT NULL DEFAULT 'running',
    archival_candidate_count integer NOT NULL DEFAULT 0,
    archival_blocked_count integer NOT NULL DEFAULT 0,
    archived_count integer NOT NULL DEFAULT 0
);
ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS snapshot_count integer;
ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS manifest_sha256 text;
ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS source_config_sha256 text;
ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS reconciliation_status text NOT NULL DEFAULT 'running';
ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS archival_candidate_count integer NOT NULL DEFAULT 0;
ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS archival_blocked_count integer NOT NULL DEFAULT 0;
ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS archived_count integer NOT NULL DEFAULT 0;
CREATE SEQUENCE IF NOT EXISTS export_run_completion_ordinal_seq AS bigint;
ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS completion_ordinal bigint;
WITH base AS (
    SELECT COALESCE(MAX(completion_ordinal), 0) AS ordinal FROM export_runs
), ranked AS (
    SELECT run_id, ROW_NUMBER() OVER (
        ORDER BY completed_at, fetched_at, run_id
    ) AS ordinal
    FROM export_runs
    WHERE completed_at IS NOT NULL AND completion_ordinal IS NULL
)
UPDATE export_runs e
SET completion_ordinal = base.ordinal + ranked.ordinal
FROM base, ranked
WHERE e.run_id = ranked.run_id;
DO $$ DECLARE max_ordinal bigint; BEGIN
    SELECT MAX(completion_ordinal) INTO max_ordinal FROM export_runs;
    IF max_ordinal IS NULL THEN
        PERFORM setval('export_run_completion_ordinal_seq', 1, false);
    ELSE
        PERFORM setval(
            'export_run_completion_ordinal_seq',
            GREATEST(
                max_ordinal,
                (SELECT last_value FROM export_run_completion_ordinal_seq)
            ),
            true
        );
    END IF;
END $$;
UPDATE export_runs SET reconciliation_status='outcome_unknown'
WHERE completed_at IS NOT NULL AND reconciliation_status='running';
CREATE UNIQUE INDEX IF NOT EXISTS ux_export_runs_completion_ordinal
    ON export_runs(completion_ordinal) WHERE completion_ordinal IS NOT NULL;
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname='ck_export_runs_completion_pair'
        AND conrelid='export_runs'::regclass
    ) THEN
        ALTER TABLE export_runs ADD CONSTRAINT ck_export_runs_completion_pair
            CHECK ((completed_at IS NULL) = (completion_ordinal IS NULL));
    END IF;
END $$;
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname='ck_export_runs_source_config_sha256'
        AND conrelid = 'export_runs'::regclass
    ) THEN
        ALTER TABLE export_runs
            ADD CONSTRAINT ck_export_runs_source_config_sha256
            CHECK (
                source_config_sha256 IS NULL
                OR source_config_sha256 ~ '^[0-9a-f]{64}$'
            );
    END IF;
END $$;
CREATE TABLE IF NOT EXISTS listing_export_state (
    listing_id uuid PRIMARY KEY REFERENCES listings("Id") ON DELETE RESTRICT,
    source_scope text NOT NULL, first_seen_at timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL, last_seen_run_id text NOT NULL,
    non_ai_passed boolean NOT NULL, pipeline_decision text NOT NULL,
    archive_reason text, raw_payload jsonb NOT NULL,
    missing_complete_snapshots integer NOT NULL DEFAULT 0,
    last_missing_snapshot_date date
);
ALTER TABLE listing_export_state ADD COLUMN IF NOT EXISTS missing_complete_snapshots integer NOT NULL DEFAULT 0;
ALTER TABLE listing_export_state ADD COLUMN IF NOT EXISTS last_missing_snapshot_date date;
CREATE INDEX IF NOT EXISTS ix_listing_export_state_scope_run ON listing_export_state(source_scope,last_seen_run_id);
CREATE TABLE IF NOT EXISTS listing_imports (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    listing_id uuid NOT NULL REFERENCES listings("Id") ON DELETE RESTRICT,
    run_id text NOT NULL REFERENCES export_runs(run_id) ON DELETE RESTRICT,
    imported_at timestamptz NOT NULL, payload_sha256 text NOT NULL,
    raw_payload jsonb NOT NULL, non_ai_passed boolean NOT NULL,
    pipeline_decision text NOT NULL, UNIQUE (listing_id, run_id)
);
CREATE TABLE IF NOT EXISTS ai_evidence (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    listing_id uuid NOT NULL REFERENCES listings("Id") ON DELETE RESTRICT,
    run_id text NOT NULL REFERENCES export_runs(run_id) ON DELETE RESTRICT,
    decision text NOT NULL, confidence text, model_version text NOT NULL,
    rule_version text NOT NULL, evidence jsonb NOT NULL,
    evidence_sha256 text NOT NULL, created_at timestamptz NOT NULL,
    UNIQUE (listing_id, run_id)
);
CREATE TABLE IF NOT EXISTS listing_media (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    listing_id uuid NOT NULL REFERENCES listings("Id") ON DELETE RESTRICT,
    kind text NOT NULL CHECK (kind IN ('thumbnail','floorplan')),
    source_url text NOT NULL, local_path text NOT NULL, content_type text,
    content_sha256 text NOT NULL, byte_size bigint NOT NULL,
    cached_at timestamptz NOT NULL, UNIQUE (listing_id,kind,source_url)
);


-- Native ingestion contract. Application migrations own production evolution; this mirrors
-- the contract for isolated PostgreSQL schema bootstraps without reading SQLite state.
CREATE TABLE IF NOT EXISTS ingestion_runs (
    run_id uuid PRIMARY KEY,
    source_system text NOT NULL CHECK (length(btrim(source_system)) > 0),
    source_scope text NOT NULL CHECK (length(btrim(source_scope)) > 0),
    requested_at timestamptz NOT NULL,
    started_at timestamptz,
    completed_at timestamptz,
    run_status text NOT NULL CHECK (run_status IN ('running','succeeded','failed','cancelled')),
    manifest_sha256 text NOT NULL CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK ((run_status = 'running' AND completed_at IS NULL) OR (run_status <> 'running' AND completed_at IS NOT NULL))
);
CREATE TABLE IF NOT EXISTS ingestion_source_snapshots (
    snapshot_id uuid PRIMARY KEY,
    run_id uuid NOT NULL,
    source_name text NOT NULL CHECK (length(btrim(source_name)) > 0),
    snapshot_sha256 text NOT NULL CHECK (snapshot_sha256 ~ '^[0-9a-f]{64}$'),
    payload jsonb NOT NULL,
    captured_at timestamptz NOT NULL,
    FOREIGN KEY (run_id) REFERENCES ingestion_runs(run_id) ON DELETE RESTRICT,
    UNIQUE (run_id, source_name, snapshot_sha256)
);
CREATE INDEX IF NOT EXISTS ix_ingestion_source_snapshots_run_id
    ON ingestion_source_snapshots(run_id, captured_at);
CREATE TABLE IF NOT EXISTS ingestion_stage_outcomes (
    outcome_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id uuid NOT NULL,
    stage_name text NOT NULL CHECK (length(btrim(stage_name)) > 0),
    attempt integer NOT NULL CHECK (attempt > 0),
    stage_status text NOT NULL CHECK (stage_status IN ('succeeded','failed','skipped')),
    outcome jsonb NOT NULL DEFAULT '{}'::jsonb,
    started_at timestamptz NOT NULL,
    completed_at timestamptz NOT NULL,
    FOREIGN KEY (run_id) REFERENCES ingestion_runs(run_id) ON DELETE RESTRICT,
    UNIQUE (run_id, stage_name, attempt),
    CHECK (completed_at >= started_at)
);
CREATE INDEX IF NOT EXISTS ix_ingestion_stage_outcomes_run_stage
    ON ingestion_stage_outcomes(run_id, stage_name, attempt DESC);

CREATE OR REPLACE FUNCTION reject_ingestion_audit_fact_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION '% records are immutable', TG_TABLE_NAME;
END;
$$;

CREATE OR REPLACE FUNCTION enforce_ingestion_run_lifecycle()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'ingestion runs cannot be deleted';
    END IF;

    IF NEW.run_id IS DISTINCT FROM OLD.run_id
       OR NEW.source_system IS DISTINCT FROM OLD.source_system
       OR NEW.source_scope IS DISTINCT FROM OLD.source_scope
       OR NEW.requested_at IS DISTINCT FROM OLD.requested_at
       OR NEW.started_at IS DISTINCT FROM OLD.started_at
       OR NEW.manifest_sha256 IS DISTINCT FROM OLD.manifest_sha256 THEN
        RAISE EXCEPTION 'ingestion run identity and provenance are immutable';
    END IF;

    IF OLD.run_status <> 'running'
       OR NEW.run_status NOT IN ('succeeded', 'failed', 'cancelled')
       OR NEW.completed_at IS NULL THEN
        RAISE EXCEPTION 'ingestion runs may only transition from running to a terminal status with a completion timestamp';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION reject_ingestion_audit_fact_truncate()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION '% records cannot be truncated', TG_TABLE_NAME;
END;
$$;

DROP TRIGGER IF EXISTS ingestion_runs_truncate_immutable ON ingestion_runs;
CREATE TRIGGER ingestion_runs_truncate_immutable
BEFORE TRUNCATE ON ingestion_runs
FOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();

DROP TRIGGER IF EXISTS ingestion_source_snapshots_truncate_immutable ON ingestion_source_snapshots;
CREATE TRIGGER ingestion_source_snapshots_truncate_immutable
BEFORE TRUNCATE ON ingestion_source_snapshots
FOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();

DROP TRIGGER IF EXISTS ingestion_stage_outcomes_truncate_immutable ON ingestion_stage_outcomes;
CREATE TRIGGER ingestion_stage_outcomes_truncate_immutable
BEFORE TRUNCATE ON ingestion_stage_outcomes
FOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();

DROP TRIGGER IF EXISTS ingestion_source_snapshots_immutable ON ingestion_source_snapshots;
CREATE TRIGGER ingestion_source_snapshots_immutable
BEFORE UPDATE OR DELETE ON ingestion_source_snapshots
FOR EACH ROW EXECUTE FUNCTION reject_ingestion_audit_fact_mutation();

DROP TRIGGER IF EXISTS ingestion_stage_outcomes_immutable ON ingestion_stage_outcomes;
CREATE TRIGGER ingestion_stage_outcomes_immutable
BEFORE UPDATE OR DELETE ON ingestion_stage_outcomes
FOR EACH ROW EXECUTE FUNCTION reject_ingestion_audit_fact_mutation();

DROP TRIGGER IF EXISTS ingestion_runs_lifecycle_guard ON ingestion_runs;
CREATE TRIGGER ingestion_runs_lifecycle_guard
BEFORE UPDATE OR DELETE ON ingestion_runs
FOR EACH ROW EXECUTE FUNCTION enforce_ingestion_run_lifecycle();
