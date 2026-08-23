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

        _lock = new LockController(_db, _settings);
        _lock.Released += OnLockReleased;

        _tray = new TrayIcon(
            open: ShowMain,
            lockNow: () => { _main?.Hide(); _lock.Engage(); },
            quit: Quit,
            isLocked: () => _lock.IsActive);

        if (_lock.ShouldLock(LogicalDay.Today())) _lock.Engage();
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

    private static void ShowMain()
    {
        if (_lock?.IsActive == true) return;

        if (_main is null)
        {
            _main = new MainWindow(_db!, _settings!, _backup!);
            _main.QuitRequested += Quit;   // fermeture réelle quand CloseAction = Quit
            _main.RoutineChanged += () => { if (_lock!.ShouldLock(LogicalDay.Today())) { _main.Hide(); _lock.Engage(); } };
        }
        _main.Reveal();
    }

    private static void Quit()
    {
        _main = null;
        Application.Current.Shutdown();
    }
}
