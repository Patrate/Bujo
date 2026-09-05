using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bujo.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HAlign = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Ui;

/// <summary>
/// Présentation de premier lancement. Modale, cinq pages, un ordre qui n'est pas
/// négociable : chaque page suppose la précédente comprise.
///
///   1. Ce que fait Bujo
///   2. Comment il vit sur la machine — zone de notification, démarrage automatique
///   3. Le mode de verrouillage
///   4. Le rythme — début du jour logique, durée du snooze
///   5. Les habitudes, puis « Terminer » ou « Terminer et verrouiller maintenant »
///
/// Les habitudes viennent EN DERNIER parce que tout le reste conditionne le sens
/// qu'elles ont : à quelle heure la journée commence, ce qui se passe quand la routine
/// n'est pas faite, et si l'application sera seulement lancée le matin.
///
/// Chaque réglage s'écrit à la volée, comme dans l'onglet Paramètres. Il n'y a donc
/// aucun « annuler » à la fermeture, et c'est voulu : fermer en cours de route laisse
/// ce qui a été choisi, pas un état à moitié appliqué.
/// </summary>
public sealed class SetupWindow : Window
{
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x9C));

    private readonly Settings _settings;
    private readonly List<UIElement> _pages = [];
    private readonly ContentControl _host = new();
    private readonly TextBlock _step = new();
    private readonly Button _back;
    private readonly Button _next;

    /// <summary>Nullable le temps du constructeur : voir RefreshLockNow.</summary>
    private readonly Button? _finishAndLock;

    private readonly RoutineConfigView _routine;

    private int _index;

    /// <summary>
    /// Levé à la fermeture. Le booléen dit si l'utilisatrice a demandé à voir le verrou
    /// tout de suite : « Terminer » n'engage rien et laisse le verrou apparaître demain
    /// matin, « Terminer et verrouiller maintenant » le déclenche sur-le-champ.
    ///
    /// C'est ce couple de boutons qui règle le problème du premier jour, et il le règle
    /// mieux qu'un état en base : terminer l'assistant à 15 h ne doit pas produire un
    /// plein écran cinq secondes après l'installation, avant la moindre confiance
    /// accordée à l'outil.
    /// </summary>
    public event Action<bool>? Finished;

    public SetupWindow(JournalDb db, Settings settings)
    {
        _settings = settings;

        Title = "Bienvenue dans Bujo";
        Width = 720;
        Height = 620;
        MinWidth = 640;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = MainWindow.Bg;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        // Mode intégré : le pied de page « Annuler / Appliquer » de la vue est masqué,
        // et c'est le bouton de l'assistant qui déclenche l'application. Sans cela,
        // l'événement Applied ferait engager le verrou PAR-DESSUS cette fenêtre.
        _routine = new RoutineConfigView(db, settings, embedded: true);
        // Le bouton dépend du contenu du brouillon : sans cet abonnement, il resterait
        // figé sur l'état trouvé à l'entrée dans la page, alors que c'est précisément
        // là qu'on saisit ses premières habitudes.
        _routine.DraftChanged += RefreshLockNow;

        _pages.Add(PageWelcome());
        _pages.Add(PageMachine());
        _pages.Add(PageMode());
        _pages.Add(PageRhythm());
        _pages.Add(PageHabits());

        _back = Secondary("Retour", () => Go(_index - 1));
        _next = Primary("Suivant", () => Go(_index + 1));
        _finishAndLock = Secondary("Terminer et verrouiller maintenant", () => Close(lockNow: true));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HAlign.Right
        };
        actions.Children.Add(_finishAndLock);
        actions.Children.Add(_back);
        actions.Children.Add(_next);

        _step.FontSize = 12;
        _step.Foreground = Dim;
        _step.VerticalAlignment = VAlign.Center;

        var footer = new Grid { Margin = new Thickness(28, 16, 28, 24) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_step, 0);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(_step);
        footer.Children.Add(actions);

        _host.Margin = new Thickness(28, 24, 28, 0);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_host);
        Content = root;

        // Le drapeau est posé à l'OUVERTURE et non à la dernière page : fermer en cours
        // de route vaut « vu ». Reproposer une fenêtre qu'on vient d'écarter serait le
        // premier agacement d'une application dont c'est la première impression.
        _settings.SetupDone = true;

        Go(0);
    }

    // ------------------------------------------------------------ navigation

    private void Go(int index)
    {
        _index = Math.Clamp(index, 0, _pages.Count - 1);
        _host.Content = _pages[_index];

        var last = _index == _pages.Count - 1;
        _step.Text = $"Étape {_index + 1} sur {_pages.Count}";
        _back.Visibility = _index == 0 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = last ? "Terminer" : "Suivant";

        if (_finishAndLock is not null)
            _finishAndLock.Visibility = last ? Visibility.Visible : Visibility.Collapsed;

        if (last) RefreshLockNow();
    }

    /// <summary>
    /// « Terminer et verrouiller maintenant » ne s'offre que si le verrou peut
    /// réellement apparaître : sans habitude due aujourd'hui, il promettrait un écran
    /// qui ne viendrait jamais.
    ///
    /// La garde de nullité n'est pas décorative : DraftChanged part dès le constructeur
    /// de la vue, c'est-à-dire avant que les boutons de l'assistant n'existent.
    /// </summary>
    private void RefreshLockNow()
    {
        if (_finishAndLock is null) return;
        _finishAndLock.IsEnabled = _routine.HasWorkToday;
    }

    private void Close(bool lockNow)
    {
        // Le brouillon d'habitudes n'est écrit qu'ici : la vue est en mode intégré,
        // son propre bouton « Appliquer » n'existe pas.
        //
        // Un refus — horaire hebdomadaire sans jour coché, suppression non confirmée —
        // laisse la fenêtre ouverte. La vue a déjà expliqué pourquoi et signalé les
        // lignes fautives ; fermer ici laisserait croire que tout a été enregistré.
        if (!_routine.ApplyFromHost()) return;

        Finished?.Invoke(lockNow);
        base.Close();
    }

    // ---------------------------------------------------------------- pages

    private UIElement PageWelcome()
    {
        var page = NewPage("Bujo",
            "Un bullet journal qui commence par une routine.");

        page.Children.Add(Paragraph(
            "Chaque matin, Bujo affiche les habitudes que tu t'es fixées et ne te laisse "
            + "pas passer à autre chose tant qu'elles ne sont pas cochées."));
        page.Children.Add(Paragraph(
            "Le reste — le journal du jour, les tâches, le suivi de tes séries — vit dans "
            + "la fenêtre principale, et s'utilise quand tu veux."));
        page.Children.Add(Paragraph(
            "Les cinq écrans qui suivent règlent l'essentiel. Tout est modifiable ensuite "
            + "dans l'onglet Paramètres, et cette présentation peut être revue à tout moment."));
        return page;
    }

    private UIElement PageMachine()
    {
        var page = NewPage("Sur ton ordinateur",
            "Bujo reste discrètement en fond.");

        page.Children.Add(Paragraph(
            "Fermer la fenêtre ne quitte pas l'application : elle se replie dans la zone "
            + "de notification, à côté de l'horloge. C'est ce qui lui permet de surveiller "
            + "le passage à un nouveau jour. Pour quitter vraiment, fais un clic droit sur "
            + "son icône puis « Quitter »."));

        var autostart = new CheckBox
        {
            IsChecked = _settings.StartWithWindows,
            Content = "Démarrer Bujo avec Windows",
            Foreground = Brushes.White,
            FontSize = 14,
            Margin = new Thickness(0, 10, 0, 0)
        };
        autostart.Checked += (_, _) => _settings.StartWithWindows = true;
        autostart.Unchecked += (_, _) => _settings.StartWithWindows = false;
        page.Children.Add(autostart);

        page.Children.Add(Hint(
            "Fortement recommandé. Sans cela, Bujo n'est pas lancé au moment où tu allumes "
            + "ton ordinateur, et la routine du matin ne s'affiche jamais."));
        return page;
    }

    private UIElement PageMode()
    {
        var page = NewPage("Le matin",
            "Comment Bujo se rappelle à toi.");

        var soft = new RadioButton
        {
            Content = "Souple — un encart dans un coin de l'écran",
            IsChecked = _settings.LockMode == LockMode.Soft,
            GroupName = "SetupMode",
            Foreground = Brushes.White,
            FontSize = 14,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 6, 0, 0)
        };
        soft.Checked += (_, _) => _settings.LockMode = LockMode.Soft;
        page.Children.Add(soft);
        page.Children.Add(Hint(
            "Dérangeant mais jamais bloquant : tu peux continuer à te servir de ton "
            + "ordinateur. L'encart grossit au bout de vingt minutes si tu l'ignores."));

        var hard = new RadioButton
        {
            Content = "Bloquant — un écran qui prend toute la place",
            IsChecked = _settings.LockMode == LockMode.Hard,
            GroupName = "SetupMode",
            Foreground = Brushes.White,
            FontSize = 14,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 14, 0, 0)
        };
        hard.Checked += (_, _) => _settings.LockMode = LockMode.Hard;
        page.Children.Add(hard);
        page.Children.Add(Hint(
            "L'ordinateur n'est utilisable qu'une fois la routine cochée. Tu n'es jamais "
            + "piégée pour autant : une sortie de secours est toujours affichée, par un "
            + "appui maintenu de cinq secondes."));

        page.Children.Add(Paragraph(
            "Dans les deux cas, un bouton permet de repousser de quelques minutes. "
            + "Ce report ne compte nulle part et ne t'est jamais reproché."));
        return page;
    }

    private UIElement PageRhythm()
    {
        var page = NewPage("Ton rythme",
            "Deux réglages, tous deux modifiables plus tard.");

        var hours = new ComboBox { Width = 110, HorizontalAlignment = HAlign.Left };
        for (var h = 0; h <= 12; h++) hours.Items.Add($"{h:00}:00");
        hours.SelectedIndex = Math.Clamp(_settings.CutoffHour, 0, 12);
        hours.SelectionChanged += (_, _) => _settings.CutoffHour = hours.SelectedIndex;

        page.Children.Add(Label("Début de la journée"));
        page.Children.Add(hours);
        page.Children.Add(Hint(
            "Avant cette heure, Bujo considère que tu es encore la veille. Se coucher à "
            + "2 h du matin ne fait donc pas basculer le jour, et la routine de la veille "
            + "reste ouverte."));

        var snooze = new ComboBox { Width = 110, HorizontalAlignment = HAlign.Left, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var m in Settings.SnoozeChoices) snooze.Items.Add($"{m} min");
        snooze.SelectedIndex = Math.Max(0, Array.IndexOf(Settings.SnoozeChoices, _settings.SnoozeMinutes));
        snooze.SelectionChanged += (_, _) =>
            _settings.SnoozeMinutes = Settings.SnoozeChoices[snooze.SelectedIndex];

        page.Children.Add(Label("Durée du report"));
        page.Children.Add(snooze);
        page.Children.Add(Hint(
            "Le temps que gagne le bouton « Encore N minutes ». Assez pour aller chercher "
            + "un café, pas assez pour oublier."));
        return page;
    }

    private UIElement PageHabits()
    {
        var head = NewPage("Tes habitudes",
            "Ce que tu veux faire chaque matin avant tout le reste.");

        head.Children.Add(Hint(
            "Trois ou quatre lignes suffisent pour commencer. Une habitude peut être une "
            + "simple case, un nombre ou un texte, et n'est pas forcément quotidienne — "
            + "le bouton d'horaire de chaque ligne le règle. Tu pourras en ajouter et en "
            + "retirer quand tu veux."));

        // DockPanel et non StackPanel, contrairement aux autres pages : la vue porte sa
        // PROPRE zone défilante. Dans un StackPanel, la hauteur disponible est infinie,
        // le ScrollViewer interne ne défilerait donc jamais et la liste déborderait de
        // la fenêtre dès la cinquième habitude. Ici la vue reçoit la place restante et
        // gère son défilement elle-même.
        // La vue complète plutôt qu'une saisie simplifiée : découvrir après coup des
        // options dont on avait besoin est plus agaçant qu'une page un peu dense.
        _routine.Margin = new Thickness(0, 12, 0, 0);

        var page = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        page.Children.Add(head);
        page.Children.Add(_routine);
        return page;
    }

    // ------------------------------------------------------------ fabriques

    private static StackPanel NewPage(string title, string subtitle)
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 28,
            FontWeight = FontWeights.Light,
            Foreground = Brushes.White
        });
        page.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 14,
            Foreground = Dim,
            Margin = new Thickness(0, 4, 0, 20),
            TextWrapping = TextWrapping.Wrap
        });
        return page;
    }

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        FontSize = 14,
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 560,
        HorizontalAlignment = HAlign.Left,
        Margin = new Thickness(0, 0, 0, 14),
        LineHeight = 22
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Dim,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 560,
        HorizontalAlignment = HAlign.Left,
        Margin = new Thickness(0, 6, 0, 0),
        LineHeight = 18
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 14,
        Foreground = Brushes.White,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static Button Primary(string label, Action onClick) => Make(label, onClick, 1.0);

    private static Button Secondary(string label, Action onClick) => Make(label, onClick, 0.7);

    private static Button Make(string label, Action onClick, double opacity)
    {
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(18, 8, 18, 8),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderBrush = MainWindow.Line,
            Cursor = Cursors.Hand,
            Opacity = opacity
        };
        b.Click += (_, _) => onClick();
        return b;
    }
}
