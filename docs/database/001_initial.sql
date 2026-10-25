-- DesktopNotes schema v1. Reference migration, not an installed application database.
-- Application opens a local database; set journal_mode=WAL before this transaction.
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;
BEGIN IMMEDIATE;

CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_utc_ms INTEGER NOT NULL,
    description TEXT NOT NULL
);

CREATE TABLE storage_roots (
    id TEXT PRIMARY KEY NOT NULL,
    absolute_path TEXT NOT NULL CHECK(length(absolute_path) > 0),
    platform TEXT NOT NULL CHECK(platform IN ('windows','macos','linux')),
    created_utc_ms INTEGER NOT NULL
);

-- kind: 0 task, 1 note. status: 0 active, 1 closed (completed/archived).
-- inherit_mask bits: publisher=1, assignee=2, priority=4, start=8, due=16, root=32.
-- records stores OWN values. effective_records stores the transactional projection.
CREATE TABLE records (
    id TEXT PRIMARY KEY NOT NULL,
    parent_id TEXT REFERENCES records(id) ON DELETE RESTRICT,
    kind INTEGER NOT NULL CHECK(kind IN (0,1)),
    title TEXT NOT NULL DEFAULT '',
    body TEXT NOT NULL DEFAULT '',
    status INTEGER NOT NULL DEFAULT 0 CHECK(status IN (0,1)),
    publisher TEXT,
    assignee TEXT,
    priority INTEGER NOT NULL DEFAULT 2 CHECK(priority BETWEEN 0 AND 3),
    start_utc_ms INTEGER,
    due_kind INTEGER NOT NULL DEFAULT 0 CHECK(due_kind IN (0,1,2)),
    due_date TEXT,
    due_utc_ms INTEGER,
    due_zone TEXT,
    attachment_root_id TEXT REFERENCES storage_roots(id) ON DELETE RESTRICT,
    inherit_mask INTEGER NOT NULL DEFAULT 0 CHECK(inherit_mask BETWEEN 0 AND 63),
    created_utc_ms INTEGER NOT NULL,
    updated_utc_ms INTEGER NOT NULL,
    closed_utc_ms INTEGER,
    deleted_utc_ms INTEGER,
    deletion_batch_id TEXT,
    version INTEGER NOT NULL DEFAULT 1 CHECK(version >= 1),
    CHECK(length(trim(title)) > 0 OR length(trim(body)) > 0),
    CHECK(parent_id IS NULL OR (kind = 0 AND parent_id <> id)),
    CHECK(parent_id IS NOT NULL OR inherit_mask = 0),
    CHECK((status = 0 AND closed_utc_ms IS NULL) OR
          (status = 1 AND closed_utc_ms IS NOT NULL)),
    CHECK((deleted_utc_ms IS NULL AND deletion_batch_id IS NULL) OR
          (deleted_utc_ms IS NOT NULL AND deletion_batch_id IS NOT NULL)),
    CHECK((due_kind = 0 AND due_date IS NULL AND due_utc_ms IS NULL AND due_zone IS NULL) OR
          (due_kind = 1 AND due_date IS NOT NULL AND length(due_date) = 10
             AND due_utc_ms IS NULL AND due_zone IS NULL) OR
          (due_kind = 2 AND due_date IS NULL AND due_utc_ms IS NOT NULL
             AND due_zone IS NOT NULL AND length(due_zone) > 0))
);

CREATE TRIGGER records_parent_insert BEFORE INSERT ON records
WHEN NEW.parent_id IS NOT NULL
BEGIN
    SELECT CASE WHEN NOT EXISTS (
        SELECT 1 FROM records p WHERE p.id = NEW.parent_id AND p.kind = 0
        AND p.parent_id IS NULL AND p.status = 0 AND p.deleted_utc_ms IS NULL
    ) THEN RAISE(ABORT, 'invalid_active_root_parent') END;
END;

CREATE TRIGGER records_identity_update BEFORE UPDATE OF parent_id, kind ON records
WHEN NEW.parent_id IS NOT OLD.parent_id OR NEW.kind <> OLD.kind
BEGIN
    SELECT RAISE(ABORT, 'kind_and_parent_are_immutable_in_v1');
END;

CREATE TRIGGER records_complete_parent BEFORE UPDATE OF status ON records
WHEN NEW.status = 1 AND OLD.status = 0
BEGIN
    SELECT CASE WHEN EXISTS (
        SELECT 1 FROM records c WHERE c.parent_id = NEW.id
        AND c.status = 0 AND c.deleted_utc_ms IS NULL
    ) THEN RAISE(ABORT, 'active_children_remain') END;
END;

CREATE TRIGGER records_reopen_child BEFORE UPDATE OF status, deleted_utc_ms ON records
WHEN NEW.parent_id IS NOT NULL AND NEW.status = 0 AND NEW.deleted_utc_ms IS NULL
BEGIN
    SELECT CASE WHEN NOT EXISTS (
        SELECT 1 FROM records p WHERE p.id = NEW.parent_id
        AND p.status = 0 AND p.deleted_utc_ms IS NULL
    ) THEN RAISE(ABORT, 'parent_must_be_active') END;
END;

CREATE TABLE effective_records (
    record_id TEXT PRIMARY KEY NOT NULL REFERENCES records(id) ON DELETE CASCADE,
    publisher TEXT,
    assignee TEXT,
    priority INTEGER NOT NULL CHECK(priority BETWEEN 0 AND 3),
    start_utc_ms INTEGER,
    due_kind INTEGER NOT NULL CHECK(due_kind IN (0,1,2)),
    due_date TEXT,
    due_utc_ms INTEGER,
    due_zone TEXT,
    attachment_root_id TEXT REFERENCES storage_roots(id) ON DELETE RESTRICT,
    due_boundary_utc_ms INTEGER,
    projection_zone TEXT NOT NULL,
    record_version INTEGER NOT NULL,
    CHECK((due_kind = 0 AND due_date IS NULL AND due_utc_ms IS NULL AND due_zone IS NULL
               AND due_boundary_utc_ms IS NULL) OR
          (due_kind = 1 AND due_date IS NOT NULL AND length(due_date) = 10
               AND due_utc_ms IS NULL AND due_zone IS NULL AND due_boundary_utc_ms IS NOT NULL) OR
          (due_kind = 2 AND due_date IS NULL AND due_utc_ms IS NOT NULL AND due_zone IS NOT NULL
               AND due_boundary_utc_ms IS NOT NULL AND due_boundary_utc_ms = due_utc_ms))
);

CREATE TABLE completion_snapshots (
    record_id TEXT PRIMARY KEY NOT NULL REFERENCES records(id) ON DELETE CASCADE,
    payload_json TEXT NOT NULL CHECK(json_valid(payload_json)),
    closed_utc_ms INTEGER NOT NULL
);

CREATE TABLE drafts (
    editor_id TEXT PRIMARY KEY NOT NULL,
    record_id TEXT REFERENCES records(id) ON DELETE SET NULL,
    base_version INTEGER,
    payload_json TEXT NOT NULL CHECK(json_valid(payload_json)),
    updated_utc_ms INTEGER NOT NULL
);

CREATE TABLE settings (
    key TEXT PRIMARY KEY NOT NULL,
    value_json TEXT NOT NULL CHECK(json_valid(value_json)),
    version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE attachments (
    id TEXT PRIMARY KEY NOT NULL,
    record_id TEXT NOT NULL REFERENCES records(id) ON DELETE RESTRICT,
    root_id TEXT NOT NULL REFERENCES storage_roots(id) ON DELETE RESTRICT,
    relative_path TEXT NOT NULL,
    original_name TEXT NOT NULL,
    entry_kind TEXT NOT NULL CHECK(entry_kind IN ('file','directory')),
    state TEXT NOT NULL CHECK(state IN ('ready','missing')),
    bytes_at_import INTEGER NOT NULL CHECK(bytes_at_import >= 0),
    created_utc_ms INTEGER NOT NULL,
    trashed_utc_ms INTEGER,
    UNIQUE(root_id, relative_path)
);

-- One job owns one top-level source item. A batch consists of multiple jobs.
CREATE TABLE attachment_jobs (
    id TEXT PRIMARY KEY NOT NULL,
    batch_id TEXT NOT NULL,
    record_id TEXT NOT NULL REFERENCES records(id) ON DELETE RESTRICT,
    attachment_id TEXT NOT NULL UNIQUE,
    root_id TEXT NOT NULL REFERENCES storage_roots(id) ON DELETE RESTRICT,
    operation TEXT NOT NULL CHECK(operation IN ('copy','move','relocate','purge')),
    source_path TEXT NOT NULL,
    target_relative_path TEXT NOT NULL,
    staging_relative_path TEXT NOT NULL,
    state TEXT NOT NULL CHECK(state IN (
        'planned','copying','verified','published','registered','cleaning',
        'done','source_cleanup_pending','failed','cancelled','needs_review')),
    source_device_id TEXT NOT NULL,
    error_code TEXT,
    created_utc_ms INTEGER NOT NULL,
    updated_utc_ms INTEGER NOT NULL
);

CREATE TABLE attachment_job_items (
    job_id TEXT NOT NULL REFERENCES attachment_jobs(id) ON DELETE CASCADE,
    relative_path TEXT NOT NULL,
    entry_kind TEXT NOT NULL CHECK(entry_kind IN ('file','directory')),
    source_identity TEXT NOT NULL,
    source_size INTEGER NOT NULL CHECK(source_size >= 0),
    source_mtime_ns INTEGER NOT NULL,
    sha256 TEXT,
    verified INTEGER NOT NULL DEFAULT 0 CHECK(verified IN (0,1)),
    source_removed INTEGER NOT NULL DEFAULT 0 CHECK(source_removed IN (0,1)),
    PRIMARY KEY(job_id, relative_path),
    CHECK(source_removed = 0 OR verified = 1)
);

CREATE TABLE reminder_deliveries (
    id TEXT PRIMARY KEY NOT NULL,
    record_id TEXT NOT NULL REFERENCES records(id) ON DELETE CASCADE,
    due_fingerprint TEXT NOT NULL,
    stage TEXT NOT NULL CHECK(stage IN ('today','before','overdue')),
    occurrence_key TEXT NOT NULL,
    delivery_state TEXT NOT NULL CHECK(delivery_state IN ('claimed','delivered','failed')),
    attempted_utc_ms INTEGER NOT NULL,
    UNIQUE(record_id, due_fingerprint, stage, occurrence_key)
);

CREATE INDEX ix_records_active ON records(kind, status, deleted_utc_ms, created_utc_ms, id);
CREATE INDEX ix_records_parent ON records(parent_id);
CREATE INDEX ix_effective_due ON effective_records(due_boundary_utc_ms, record_id);
CREATE INDEX ix_attachments_record ON attachments(record_id, trashed_utc_ms);
CREATE INDEX ix_jobs_state ON attachment_jobs(state, updated_utc_ms);

CREATE VIEW record_search AS
SELECT r.id, r.parent_id, r.kind, r.status, r.title, r.body, r.created_utc_ms,
       r.closed_utc_ms, r.deleted_utc_ms, r.version,
       e.publisher, e.assignee, e.priority, e.start_utc_ms,
       e.due_kind, e.due_date, e.due_utc_ms, e.due_zone, e.due_boundary_utc_ms
FROM records r JOIN effective_records e ON e.record_id = r.id;

INSERT INTO schema_migrations VALUES(1, unixepoch() * 1000, 'initial schema');
COMMIT;
