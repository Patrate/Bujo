-- Migration 006 — habitudes non quotidiennes
--
-- Quatre colonnes suffisent, et c'est une décision, pas une économie : l'horaire
-- n'est PAS historisé. Les jours dus se déduisent toujours de l'horaire courant,
-- donc changer l'horaire recalcule tout le passé. Passer « lundi et mercredi » à
-- « quotidienne » fait s'effondrer le taux rétroactivement, et c'est le
-- comportement voulu : la nouvelle exigence juge aussi les jours écoulés.
-- Une table d'horaires datés aurait dit l'inverse.
--
--   schedule_kind      'daily' | 'weekly' | 'interval'
--   schedule_days      masque de bits, pertinent pour 'weekly' seulement
--   schedule_interval  nombre de jours, pertinent pour 'interval' seulement
--   schedule_anchor    'YYYY-MM-DD', pertinent pour 'interval' seulement
--
-- Le masque de bits plutôt qu'une chaîne « 1,3 » : 1 = lundi, 2 = mardi, 4 =
-- mercredi, 8 = jeudi, 16 = vendredi, 32 = samedi, 64 = dimanche. Un INTEGER se
-- transpose tel quel côté Postgres, et un ET binaire remplace un découpage de
-- chaîne dans une boucle qui parcourt 84 jours à chaque affichage du Suivi.
--
-- schedule_anchor est une PHASE, pas un début : la formule modulo s'applique
-- symétriquement de part et d'autre de l'ancre. Ce qui borne le passé, c'est
-- habits.created_at — une habitude n'est jamais due avant d'exister — et
-- habits.archived_at borne l'avenir. Ces deux bornes sont portées par le code
-- (HabitSchedule), pas par ces colonnes.

-- NOT NULL exige un DEFAULT non nul dans un ALTER TABLE SQLite. Ce défaut n'est
-- pas seulement une contrainte technique : il vaut migration des données. Toutes
-- les habitudes existantes deviennent explicitement quotidiennes, ce qu'elles
-- étaient déjà implicitement, et aucune statistique ne bouge après application.
ALTER TABLE habits ADD COLUMN schedule_kind TEXT NOT NULL DEFAULT 'daily';
ALTER TABLE habits ADD COLUMN schedule_days INTEGER NOT NULL DEFAULT 0;
ALTER TABLE habits ADD COLUMN schedule_interval INTEGER NOT NULL DEFAULT 0;
ALTER TABLE habits ADD COLUMN schedule_anchor TEXT;

-- Comme pour value_type en 002, le CHECK sur schedule_kind ne peut pas être ajouté
-- par ALTER TABLE dans SQLite. Il est porté par le code (enum ScheduleKind, qui
-- retombe sur 'daily' devant une valeur inconnue) et sera rétabli côté Postgres.
