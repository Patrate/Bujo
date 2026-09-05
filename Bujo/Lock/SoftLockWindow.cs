using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bujo.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HAlign = System.Windows.HorizontalAlignment;

namespace Bujo.Lock;

/// <summary>
/// Verrou non bloquant : un encart en bas à droite de l'écran principal. Dérangeant,
/// mais l'ordinateur reste utilisable.
///
/// Ce que cette fenêtre ne fait PAS, et c'est tout le sujet :
///   * aucun appel Win32. Pas de SetWindowPos, pas de WndProc, pas de ForceForeground,
///     pas de boucle de remise au premier plan. La géométrie passe par Left/Top en
///     unités WPF, ce qui règle le DPI sans un octet d'interop
///   * aucun vol de focus. ShowActivated = false, sinon l'encart prendrait le clavier
///     à chaque apparition, éventuellement en pleine frappe
///   * aucune sortie de secours. En mode souple il n'y a rien à forcer : le seul
///     bouton est le snooze, qui devient « valider » quand tout est coché
///
/// Écran principal seulement. Couvrir les moniteurs secondaires est la logique du
/// verrou bloquant, dont l'objet est de rendre le poste inutilisable — ici ce serait
/// gratuitement envahissant.
/// </summary>
public sealed class SoftLockWindow : Window
{
    private const double Gap = 18;
    private const double CalmWidth = 420;
    private const double LoudWidth = 480;

    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x50));
    private static readonly Brush Loud = new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x5A));

    private readonly JournalDb _db;
    private readonly DateOnly _day;
    private readonly int _snoozeMinutes;
    private readonly Action _onSnooze;
    private readonly Action _onComplete;

    private readonly Border _frame;
    private readonly TextBlock _title;
    private readonly RoutinePanel _panel;
    private readonly Button _action;

    private bool _allowClose;
    private bool _escalated;

    public SoftLockWindow(JournalDb db, DateOnly day, int snoozeMinutes,
                          Action onSnooze, Action onComplete)
    {
        _db = db;
        _day = day;
        _snoozeMinutes = snoozeMinutes;
        _onSnooze = onSnooze;
        _onComplete = onComplete;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        // Sans ceci, l'encart vole le clavier à chaque apparition. C'est la propriété
        // qui sépare « dérangeant » de « insupportable ».
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        SizeToContent = SizeToContent.Height;
        Width = CalmWidth;

        _title = new TextBlock
        {
            Text = "Routine du matin",
            FontSize = 19,
            FontWeight = FontWeights.Light,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 2)
        };

        var date = new TextBlock
        {
            Text = _day.ToString("dddd d MMMM"),
            FontSize = 12,
            Opacity = 0.5,
            Margin = new Thickness(0, 0, 0, 14)
        };

        _panel = new RoutinePanel(_db, _day);
        _panel.Changed += OnPanelChanged;

        // Hauteur bornée : huit habitudes dont plusieurs à saisie déborderaient de
        // l'écran, et un encart plus haut que le bureau n'est plus un encart.
        var scroll = new ScrollViewer
        {
            Content = _panel,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        _action = new Button
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(18, 9, 18, 9),
            HorizontalAlignment = HAlign.Left,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderBrush = Line,
            FontSize = 14,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        _action.Click += (_, _) =>
        {
            if (_panel.IsComplete) _onComplete();
            else _onSnooze();
        };

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(date);
        stack.Children.Add(scroll);

        var streak = RoutinePanel.Streak(_db, _day);
        if (streak > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = RoutinePanel.StreakLabel(streak),
                FontSize = 11,
                Opacity = 0.4,
                Margin = new Thickness(0, 14, 0, 0)
            });
        }

        stack.Children.Add(_action);

        _frame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x25)),
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(20),
            Child = stack
        };
        Content = _frame;

        RefreshAction();

        // SizeToContent fait varier la hauteur au fil des rendus : la position doit
        // être recalculée à chaque fois, sinon l'encart s'enfonce sous le bord bas.
        SizeChanged += (_, _) => Reposition();
        Loaded += (_, _) => Reposition();

        Closing += (_, e) => e.Cancel = !_allowClose;
    }

    /// <summary>
    /// Coin bas-droit de la zone de travail, marges comprises. SystemParameters.WorkArea
    /// est en unités WPF et tient déjà compte de la barre des tâches et du DPI — c'est
    /// exactement ce que le verrou bloquant ne peut pas utiliser, lui qui doit couvrir
    /// des moniteurs en pixels physiques.
    /// </summary>
    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - Gap;
        Top = area.Bottom - ActualHeight - Gap;
    }

    /// <summary>
    /// L'encart ne se ferme JAMAIS de lui-même, pas même une fois la routine complète.
    /// C'est la règle du verrou bloquant depuis la V1.0, et elle vaut ici : le même
    /// geste termine la routine dans les deux modes, et cocher la dernière case ne
    /// doit pas escamoter l'écran sous les doigts.
    /// </summary>
    private void OnPanelChanged() => RefreshAction();

    /// <summary>
    /// Un seul bouton, deux rôles. Il n'y a pas de sortie forcée à proposer ici : en
    /// mode souple, l'ordinateur n'a jamais été pris en otage.
    /// </summary>
    private void RefreshAction()
    {
        var ready = _panel.IsComplete;
        _action.Content = ready ? "Routine terminée — fermer" : $"Encore {_snoozeMinutes} minutes";
        _action.BorderBrush = ready
            ? new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A))
            : Line;
    }

    /// <summary>
    /// Deuxième palier : plus grand et plus contrasté, rien d'autre.
    ///
    /// Pas de texte ajouté du genre « ça fait vingt minutes » : ce serait un constat
    /// à charge, et la feuille de route interdit la réprimande. La taille et le
    /// contraste disent la même chose sans rien reprocher.
    ///
    /// Idempotent : la garde évite qu'un second appel, venu du filet de surveillance,
    /// ne relance la mise en page pour rien.
    /// </summary>
    public void Escalate()
    {
        if (_escalated) return;
        _escalated = true;

        Width = LoudWidth;
        _title.FontSize = 23;
        _frame.BorderBrush = Loud;
        _frame.BorderThickness = new Thickness(2);
        Reposition();
    }

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }
}
