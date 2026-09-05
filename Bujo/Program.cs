using System.Windows;
using Bujo.Core;
using Bujo.Lock;
using Bujo.Ui;
using Application = System.Windows.Application;

namespace Bujo;

public static class Program
{
    private static JournalDb? _db;
    private static Settings? _settings;
    private static LockController? _lock;
    private static TrayIcon? _tray;
    private static MainWindow? _main;
    private static BackupService? _backup;
    
    [STAThread]
    public static int Main(string[] args)
    {
        // Instance unique. Les tâches planifiées peuvent donc réveiller l'exe
        // aussi souvent qu'elles veulent : si le résident tourne, elles ressortent.
        using var mutex = new Mutex(true, @"Global\BujoSingleInstance", out var isFirst);
        if (!isFirst) return 0;

        var startHidden = args.Contains("--check");

        try
        {
            _db = new JournalDb();
        }
        catch (Exception ex)
        {
            // Règle de sûreté : base illisible, on n'enferme personne.
            Log.Write("startup", ex);
            return 1;
        }

        _settings = new Settings(_db);
        _settings.Apply();                 // fixe LogicalDay.Cutoff avant tout calcul
        _backup = new BackupService(_db, _settings);
        _backup.Start();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // AVANT la moindre fenêtre : un style implicite posé après coup ne serait pas
        // repris par les fenêtres déjà construites. Le verrou du matin peut s'ouvrir
        // dès la ligne suivante.
        Theme.Apply(app);

        _lock = new LockController(_db, _settings);
        _lock.Released += OnLockReleased;

        _tray = new TrayIcon(
            open: ShowMain,
            lockNow: () => { _main?.Hide(); _lock.Engage(); },
            quit: Quit,
            isLocked: () => _lock.IsBlocking);

        // Première ouverture : la présentation passe AVANT tout le reste, et notamment
        // avant le moindre test de verrouillage. Sur une base neuve il n'y a de toute
        // façon aucune habitude, donc rien à verrouiller ; c'est l'assistant qui décide
        // ensuite, par ses deux boutons.
        //
        // Y compris au démarrage de session avec --check : une machine qui se réveille
        // sur une installation jamais configurée doit poser la question, pas rester
        // muette dans la zone de notification.
        if (!_settings.SetupDone)
        {
            ShowSetup(owner: null);
        }
        else if (_lock.ShouldLock(LogicalDay.Today())) _lock.Engage();
        else if (!startHidden) ShowMain();

        app.Run();

        _tray.Dispose();
        _lock.Dispose();
        _backup.Dispose();
        _db.Dispose();
        return 0;
    }

    /// <summary>
    /// L'app reste résidente après le verrou : c'est elle qui surveille la bascule
    /// de 6 h tant que la session est ouverte.
    ///
    /// Le message dépend de la raison. Féliciter après une sortie de secours était
    /// à la fois faux et un peu insultant ; à l'inverse, réprimander pousse à
    /// l'abandon ou au contournement malhonnête. Le ton reste donc neutre et
    /// rappelle simplement que la journée n'est pas jouée.
    /// </summary>
    private static void OnLockReleased(LockRelease reason)
    {
        switch (reason)
        {
            case LockRelease.Completed:
                _tray?.Notify("Routine validée",
                    "Bonne journée. Bujo reste dans la zone de notification.");
                break;

            case LockRelease.Bypassed:
                _tray?.Notify("Verrou levé",
                    "La routine du jour reste ouverte : tu peux la terminer depuis l'onglet Journal.");
                break;

            // Bascule du jour : la fenêtre de la veille se ferme pour laisser place
            // à celle du jour. Rien n'a été fait, il n'y a rien à annoncer.
            case LockRelease.DayChanged:
                break;

            // Snooze : rien non plus, et pour une autre raison. Le verrou revient de
            // lui-même dans quelques minutes ; annoncer « à tout à l'heure » serait
            // bavard, et féliciter ou déplorer un geste neutre serait un jugement.
            case LockRelease.Snoozed:
                break;
        }
    }

    /// <summary>
    /// Ouvre la présentation de premier lancement.
    ///
    /// À sa fermeture, deux issues seulement. « Terminer » n'engage rien : le verrou
    /// apparaîtra demain matin, au premier moment où il a du sens. « Terminer et
    /// verrouiller maintenant » le déclenche sur-le-champ, parce qu'il a été demandé.
    ///
    /// C'est ce couple qui remplace un état « jour d'installation » en base : terminer
    /// à 15 h ne doit pas produire un plein écran cinq secondes après l'installation,
    /// avant la moindre confiance accordée à l'outil.
    /// </summary>
    /// <param name="owner">
    /// Fenêtre propriétaire, pour une relecture depuis les Paramètres : la présentation
    /// est alors réellement MODALE, sinon on pourrait modifier les mêmes réglages dans
    /// les deux fenêtres à la fois.
    ///
    /// Null au premier lancement, et il le faut : ShowDialog ouvre une boucle de
    /// messages imbriquée, ce qui n'a pas de sens avant que app.Run() n'ait démarré la
    /// principale. Aucune autre fenêtre n'existe alors, la présentation est donc
    /// exclusive de fait.
    /// </param>
    private static void ShowSetup(Window? owner)
    {
        var setup = new SetupWindow(_db!, _settings!);
        setup.Finished += lockNow =>
        {
            if (lockNow && _lock!.ShouldLock(LogicalDay.Today())) _lock.Engage();
            else ShowMain();
        };

        if (owner is null) { setup.Show(); return; }

        setup.Owner = owner;
        setup.ShowDialog();
    }

    private static void ShowMain()
    {
        // IsBlocking et non IsActive : sous l'encart souple, ouvrir Bujo est
        // parfaitement légitime — c'est même le moyen d'aller finir sa routine depuis
        // le Journal. Seul le plein écran doit refuser.
        if (_lock?.IsBlocking == true) return;

        if (_main is null)
        {
            _main = new MainWindow(_db!, _settings!, _backup!);
            _main.QuitRequested += Quit;   // fermeture réelle quand CloseAction = Quit

            // Relecture depuis les Paramètres : la présentation ne doit RIEN engager en
            // se refermant. Ses deux boutons de fin gardent leur sens — « Terminer et
            // verrouiller maintenant » reste disponible si on veut voir l'écran — mais
            // le simple fait de la revoir ne déclenche pas de verrou.
            _main.SetupRequested += () => ShowSetup(_main);
            // Engage d'abord, repli ensuite : c'est l'engagement qui fixe le mode, donc
            // le seul moment où l'on sait s'il faut cacher la fenêtre. En mode souple,
            // la replier serait absurde — l'encart ne prend pas l'écran.
            _main.RoutineChanged += () =>
            {
                if (!_lock!.ShouldLock(LogicalDay.Today())) return;
                _lock.Engage();
                if (_lock.IsBlocking) _main!.Hide();
            };
        }
        _main.Reveal();
    }

    private static void Quit()
    {
        _main = null;
        Application.Current.Shutdown();
    }
}
