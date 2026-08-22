-- Migration 003 — archivage des habitudes
--
-- Trois états distincts, à ne pas confondre :
--   * active = 0      : habitude conservée dans l'écran de configuration, mais
--                       hors routine du jour (mise en pause)
--   * archived_at     : sortie de l'écran de configuration ; ses habit_entries
--                       restent lisibles dans les vues de suivi
--   * deleted_at      : suppression réelle, réservée à la synchro. Jamais posée par l'UI.

ALTER TABLE habits ADD COLUMN archived_at TEXT;
