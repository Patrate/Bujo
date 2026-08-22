# Bujo — bullet journal numérique avec verrou de routine

Squelette C# / WPF (.NET 9). Base SQLite locale, schéma prêt pour une synchro
multi-appareils ultérieure (UUIDv7, `updated_at`, `deleted_at`, `device_id`,
suppressions logiques).

## Compiler et lancer

```powershell
dotnet build -c Release
dotnet run -- --check     # mode "tâche planifiée" : ne fait rien si la routine est faite
dotnet run                # lance l'app (verrou si nécessaire, sinon le journal)
```

## Déclenchement automatique

Trois déclencheurs, tous en mode `--check` :

```powershell
# À l'ouverture de session
schtasks /Create /TN "Bujo\Verrou logon" /SC ONLOGON `
  /TR "\"C:\Apps\Bujo\Bujo.exe\" --check"

# Toutes les 5 minutes de 6h à 12h
schtasks /Create /TN "Bujo\Verrou matin" /SC MINUTE /MO 5 /ST 06:00 /DU 06:00 `
  /TR "\"C:\Apps\Bujo\Bujo.exe\" --check"
```

Le troisième — au déverrouillage de la session — n'est pas exprimable en
`schtasks` : il faut passer par le Planificateur de tâches (Déclencheur >
*À la connexion d'un poste de travail*) ou importer une définition XML.

Décoche **Ne démarrer la tâche que si l'ordinateur est alimenté sur secteur**,
sinon le verrou ne se déclenchera pas sur le laptop.

## Coupe-circuit

Deux moyens de désarmer le verrou, à connaître avant d'en avoir besoin :

- variable d'environnement `BUJO_NOLOCK=1`
- fichier vide `%APPDATA%\Bujo\NOLOCK`

Le processus s'arrête aussi de lui-même sans rien verrouiller si la base est
illisible (trace dans `%TEMP%\bujo-error.log`).

## Ce que le verrou fait — et ne fait pas

Fait :

- fenêtre sans bordure, plein écran, **une par moniteur**, absente de la barre des tâches
- `WM_CLOSE` et `SC_MINIMIZE` interceptés (Alt+F4, Win+D, Win+M sans effet)
- retour au premier plan toutes les 300 ms si le focus est perdu (neutralise Alt+Tab)
- sortie de secours par maintien de 5 s, journalisée et affichée en compteur

Ne fait pas, volontairement :

- aucun hook clavier bas niveau, aucun processus de surveillance mutuelle
- Ctrl+Alt+Suppr, le gestionnaire des tâches et `taskkill` fonctionnent normalement
- rien n'est masqué ni protégé contre l'arrêt du processus

La dissuasion vient de la friction et du compteur de sorties forcées, pas de
l'impossibilité technique.

## Structure

```
Core/LogicalDay.cs     jour logique (bascule 6h), horodatages, UUIDv7
Core/JournalDb.cs      persistance, migration, sauvegarde à chaud (VACUUM INTO)
Data/schema.sql        schéma commenté, transposable vers Postgres
Lock/Native.cs         P/Invoke Win32 (topmost, premier plan)
Lock/LockWindow.cs     fenêtre de verrou, cases à cocher, sortie de secours
Lock/LockController.cs orchestration multi-écrans, bascule de jour, coupe-circuit
Program.cs             point d'entrée, instance unique
```

## Reste à faire

- fenêtre principale du journal : daily log (`log_entries`), migration BuJo
  (`state = 'migrated'`, `migrated_to`), revue mensuelle, séries d'habitudes
- édition de la routine (ajout/retrait d'habitudes, `is_routine`)
- sauvegarde automatique via `JournalDb.BackupTo` + envoi vers le VPS
- API de synchro, le jour où l'Android arrive
