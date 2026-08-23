using System.IO;
using System.Windows.Threading;
using Bujo.Core;
using Forms = System.Windows.Forms;

namespace Bujo.Lock;

/// <summary>
/// Raison de la levée du verrou. Les trois cas se ressemblent côté code — la même
/// fenêtre se ferme — mais ne se ressemblent pas du tout côté utilisatrice.
/// </summary>
public enum LockRelease
{
    /// <summary>Toutes les lignes cochées, sortie par le bouton vert.</summary>
    Completed,

    /// <summary>Sortie de secours, après cinq secondes de maintien.</summary>
    Bypassed,

    /// <summary>Bascule du jour logique : la fenêtre affichait la veille.</summary>
    DayChanged,

    /// <summary>
    /// Snooze : levée temporaire, demandée et sans conséquence. N'est pas une sortie
    /// forcée et n'est comptabilisée nulle part — le verrou revient de lui-même.
    /// </summary>
    Snoozed
}

/// <summary>
/// Orchestre le verrou. Le contrat est simple : le verrou n'existe que tant que
/// <see cref="JournalDb.IsRoutineDone"/> répond false pour le jour logique courant.
/// </summary>
public sealed class LockController : IDisposable
{
    private readonly JournalDb _db;
    private readonly Settings _settings;
    private readonly List<LockWindow> _windows = [];
    private readonly DispatcherTimer _reassert;
    private readonly DispatcherTimer _dayWatch;
    private readonly DispatcherTimer _snoozeWatch;

    private DateOnly _day;
    private bool _active;

    /// <summary>
    /// Un snooze posé attend son échéance. Sert au filet de sécurité de _dayWatch,
    /// qui sans lui ne saurait pas distinguer « le snooze vient d'expirer » de
    /// « l'écran n'est simplement pas verrouillé ».
    /// </summary>
    private bool _snoozePending;

    /// <summary>
    /// Levé à chaque fermeture du verrou, avec la raison. Sans elle, l'appelant ne
    /// peut pas distinguer une routine faite d'une fuite, et finit par féliciter
    /// les deux.
    /// </summary>
    public event Action<LockRelease>? Released;
    public bool IsActive => _active;

    public LockController(JournalDb db, Settings settings)
    {
        _db = db;
        _settings = settings;
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

        // Timer dédié au retour de snooze. _dayWatch aurait pu s'en charger, mais à
        // 30 s d'intervalle un snooze de 5 minutes reviendrait à 5 min 22 s.
        _snoozeWatch = new DispatcherTimer();
        _snoozeWatch.Tick += (_, _) => SnoozeExpired();

        // Un snooze survit au redémarrage : il vit en base, pas en mémoire. Sans ce
        // réarmement, quitter puis relancer Bujo pendant un snooze le rendrait éternel
        // jusqu'à la bascule du jour — un contournement gratuit et involontaire.
        var pending = _db.SnoozeEnd(_day);
        if (pending is not null) ArmSnooze(pending.Value);
    }

    /// <summary>Coupe-circuit. Sans lui, un bug de lecture de la base te verrouille dehors un lundi matin.</summary>
    public static bool KillSwitchEngaged =>
        Environment.GetEnvironmentVariable("BUJO_NOLOCK") == "1"
        || File.Exists(Path.Combine(Path.GetDirectoryName(JournalDb.DefaultPath)!, "NOLOCK"));

    public bool ShouldLock(DateOnly day) =>
        !KillSwitchEngaged && !_db.IsSnoozed(day) && _db.HasRoutine(day) && !_db.IsRoutineDone(day);

    public void Engage()
    {
        if (_active) return;
        _active = true;
        _snoozePending = false;
        _snoozeWatch.Stop();
        _day = LogicalDay.Today();
        _db.EnsureDay(_day);
        _db.LogLockEvent(_day, "shown");

        var primary = Forms.Screen.PrimaryScreen;
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var window = new LockWindow(
                _db, _day, screen,
                interactive: screen.Equals(primary),
                snoozeMinutes: _settings.SnoozeMinutes,
                onRoutineComplete: () => Release(LockRelease.Completed),
                onSnooze: Snooze,
                onBypass: () => Release(LockRelease.Bypassed));
            _windows.Add(window);
            window.Show();
        }

        _reassert.Start();
        _windows.FirstOrDefault()?.Reassert();
    }

    public void Release(LockRelease reason)
    {
        if (!_active) return;
        _active = false;
        _reassert.Stop();

        foreach (var w in _windows) w.AllowCloseAndClose();
        _windows.Clear();
        Released?.Invoke(reason);
    }

    // ---------------------------------------------------------------- snooze

    /// <summary>
    /// Lève le verrou pour la durée réglée. Ce n'est pas une sortie forcée : rien
    /// n'est écrit dans lock_events, rien n'est compté, rien n'est reproché.
    /// </summary>
    public void Snooze()
    {
        if (!_active) return;

        var minutes = _settings.SnoozeMinutes;
        var until = DateTimeOffset.UtcNow.AddMinutes(minutes);
        _db.SetSnooze(_day, until);

        // Seule trace du snooze, et elle est volontairement hors de la base : la
        // non-reconductibilité a été repoussée faute de savoir si le problème existe.
        // Ces lignes servent à le savoir dans quelques semaines, sans engager le
        // schéma ni afficher quoi que ce soit à l'utilisatrice.
        Log.Write("snooze", $"{LogicalDay.Key(_day)} — {minutes} min, jusqu'à {until:O}");

        ArmSnooze(until);
        Release(LockRelease.Snoozed);
    }

    /// <summary>
    /// Arme le réveil. L'intervalle d'un DispatcherTimer doit être strictement
    /// positif : une échéance déjà passée — snooze rapatrié d'une session précédente,
    /// horloge reculée — est ramenée à un déclenchement immédiat plutôt que de lever.
    /// </summary>
    private void ArmSnooze(DateTimeOffset until)
    {
        _snoozePending = true;
        _snoozeWatch.Stop();

        var left = until - DateTimeOffset.UtcNow;
        _snoozeWatch.Interval = left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
        _snoozeWatch.Start();
    }

    private void SnoozeExpired()
    {
        _snoozeWatch.Stop();
        _snoozePending = false;

        // ShouldLock, et non Engage directement : la routine a pu être terminée depuis
        // le Journal pendant le snooze. Dans ce cas le timer meurt sans bruit, ce qui
        // est le comportement voulu.
        if (ShouldLock(LogicalDay.Today())) Engage();
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
        if (today != _day)
        {
            _day = today;
            if (_active) Release(LockRelease.DayChanged);   // la fenêtre affichait la veille
            if (ShouldLock(_day)) Engage();
            return;
        }

        // FILET DE SÉCURITÉ DU SNOOZE. _snoozeWatch est un DispatcherTimer, et une
        // mise en veille du PC suspend la file du dispatcher : un snooze posé juste
        // avant la veille pourrait ne jamais réveiller le verrou.
        //
        // La condition est volontairement étroite — seulement quand un snooze que
        // NOUS avons posé vient d'expirer. Un « si ShouldLock alors Engage » à chaque
        // tour reverrouillerait dans les 30 secondes dès qu'une case est décochée
        // depuis le Journal, ce qui punirait une simple correction.
        if (_snoozePending && !_db.IsSnoozed(_day)) SnoozeExpired();
    }

    public void Dispose()
    {
        _reassert.Stop();
        _dayWatch.Stop();
        _snoozeWatch.Stop();
    }
}
