-- Migration 005 — chaîne de migration BuJo
--
-- migrated_to porte une DATE : « repoussée au 14 ». Cela suffit à afficher la
-- ligne, pas à remonter la chaîne. Deux tâches repoussées le même jour vers le
-- même jour deviennent indiscernables, et « repoussée 4 fois » est incalculable.
--
-- migrated_from porte l'ID de l'entrée mère, écrit à la naissance de l'entrée
-- fille. La chaîne se remonte alors d'un maillon à la fois. Le calcul viendra en
-- V1.2 ; la colonne arrive maintenant pour que les données s'accumulent.
--
-- Asymétrie assumée : migrated_to appartient à la mère et pourrait être défait,
-- migrated_from appartient à la fille et enregistre un fait de naissance. Il
-- n'est jamais effacé ni modifié après l'INSERT.

-- Volontairement SANS clause REFERENCES, pour deux raisons :
--   * DELETE FROM log_entries (zone dangereuse) échouerait sur une clé étrangère
--     auto-référencée : foreign_keys est à ON, l'optimisation de troncature est
--     alors désactivée, et SQLite supprime ligne à ligne — une mère effacée avant
--     sa fille lève « FOREIGN KEY constraint failed »
--   * à la synchro, une fille peut arriver avant sa mère. Une contrainte dure
--     rejetterait l'insertion au lieu de la laisser se réconcilier
-- L'intégrité est portée par le code, qui n'écrit cette colonne qu'en un seul
-- endroit : MigrateLogEntry.
ALTER TABLE log_entries ADD COLUMN migrated_from TEXT;

-- Sens fille -> mère : passe par la clé primaire, déjà indexée.
-- Cet index sert le sens inverse, mère -> filles, celui de la revue mensuelle.
CREATE INDEX IF NOT EXISTS idx_log_entries_migrated_from
    ON log_entries (migrated_from);
