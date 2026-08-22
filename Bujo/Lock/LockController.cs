using System.IO;
using System.Windows.Threading;
using Bujo.Core;
using Forms = System.Windows.Forms;

namespace Bujo.Lock;

/// <summary>
/// Orchestre le verrou. Le contrat est simple : le verrou n'existe que tant que
/// <see cref="JournalDb.IsRoutineDone"/> répond false pour le jour logique courant.
/// </summary>
public sealed class LockController : IDisposable
{
    private readonly JournalDb _db;
    private readonly List<LockWindow> _windows = [];
    private readonly DispatcherTimer _reassert;
    private readonly DispatcherTimer _dayWatch;

    private DateOnly _day;
    private bool _active;

    /// <summary>Levé quand la routine est validée ou qu'une sortie forcée a lieu.</summary>
    public event Action? Released;
    public bool IsActive => _active;

    public LockController(JournalDb db)
    {
        _db = db;
        _day = LogicalDay.Today();

        // 300 ms : assez court pour qu'un Alt+Tab ne laisse pas le temps de cliquer,
        // assez long pour ne pas saturer la file de messages.
        _reassert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _reassert.Tick += (_, _) => ReassertIfNeeded();

        // Surveille la bascule de 6h : un PC resté allumé toute la nuit doit se
        // reverrouiller quand le nouveau jour logique commence.
        _dayWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _dayWatch.Tick += (_, _) => CheckDayRollover();
        _dayWatch.Start();
    }

    /// <summary>Coupe-circuit. Sans lui, un bug de lecture de la base te verrouille dehors un lundi matin.</summary>
    public static bool KillSwitchEngaged =>
        Environment.GetEnvironmentVariable("BUJO_NOLOCK") == "1"
        || File.Exists(Path.Combine(Path.GetDirectoryName(JournalDb.DefaultPath)!, "NOLOCK"));

    public bool ShouldLock(DateOnly day) => !KillSwitchEngaged && _db.HasRoutine(day) && !_db.IsRoutineDone(day);

    public void Engage()
    {
        if (_active) return;
        _active = true;
        _day = LogicalDay.Today();
        _db.EnsureDay(_day);
        _db.LogLockEvent(_day, "shown");

        var primary = Forms.Screen.PrimaryScreen;
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var window = new LockWindow(
                _db, _day, screen,
                interactive: screen.Equals(primary),
                onRoutineComplete: Release,
                onBypass: Release);
            _windows.Add(window);
            window.Show();
        }

        _reassert.Start();
        _windows.FirstOrDefault()?.Reassert();
    }

    public void Release()
    {
        if (!_active) return;
        _active = false;
        _reassert.Stop();

        foreach (var w in _windows) w.AllowCloseAndClose();
        _windows.Clear();
        Released?.Invoke();
    }

    private void ReassertIfNeeded()
    {
        if (!_active || _windows.Count == 0) return;

        var foreground = Native.GetForegroundWindow();
        if (_windows.Any(w => w.Handle == foreground))
        {
            // Déjà au premier plan : on se contente de recoller le topmost, sans
            // voler le focus, sinon le champ de saisie perd le curseur en boucle.
            foreach (var w in _windows) Native.PinTopmost(w.Handle);
            return;
        }

        foreach (var w in _windows) w.Reassert();
    }

    private void CheckDayRollover()
    {
        var today = LogicalDay.Today();
        if (today == _day) return;

        _day = today;
        if (_active) Release();          // la fenêtre affichait la veille
        if (ShouldLock(_day)) Engage();
    }

    public void Dispose()
    {
        _reassert.Stop();
        _dayWatch.Stop();
    }
}
