-- Bullet journal numérique — schéma SQLite v1
--
-- Conventions tenues partout, pour que la synchro multi-appareils reste possible
-- sans migration douloureuse :
--   * clé primaire = UUIDv7 en TEXT (triable par date, pas de collision entre appareils)
--   * horodatages = TEXT ISO-8601 UTC ('2026-08-18T04:12:33.4567890+00:00')
--   * jour logique = TEXT 'YYYY-MM-DD' (date de now - 6h, cf. LogicalDay.cs)
--   * pas de DELETE : deleted_at non NULL = supprimé
--   * updated_at + device_id sur toute ligne synchronisable
--
-- Les types SQL ci-dessous sont volontairement des types que Postgres comprendra
-- aussi (TEXT/INTEGER), pour que le jour où la base serveur arrive, le modèle
-- soit transposable ligne pour ligne.

CREATE TABLE IF NOT EXISTS schema_version (
    version     INTEGER NOT NULL
);

-- Appareils connus. Sert à tracer l'origine des écritures lors de la synchro.
CREATE TABLE IF NOT EXISTS devices (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    platform    TEXT NOT NULL,              -- 'windows' | 'android' | ...
    created_at  TEXT NOT NULL
);

-- Une ligne par jour logique. Créée paresseusement, au premier accès du jour.
CREATE TABLE IF NOT EXISTS days (
    id                    TEXT PRIMARY KEY,
    logical_date          TEXT NOT NULL UNIQUE,   -- 'YYYY-MM-DD'
    mood                  INTEGER,                -- 1..5, NULL si non renseigné
    note                  TEXT,                   -- note libre du jour
    routine_completed_at  TEXT,                   -- non NULL = routine validée
    created_at            TEXT NOT NULL,
    updated_at            TEXT NOT NULL,
    deleted_at            TEXT,
    device_id             TEXT NOT NULL REFERENCES devices(id)
);

-- Habitudes suivies. is_routine = 1 -> fait partie du verrou du matin.
CREATE TABLE IF NOT EXISTS habits (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    is_routine  INTEGER NOT NULL DEFAULT 0 CHECK (is_routine IN (0, 1)),
    position    INTEGER NOT NULL DEFAULT 0,
    active      INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    deleted_at  TEXT,
    device_id   TEXT NOT NULL REFERENCES devices(id)
);

-- Une case cochée = une ligne. Idempotent : c'est ce qui rend la synchro triviale
-- sur cette table (deux appareils qui cochent la même case tombent d'accord).
CREATE TABLE IF NOT EXISTS habit_entries (
    id            TEXT PRIMARY KEY,
    habit_id      TEXT NOT NULL REFERENCES habits(id),
    logical_date  TEXT NOT NULL,
    done          INTEGER NOT NULL DEFAULT 0 CHECK (done IN (0, 1)),
    done_at       TEXT,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL,
    deleted_at    TEXT,
    device_id     TEXT NOT NULL REFERENCES devices(id),
    UNIQUE (habit_id, logical_date)
);

CREATE INDEX IF NOT EXISTS idx_habit_entries_date ON habit_entries (logical_date);

-- Daily log au sens BuJo. Les tâches ponctuelles importantes restent dans Vikunja :
-- ici, ce sont les entrées de journal, pas une seconde todo list.
CREATE TABLE IF NOT EXISTS log_entries (
    id            TEXT PRIMARY KEY,
    logical_date  TEXT NOT NULL,
    kind          TEXT NOT NULL CHECK (kind IN ('task', 'event', 'note')),
    content       TEXT NOT NULL,
    state         TEXT NOT NULL DEFAULT 'open'
                  CHECK (state IN ('open', 'done', 'migrated', 'dropped')),
    position      INTEGER NOT NULL DEFAULT 0,
    migrated_to   TEXT,                     -- jour logique de destination
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL,
    deleted_at    TEXT,
    device_id     TEXT NOT NULL REFERENCES devices(id)
);

CREATE INDEX IF NOT EXISTS idx_log_entries_date ON log_entries (logical_date, position);

-- Journal du verrou. C'est la table qui fait la dissuasion : chaque sortie forcée
-- laisse une trace visible dans les statistiques.
CREATE TABLE IF NOT EXISTS lock_events (
    id            TEXT PRIMARY KEY,
    logical_date  TEXT NOT NULL,
    kind          TEXT NOT NULL CHECK (kind IN ('shown', 'completed', 'bypassed')),
    occurred_at   TEXT NOT NULL,
    device_id     TEXT NOT NULL REFERENCES devices(id)
);

CREATE INDEX IF NOT EXISTS idx_lock_events_date ON lock_events (logical_date);

-- Clé/valeur local, non synchronisé : identité de cet appareil, filigranes de synchro.
CREATE TABLE IF NOT EXISTS local_state (
    key    TEXT PRIMARY KEY,
    value  TEXT NOT NULL
);
