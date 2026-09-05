using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bujo.Core;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Forms = System.Windows.Forms;
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Lock;

/// <summary>
/// Une instance par moniteur. Seule celle de l'écran principal est interactive ;
/// les autres ne sont qu'un cache, pour qu'on ne puisse pas travailler sur l'écran d'à côté.
/// </summary>
public sealed class LockWindow : Window
{
    private readonly JournalDb _db;
    private readonly DateOnly _day;
    private readonly Forms.Screen _screen;
    private readonly bool _interactive;
    private readonly int _snoozeMinutes;
    private readonly Action _onRoutineComplete;
    private readonly Action _onSnooze;
    private readonly Action _onBypass;

    private bool _allowClose;
    private RoutinePanel? _panel;
    private DispatcherTimer? _holdTimer;
    private DateTimeOffset _holdStart;
    private Button? _bypassButton;
    private Button? _snoozeButton;

    /// <summary>
    /// Dernier état connu du bouton de sortie, tel que RefreshExitButton l'a lu en
    /// base. Mémorisé parce que CancelHold doit pouvoir rendre au bouton son
    /// apparence de repos sans savoir laquelle, et sans interroger la base à chaque
    /// mouvement de souris — MouseLeave est un événement à haute fréquence.
    /// </summary>
    private bool _ready;

    private const int BypassHoldSeconds = 5;

    /// <summary>
    /// Opacité de la sortie de secours au repos. Plus effacée qu'en V1.1 : depuis que
    /// le snooze existe, la fuite n'est plus la seule échappatoire, et elle n'a donc
    /// plus à être la plus visible.
    /// </summary>
    private const double BypassIdleOpacity = 0.30;

    public IntPtr Handle { get; private set; }

    public LockWindow(JournalDb db, DateOnly day, Forms.Screen screen, bool interactive,
                      int snoozeMinutes, Action onRoutineComplete, Action onSnooze, Action onBypass)
    {
        _db = db;
        _day = day;
        _screen = screen;
        _interactive = interactive;
        _snoozeMinutes = snoozeMinutes;
        _onRoutineComplete = onRoutineComplete;
        _onSnooze = onSnooze;
        _onBypass = onBypass;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;          // n'apparaît pas dans la barre des tâches
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        Content = _interactive ? BuildRoutinePanel() : BuildCover();

        SourceInitialized += OnSourceInitialized;
        Closing += (_, e) => e.Cancel = !_allowClose;
    }

    // ------------------------------------------------------------------ Win32

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(Handle)?.AddHook(WndProc);

        // Positionnement en pixels physiques via SetWindowPos : cela évite toute
        // conversion DIP/DPI, qui laisse sinon des bandes libres en DPI mixte.
        var b = _screen.Bounds;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height,
            Native.SWP_SHOWWINDOW);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_SYSCOMMAND:
                var command = wParam.ToInt32() & 0xFFF0;
                // Neutralise Win+D, Win+M, Alt+Espace > Réduire, et Alt+F4.
                if (command is Native.SC_MINIMIZE or Native.SC_CLOSE or Native.SC_MAXIMIZE)
                    handled = true;
                break;

            case Native.WM_CLOSE:
                if (!_allowClose) handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    public void Reassert()
    {
        if (Handle == IntPtr.Zero) return;
        Native.PinTopmost(Handle);
        if (_interactive) Native.ForceForeground(Handle);
    }

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    // --------------------------------------------------------------------- UI

    private UIElement BuildCover() => new TextBlock
    {
        Text = "Routine du matin en cours",
        FontSize = 22,
        Opacity = 0.35,
        HorizontalAlignment = HAlign.Center,
        VerticalAlignment = VAlign.Center
    };

    private UIElement BuildRoutinePanel()
    {
        var root = new StackPanel
        {
            HorizontalAlignment = HAlign.Center,
            VerticalAlignment = VAlign.Center,
            Width = 460
        };

        root.Children.Add(new TextBlock
        {
            Text = _day.ToString("dddd d MMMM"),
            FontSize = 15,
            Opacity = 0.5,
            Margin = new Thickness(0, 0, 0, 6)
        });

        root.Children.Add(new TextBlock
        {
            Text = "Routine du matin",
            FontSize = 34,
            FontWeight = FontWeights.Light,
            Margin = new Thickness(0, 0, 0, 28)
        });

        _panel = new RoutinePanel(_db, _day);
        // Le panneau porte désormais le marquage de complétion : il ne reste ici que
        // le rafraîchissement des boutons, qui est propre à cet écran.
        _panel.Changed += RefreshExitButton;
        root.Children.Add(_panel);

        // La série remplace le compteur de sorties forcées, retiré volontairement —
        // ce qui tient debout le matin, c'est ce qu'on a déjà construit, pas le
        // rappel de ce qu'on a raté.
        var streak = RoutinePanel.Streak(_db, _day);

        if (streak > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = RoutinePanel.StreakLabel(streak),
                FontSize = 12,
                Opacity = 0.4,
                Margin = new Thickness(0, 34, 0, 0)
            });
        }

        // Le snooze est le bouton PRINCIPAL, et c'est une décision : il doit être plus
        // facile que la fuite. Un matin où l'on n'est pas prête, le geste offert est
        // « encore cinq minutes », pas « sortir quand même ».
        _snoozeButton = new Button
        {
            Content = $"Encore {_snoozeMinutes} minutes",
            Margin = new Thickness(0, 26, 0, 0),
            Padding = new Thickness(20, 11, 20, 11),
            HorizontalAlignment = HAlign.Left,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x70, 0x7C)),
            FontSize = 15,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        _snoozeButton.Click += (_, _) => _onSnooze();
        root.Children.Add(_snoozeButton);

        _bypassButton = new Button
        {
            Content = $"Maintenir {BypassHoldSeconds} s pour sortir quand même",
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HAlign.Left,
            Opacity = BypassIdleOpacity,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x50)),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        _bypassButton.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (_db.IsRoutineDone(_day)) return;   // mode vert : c'est Click qui agit
            StartHold();
        };
        _bypassButton.PreviewMouseLeftButtonUp += (_, _) => CancelHold();
        _bypassButton.MouseLeave += (_, _) => CancelHold();
        _bypassButton.Click += (_, _) =>
        {
            if (_db.IsRoutineDone(_day)) _onRoutineComplete();
        };
        root.Children.Add(_bypassButton);

        RefreshExitButton();
        
        return root;
    }

    /// <summary>Relit l'état en base, puis rend au bouton l'apparence correspondante.</summary>
    private void RefreshExitButton()
    {
        _ready = _db.IsRoutineDone(_day);
        RestoreExitButton();
    }

    /// <summary>
    /// Applique au bouton son apparence de repos, celle du dernier état connu.
    ///
    /// Séparée de RefreshExitButton parce que CancelHold doit l'appeler sans relire
    /// la base : c'est précisément l'oubli de cette restauration qui faisait
    /// réapparaître « Maintenir 5 s » sur une routine terminée, dès que le pointeur
    /// avait effleuré le bouton — MouseLeave appelant CancelHold sans condition.
    /// </summary>
    private void RestoreExitButton()
    {
        // Routine terminée : le snooze n'a plus rien à retarder, il disparaît. Le
        // laisser proposerait de repousser une chose déjà faite.
        if (_snoozeButton is not null)
            _snoozeButton.Visibility = _ready ? Visibility.Collapsed : Visibility.Visible;

        if (_bypassButton is null) return;

        _bypassButton.Content = _ready
            ? "Routine terminée — quitter"
            : $"Maintenir {BypassHoldSeconds} s pour sortir quand même";
        // Le Foreground portait un ternaire dont les deux branches étaient blanches :
        // c'est la bordure et l'opacité qui distinguent les deux états, pas le texte.
        _bypassButton.BorderBrush = _ready
            ? new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A))
            : new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x50));
        _bypassButton.Opacity = _ready ? 1.0 : BypassIdleOpacity;
    }

    // ----------------------------------------------------- sortie de secours

    private void StartHold()
    {
        _holdStart = DateTimeOffset.UtcNow;
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _holdTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTimeOffset.UtcNow - _holdStart).TotalSeconds;
            if (elapsed >= BypassHoldSeconds)
            {
                CancelHold();
                _db.LogLockEvent(_day, "bypassed");
                _onBypass();
                return;
            }
            var left = BypassHoldSeconds - (int)elapsed;
            if (_bypassButton is not null)
            {
                _bypassButton.Content = $"Continue de maintenir… {left} s";
                _bypassButton.Opacity = BypassIdleOpacity
                                      + (1.0 - BypassIdleOpacity) * (elapsed / BypassHoldSeconds);
            }
        };
        _holdTimer.Start();
    }

    private void CancelHold()
    {
        _holdTimer?.Stop();
        _holdTimer = null;
        RestoreExitButton();
    }
}
