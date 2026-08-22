-- Migration 004 — retrait de l'unité et de l'objectif
--
-- Le nom de l'habitude porte déjà l'unité (« Squats » = un nombre de squats),
-- et un objectif chiffré est contraire à l'usage voulu : on suit une progression,
-- on ne se fixe pas un plafond.
--
-- Réversible à peu de frais : un ALTER TABLE ADD COLUMN suffirait à les rétablir.

ALTER TABLE habits DROP COLUMN unit;
ALTER TABLE habits DROP COLUMN target_num;