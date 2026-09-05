using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bujo.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HAlign = System.Windows.HorizontalAlignment;
using TabControl = System.Windows.Controls.TabControl;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Ui;

/// <summary>
/// Coquille de l'application. Chaque onglet reçoit sa vue ; celles qui savent se
/// recharger implémentent IRefreshable et sont réveillées à l'entrée dans l'onglet.
/// </summary>
public sealed class MainWindow : Window
{
    public static readonly Brush Bg    = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A));
    public static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x25));
    public static readonly Brush Line  = new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x50));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x9C));

    private readonly Settings _settings;

    /// <summary>Levé quand l'utilisatrice ferme réellement l'app (et non un repli en zone de notification).</summary>
    public event Action? QuitRequested;
    public event Action? RoutineChanged;

    /// <summary>Relayé depuis les Paramètres : « Revoir la présentation ».</summary>
    public event Action? SetupRequested;

    public MainWindow(JournalDb db, Settings settings, BackupService backupService)
    {
        _settings = settings;

        Title = "Bujo";
        Width = 900;
        Height = 620;
        MinWidth = 640;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        var tabs = new TabControl
        {
            Background = Bg,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(8)
        };

        var routineView = new RoutineConfigView(db, settings);
        routineView.Applied += () => RoutineChanged?.Invoke();

        var journalView = new JournalView(db);
        var statsView = new StatsView(db);
        var settingsView = new SettingsView(settings, db, backupService);

        tabs.Items.Add(Tab("Routine", routineView));
        tabs.Items.Add(Tab("Journal", journalView));
        tabs.Items.Add(Tab("Suivi", statsView));
        tabs.Items.Add(Tab("Paramètres", settingsView));

        // Une liste plutôt qu'un index codé en dur : c'est le if (SelectedIndex == 1)
        // précédent qui laissait le Suivi obsolète après l'ajout d'une habitude, et
        // qui aurait reproduit le bug au prochain onglet ajouté.
        IRefreshable[] views = [routineView, journalView, statsView];

        settingsView.SetupRequested += () => SetupRequested?.Invoke();

        settingsView.DataChanged += () =>
        {
            // Refresh et non Activate : effacer l'historique est une opération de
            // maintenance, elle n'a pas à ramener le Journal sur aujourd'hui.
            foreach (var view in views) view.Refresh();
        };

        settingsView.DayOffsetChanged += () =>
        {
            // Activate et non Refresh : décaler le jour logique EST une entrée dans
            // un autre jour. Le Journal doit suivre, sinon il continuerait d'afficher
            // le jour d'avant tout en se disant à jour.
            foreach (var view in views) view.Activate();
        };

        Content = tabs;

        Closing += (_, e) =>
        {
            if (_settings.CloseAction != CloseAction.MinimizeToTray) return;
            e.Cancel = true;   // repli en zone de notification, l'app reste vivante
            Hide();
        };

        Closed += (_, _) => QuitRequested?.Invoke();
        
        // PIÈGE WPF : Selector.SelectionChanged est un événement routé BOUILLONNANT.
        // Chaque ComboBox ou ListBox posée dans un onglet — type d'habitude, genre
        // d'entrée du journal, liste des habitudes du Suivi, heure de bascule — le
        // fait remonter jusqu'au TabControl. Sans ce filtre, choisir « Note » dans
        // le Journal appellerait Activate() et renverrait la vue à aujourd'hui en
        // pleine consultation d'un jour passé.
        tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, tabs)) return;
            if (tabs.SelectedItem is TabItem { Tag: IRefreshable view }) view.Activate();
        };
    }

    /// <summary>
    /// Le Tag porte la vue elle-même : le Content est une Border intermédiaire,
    /// aller la rechercher par son enfant serait fragile au premier habillage.
    /// </summary>
    private static TabItem Tab(string header, UIElement content) => new()
    {
        Header = header,
        Foreground = Brushes.Black,
        Tag = content as IRefreshable,
        Content = new Border
        {
            Background = Panel,
            Padding = new Thickness(24),
            Child = content
        }
    };

    private static UIElement Placeholder(string title, string detail)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HAlign.Center,
            VerticalAlignment = VAlign.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            Foreground = Brushes.White,
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 13,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            TextAlignment = TextAlignment.Center
        });
        return panel;
    }

    /// <summary>Ramène la fenêtre au premier plan, qu'elle soit cachée ou réduite.</summary>
    public void Reveal()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;    // sinon la fenêtre s'ouvre derrière si le focus est ailleurs
        Topmost = false;
    }
}
