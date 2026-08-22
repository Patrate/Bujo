-- Migration 002 — habitudes typées
--
-- Principe : la VALEUR et la VALIDATION sont deux données distinctes.
--   * value_num / value_text  : ce que tu as saisi, écrit au fil de la frappe
--   * done                    : validé explicitement, seul champ que le verrou consulte
-- Aucune règle n'infère l'une depuis l'autre.

ALTER TABLE habits ADD COLUMN value_type TEXT NOT NULL DEFAULT 'bool';
    -- 'bool'   : case à cocher simple
    -- 'number' : saisie numérique (nombre de squats, minutes, km...)
    -- 'text'   : saisie libre (humeur, note, morceau travaillé au piano...)

ALTER TABLE habits ADD COLUMN unit TEXT;
    -- Libellé affiché après le champ : 'reps', 'min', 'km'. NULL si sans objet.

ALTER TABLE habits ADD COLUMN target_num REAL;
    -- Objectif indicatif, purement informatif : n'entre PAS dans le calcul de done.

ALTER TABLE habit_entries ADD COLUMN value_num REAL;
ALTER TABLE habit_entries ADD COLUMN value_text TEXT;

-- Le CHECK sur value_type ne peut pas être ajouté par ALTER TABLE dans SQLite.
-- Il est porté par le code (enum HabitValueType) et sera rétabli côté Postgres
-- le jour de la synchro.
