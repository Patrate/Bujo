using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Bujo.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
// System.Windows.Forms a perdu ContextMenu et MenuItem au passage à .NET Core,
// mais les aliases coûtent zéro et ferment la question pour de bon.
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using HAlign = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Ui;

/// <summary>
/// Édition de la liste d'habitudes. Les modifications restent en brouillon jusqu'à
/// « Appliquer » : rien n'est écrit tant que tu n'as pas confirmé.
/// Réordonnancement par glisser-déposer depuis la poignée à gauche de chaque ligne.
/// </summary>
public sealed class RoutineConfigView : DockPanel, IRefreshable
{
    private sealed class Draft
    {
        public string? Id;              // null = habitude à créer
        public string Name = "";
        public HabitValueType Type = HabitValueType.Bool;
        public bool IsRoutine = true;
        public bool Active = true;
        public bool Archived;

        /// <summary>
        /// Suppression franche en attente. Distincte d'Archived : l'archivage garde
        /// l'historique lisible, ceci l'efface. Reste un brouillon jusqu'à
        /// « Appliquer », donc « Annuler » la défait — la seule chance d'annuler
        /// qu'aura jamais cette opération.
        /// </summary>
        public bool Deleted;

        /// <summary>
        /// Horaire en cours d'édition. Chargé depuis la base comme le reste : sans
        /// cela, « Appliquer » écrirait un horaire quotidien par défaut et écraserait
        /// silencieusement le réglage de chaque habitude à chaque application.
        /// </summary>
        public Schedule Schedule = default;

        /// <summary>
        /// Posé quand une application a été refusée à cause de cet horaire, pour que
        /// le message n'oblige pas à retrouver la ligne à l'œil. Effacé dès que
        /// l'horaire est réédité.
        /// </summary>
        public bool ScheduleInvalid;

        /// <summary>Un mode hebdomadaire sans aucun jour coché n'est jamais dû : refusé à l'application.</summary>
        public bool ScheduleIsEmpty =>
            Schedule.Kind == ScheduleKind.Weekly && Schedule.Weekdays == 0;
    }

    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xD1, 0x50, 0x50));
    private const string DragFormat = "BujoHabitDraft";

    private readonly JournalDb _db;
    private readonly Settings _settings;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status;
    private List<Draft> _drafts = [];

    // État du geste en cours. Le glissement ne démarre qu'au-delà du seuil système,
    // sinon un simple clic sur la poignée déclencherait un drag parasite.
    private Point _pressOrigin;
    private Draft? _pressed;
    private readonly Button _apply;
    private readonly Button _cancel;
    private readonly SolidColorBrush _applyBorder = new(((SolidColorBrush)MainWindow.Line).Color);
    private bool _dirty;

    /// <summary>
    /// Popup d'horaire ouvert, s'il y en a un. Un seul à la fois, et il faut pouvoir
    /// le fermer de l'extérieur : un popup est une fenêtre à part, il ne suit ni le
    /// défilement de la liste ni la ligne qu'il édite si celle-ci part en glissement.
    /// </summary>
    private Popup? _schedulePopup;

    /// <summary>Zone défilante de la liste. Gardée pour fermer le popup au défilement.</summary>
    private readonly ScrollViewer _scroll;


    /// <summary>Levé après écriture en base, pour que l'appelant reteste le verrou.</summary>
    public event Action? Applied;

    /// <summary>
    /// Levé à chaque modification du brouillon, avant toute écriture. Sert à un hôte
    /// qui affiche un bouton dépendant du contenu — « Terminer et verrouiller
    /// maintenant » de la présentation — et qui doit le rafraîchir à la saisie, pas
    /// seulement à l'entrée dans la page.
    /// </summary>
    public event Action? DraftChanged;

    /// <summary>
    /// Vue embarquée dans un hôte qui fournit ses propres boutons — la présentation de
    /// premier lancement. Le pied de page « Annuler / Appliquer » disparaît, l'écriture
    /// passe par <see cref="ApplyFromHost"/>, et l'événement Applied n'est PAS levé :
    /// il ferait engager le verrou par-dessus la fenêtre modale de l'hôte.
    /// </summary>
    private readonly bool _embedded;

    public RoutineConfigView(JournalDb db, Settings settings, bool embedded = false)
    {
        _db = db;
        _settings = settings;
        _embedded = embedded;

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        if (_embedded) footer.Visibility = Visibility.Collapsed;

        _status = new TextBlock
        {
            Foreground = MainWindow.Muted,
            FontSize = 12,
            VerticalAlignment = VAlign.Center,
            TextWrapping = TextWrapping.Wrap
        };

        _cancel = MakeButton("Annuler", () => Load());
        // Lambda et non groupe de méthodes : ApplyChanges rend désormais un bool.
        _apply = MakeButton("Appliquer", () => ApplyChanges());
        _apply.Margin = new Thickness(8, 0, 0, 0);
        // Pinceau non figé et propre à ce bouton : une animation ne peut pas
        // cibler un pinceau partagé entre plusieurs contrôles.
        _apply.BorderBrush = _applyBorder;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_apply);
        footer.Children.Add(buttons);
        footer.Children.Add(_status);

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(header, Dock.Top);
        // L'hôte pose déjà son propre titre et son propre texte d'explication.
        if (_embedded) header.Visibility = Visibility.Collapsed;
        header.Children.Add(new TextBlock
        {
            Text = "Habitudes",
            FontSize = 18,
            Foreground = Brushes.White
        });
        header.Children.Add(new TextBlock
        {
            Text = "Une habitude « dans la routine » bloque l'écran tant qu'elle n'est pas validée, "
                 + "les jours où elle est due. Le bouton d'horaire fixe ces jours ; changer l'horaire "
                 + "recalcule aussi les statistiques passées. "
                 + "Après application, si la routine du jour n'est pas complète, l'écran se verrouille immédiatement. "
                 + "Glisse la poignée à gauche pour réordonner.",
            FontSize = 12,
            Foreground = MainWindow.Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var add = MakeButton("+  Ajouter une habitude", () =>
        {
            _drafts.Add(new Draft());
            MarkDirty();
        });
        add.HorizontalAlignment = HAlign.Left;
        add.Margin = new Thickness(0, 12, 0, 0);

        var scrollContent = new StackPanel();
        scrollContent.Children.Add(_rows);
        scrollContent.Children.Add(add);

        Children.Add(footer);
        Children.Add(header);

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = scrollContent
        };
        // PIÈGE WPF : un Popup est une fenêtre distincte, posée en coordonnées écran.
        // Il ne suit pas le défilement de la liste et resterait planté en place,
        // détaché de la ligne qu'il édite. Le recoller à chaque défilement demande de
        // faire varier un de ses offsets pour forcer le repositionnement, ce qui est
        // laid ; le fermer est ce que fait n'importe quel menu, et c'est sans perte
        // puisque « Annuler » et le clic à l'extérieur ferment déjà sans écrire.
        // Filtre sur la source, exactement pour la raison documentée dans MainWindow
        // à propos de SelectionChanged : ScrollChanged bouillonne lui aussi. La liste
        // déroulante d'un ComboBox de type d'habitude le ferait remonter jusqu'ici et
        // fermerait un popup que personne n'a touché.
        _scroll.ScrollChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _scroll)) CloseSchedulePopup();
        };

        Children.Add(_scroll);

        Load();
    }

    // ------------------------------------------------------------- chargement

    /// <summary>
    /// Relecture depuis la base, sauf si un brouillon attend d'être appliqué.
    ///
    /// Sans cette garde, saisir trois habitudes, passer voir le Suivi et revenir
    /// effacerait la saisie sans un mot. L'application est explicite ; l'abandon
    /// doit l'être aussi, et c'est le rôle du bouton « Annuler ».
    /// </summary>
    public void Refresh()
    {
        if (_dirty) return;
        Load();
    }

    private void Load()
    {
        _drafts = _db.GetHabits().Select(h => new Draft
        {
            Id = h.Id,
            Name = h.Name,
            Type = h.Type,
            IsRoutine = h.IsRoutine,
            Active = h.Active,
            Schedule = h.Schedule
        }).ToList();
        _dirty = false;
        RenderRows();
        UpdateButtons();
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        var visible = _drafts.Where(d => !d.Archived && !d.Deleted).ToList();

        foreach (var draft in visible)
            _rows.Children.Add(BuildRow(draft));

        var routine = visible.Where(d => d.IsRoutine && d.Active).ToList();
        var routineCount = routine.Count;

        // Deux causes bien distinctes à une routine vide, et depuis la V1.2 la seconde
        // est ordinaire : aucune habitude configurée, ou des habitudes configurées dont
        // aucune n'est due aujourd'hui. Dire « aucune habitude dans la routine » dans
        // le second cas serait faux, et laisserait croire à une configuration perdue.
        //
        // Seule la règle est consultée, pas la borne de création : ce jour-ci ne peut
        // pas précéder la création d'une habitude qu'on est en train d'éditer.
        var today = LogicalDay.Today();
        var dueToday = routine.Count(d => d.Schedule.IsDue(today));

        string text;
        if (routineCount == 0)
            text = "Aucune habitude dans la routine : l'écran de verrouillage n'apparaîtra pas.";
        else if (dueToday == 0)
            text = $"{routineCount} habitude{(routineCount > 1 ? "s" : "")} dans la routine, "
                 + "mais aucune n'est prévue aujourd'hui : l'écran ne se verrouillera pas.";
        else if (dueToday < routineCount)
            text = $"{routineCount} habitudes dans la routine du matin, "
                 + $"dont {dueToday} prévue{(dueToday > 1 ? "s" : "")} aujourd'hui.";
        else
            text = $"{routineCount} habitude{(routineCount > 1 ? "s" : "")} dans la routine du matin.";

        // Une suppression en attente ne doit jamais être discrète : la ligne a
        // disparu de la liste, seul ce texte dit ce qui va réellement se passer.
        var doomed = _drafts.Count(d => d.Deleted && d.Id is not null);
        if (doomed > 0)
            text += doomed == 1
                ? "  1 habitude et tout son historique seront supprimés définitivement à l'application."
                : $"  {doomed} habitudes et tout leur historique seront supprimées définitivement à l'application.";

        _status.Text = text;
        _status.Foreground = doomed > 0 ? Danger : MainWindow.Muted;
    }

    private UIElement BuildRow(Draft d)
    {
        var grid = new Grid();
        // Poignée, nom (extensible), type, horaire, routine, corbeille.
        foreach (var star in new[] { false, true, false, false, false, false })
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });

        // La bordure porte l'indicateur d'insertion (trait vert au-dessus ou en dessous).
        var border = new Border
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 5, 0, 5),
            Margin = new Thickness(0, 1, 0, 1),
            Child = grid,
            AllowDrop = true,
            Tag = d
        };

        var grip = new TextBlock
        {
            Text = "⠿",
            FontSize = 17,
            Foreground = MainWindow.Muted,
            Padding = new Thickness(4, 0, 10, 0),
            Cursor = Cursors.SizeAll,
            VerticalAlignment = VAlign.Center,
            ToolTip = "Glisser pour réordonner",
            Background = Brushes.Transparent   // sinon la zone transparente n'est pas cliquable
        };
        grip.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressed = d;
            _pressOrigin = e.GetPosition(null);
        };
        grip.PreviewMouseMove += (_, e) => MaybeStartDrag(e, border);
        grip.PreviewMouseLeftButtonUp += (_, _) => _pressed = null;
        Grid.SetColumn(grip, 0);
        grid.Children.Add(grip);

        var name = Input(d.Name, 0);
        name.TextChanged += (_, _) => { d.Name = name.Text; MarkDirty(false); };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var type = new ComboBox
        {
            Width = 110,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VAlign.Center,
            AllowDrop = false
        };
        type.Items.Add("Case");
        type.Items.Add("Nombre");
        type.Items.Add("Texte");
        type.SelectedIndex = d.Type switch
        {
            HabitValueType.Number => 1,
            HabitValueType.Text => 2,
            _ => 0
        };
        type.SelectionChanged += (_, _) =>
        {
            d.Type = type.SelectedIndex switch
            {
                1 => HabitValueType.Number,
                2 => HabitValueType.Text,
                _ => HabitValueType.Bool
            };
            MarkDirty(false);
        };
        Grid.SetColumn(type, 2);
        grid.Children.Add(type);

        var schedule = ScheduleButton(d);
        Grid.SetColumn(schedule, 3);
        grid.Children.Add(schedule);

        var inRoutine = new CheckBox
        {
            Content = "Routine",
            IsChecked = d.IsRoutine && d.Active,
            Foreground = Brushes.White,
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(12, 0, 4, 0),
            ToolTip = "Décoché : l'habitude est conservée et suivie, mais ne bloque plus l'écran."
        };
        inRoutine.Checked += (_, _) => { d.IsRoutine = true; d.Active = true; MarkDirty(); };
        inRoutine.Unchecked += (_, _) => { d.IsRoutine = false; MarkDirty(); };
        Grid.SetColumn(inRoutine, 4);
        grid.Children.Add(inRoutine);

        var trash = SmallButton("🗑", () => { });
        trash.ToolTip = "Archiver ou supprimer";
        trash.ContextMenu = BuildTrashMenu(trash, d);
        // Clic gauche ouvre aussi le menu : sur une action à deux issues dont l'une
        // est irréversible, le geste ne doit pas trancher tout seul.
        trash.Click += (_, _) => trash.ContextMenu.IsOpen = true;
        Grid.SetColumn(trash, 5);
        grid.Children.Add(trash);

        border.PreviewDragOver += (_, e) => ShowInsertionMark(border, e);
        border.PreviewDragLeave += (_, _) => ClearMark(border);
        border.PreviewDrop += (_, e) => HandleDrop(border, e);

        return border;
    }

    /// <summary>
    /// Menu de la corbeille. Deux issues très différentes, d'où le menu plutôt qu'un
    /// bouton : archiver conserve l'historique, supprimer l'efface.
    ///
    /// PIÈGE WPF : un ContextMenu vit dans son propre arbre visuel, en dehors de la
    /// fenêtre. Il n'hérite donc RIEN du thème sombre de l'application et s'affiche
    /// sur le fond clair du système — un libellé blanc y serait invisible. Les
    /// couleurs sont posées ici, à la main.
    /// </summary>
    private ContextMenu BuildTrashMenu(UIElement target, Draft d)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            Background = MainWindow.Panel,
            BorderBrush = MainWindow.Line
        };

        var archive = new MenuItem
        {
            Header = "Archiver",
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            ToolTip = "Retire l'habitude de la configuration. Son historique reste lisible dans le Suivi."
        };
        archive.Click += (_, _) =>
        {
            // Une habitude jamais écrite en base n'a rien à archiver : on la retire.
            if (d.Id is null) _drafts.Remove(d);
            else d.Archived = true;
            MarkDirty();
        };
        menu.Items.Add(archive);

        var delete = new MenuItem
        {
            Header = "Supprimer définitivement",
            Foreground = Danger,
            Background = Brushes.Transparent,
            ToolTip = "Efface l'habitude ET toutes ses entrées. Irréversible une fois appliqué."
        };
        delete.Click += (_, _) =>
        {
            if (d.Id is null) _drafts.Remove(d);
            else d.Deleted = true;
            MarkDirty();
        };
        menu.Items.Add(delete);

        // Visibilité décidée à l'ouverture et non à la construction : la case du mode
        // développeur peut être cochée dans l'onglet Paramètres sans que cette vue
        // soit reconstruite entre-temps.
        menu.Opened += (_, _) =>
            delete.Visibility = _settings.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;

        return menu;
    }

    // -------------------------------------------------------------- horaire

    /// <summary>
    /// Bouton d'horaire de la ligne. Il affiche l'état courant en abrégé et ouvre le
    /// popup d'édition ; la bordure passe au rouge quand une application vient d'être
    /// refusée à cause de cet horaire.
    /// </summary>
    private Button ScheduleButton(Draft d)
    {
        var label = new TextBlock
        {
            Text = d.Schedule.DescribeShort(),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var b = new Button
        {
            Content = label,
            MinWidth = 118,
            MaxWidth = 160,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            Background = Brushes.Transparent,
            Foreground = d.ScheduleInvalid ? Danger : Brushes.White,
            BorderBrush = d.ScheduleInvalid ? Danger : MainWindow.Line,
            Cursor = Cursors.Hand,
            VerticalAlignment = VAlign.Center,
            // Le libellé abrégé peut être tronqué : l'infobulle porte la version longue.
            ToolTip = $"Horaire : {d.Schedule.Describe()}.\n"
                    + "Changer l'horaire recalcule aussi les statistiques passées.",
            AllowDrop = false
        };
        b.Click += (_, _) => OpenSchedulePopup(b, d);
        return b;
    }

    /// <summary>
    /// Éditeur d'horaire. Travaille sur une COPIE : c'est un brouillon dans le
    /// brouillon. « Terminé » écrit dans le Draft, « Annuler », Échap et le clic à
    /// l'extérieur ferment sans rien écrire — le clic à l'extérieur vaut abandon et
    /// non validation silencieuse, sinon « Terminé » ne voudrait rien dire.
    ///
    /// Un mode hebdomadaire sans jour coché n'est pas empêché ici : la fenêtre le
    /// signale et c'est « Appliquer » qui refuse. Interdire de décocher la dernière
    /// case, ou la recocher dans le dos, serait agir contre le geste en cours.
    /// </summary>
    private void OpenSchedulePopup(Button anchor, Draft d)
    {
        CloseSchedulePopup();

        var working = d.Schedule;
        // Garde-fou de réentrance : Sync() repose les IsChecked des sept bascules, ce
        // qui relance leurs gestionnaires, qui rappelleraient Sync(). Classique dès
        // qu'un état est reflété dans des contrôles qui le modifient aussi.
        var syncing = false;

        // PIÈGE WPF, et il est vicieux : une ComboBox dans un Popup à StaysOpen =
        // false se saborde. Sa liste déroulante est un popup imbriqué, dans un autre
        // arbre visuel, si bien que le clic sur un élément est compté comme un clic à
        // l'extérieur — le popup parent se referme à l'instant précis du choix.
        // Trois boutons radio n'ouvrent rien et ferment la question.
        var modePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var modeDaily = ModeOption("Tous les jours");
        var modeWeekly = ModeOption("Jours de la semaine");
        var modeInterval = ModeOption("Tous les X jours");
        modePanel.Children.Add(modeDaily);
        modePanel.Children.Add(modeWeekly);
        modePanel.Children.Add(modeInterval);

        // ORDRE IMPOSÉ PAR LE COMPILATEUR, et le motif mérite d'être écrit : Sync()
        // est une fonction locale, et C# exige que TOUTES les variables qu'elle
        // capture soient assignées au point d'appel — y compris quand cet appel est
        // enfoui dans un lambda différé, dont l'analyse se fait à sa position dans le
        // texte (CS0165). Tous les contrôles que Sync touche sont donc déclarés ici,
        // avant le moindre gestionnaire ; leur contenu est rempli plus bas.
        var toggles = new List<ToggleButton>(7);
        var weekPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var intervalPanel = new StackPanel();

        var intervalValue = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 13,
            MinWidth = 26,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VAlign.Center
        };

        var anchorValue = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 13,
            MinWidth = 128,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VAlign.Center
        };

        var warning = new TextBlock
        {
            Text = "Aucun jour coché : l'application sera refusée.",
            Foreground = Danger,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 230,
            Margin = new Thickness(0, 10, 0, 0)
        };

        // ---------------------------------------------------------- hebdomadaire
        string[] initials = ["L", "M", "M", "J", "V", "S", "D"];
        string[] longNames = ["lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi", "dimanche"];

        for (var i = 0; i < 7; i++)
        {
            var bit = 1 << i;
            var toggle = new ToggleButton
            {
                Content = initials[i],
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 4, 0),
                FontSize = 12,
                Cursor = Cursors.Hand,
                ToolTip = longNames[i]
            };
            toggle.Checked += (_, _) =>
            {
                if (syncing) return;
                working = working with { Weekdays = working.Weekdays | bit };
                Sync();
            };
            toggle.Unchecked += (_, _) =>
            {
                if (syncing) return;
                working = working with { Weekdays = working.Weekdays & ~bit };
                Sync();
            };
            toggles.Add(toggle);
            weekPanel.Children.Add(toggle);
        }

        // ------------------------------------------------------------ intervalle
        var intervalRow = new StackPanel { Orientation = Orientation.Horizontal };
        intervalRow.Children.Add(Legend("Tous les"));
        intervalRow.Children.Add(Stepper("−", () => { working = Clamp(working, working.Interval - 1); Sync(); }));
        intervalRow.Children.Add(intervalValue);
        intervalRow.Children.Add(Stepper("+", () => { working = Clamp(working, working.Interval + 1); Sync(); }));
        intervalRow.Children.Add(Legend("jours"));

        var anchorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        anchorRow.Children.Add(Legend("Calé sur le"));
        anchorRow.Children.Add(Stepper("‹", () => { working = Shift(working, -1); Sync(); }));
        anchorRow.Children.Add(anchorValue);
        anchorRow.Children.Add(Stepper("›", () => { working = Shift(working, +1); Sync(); }));

        // Deux flèches plutôt qu'un calendrier : l'ancrage est une PHASE, pas un
        // début. Ce qu'on règle, c'est « sur quel jour tombent les occurrences »,
        // et un décalage d'un jour à la fois le dit mieux qu'une date à choisir.
        intervalPanel.Children.Add(intervalRow);
        intervalPanel.Children.Add(anchorRow);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HAlign.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = MakeButton("Annuler", CloseSchedulePopup);
        var confirm = MakeButton("Terminé", () =>
        {
            d.Schedule = working;
            d.ScheduleInvalid = false;
            CloseSchedulePopup();
            MarkDirty();   // reconstruit la ligne, donc le libellé du bouton
        });
        confirm.Margin = new Thickness(8, 0, 0, 0);
        cancel.Padding = confirm.Padding = new Thickness(12, 4, 12, 4);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "HORAIRE",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = MainWindow.Muted,
            Margin = new Thickness(0, 0, 0, 10)
        });
        stack.Children.Add(modePanel);
        stack.Children.Add(weekPanel);
        stack.Children.Add(intervalPanel);
        stack.Children.Add(warning);
        stack.Children.Add(actions);

        // PIÈGE WPF : le contenu d'un Popup vit dans un arbre visuel séparé et
        // n'hérite RIEN du thème sombre — mêmes causes et mêmes effets que pour le
        // ContextMenu de la corbeille. Fond et bordure sont posés à la main.
        var content = new Border
        {
            Background = MainWindow.Panel,
            BorderBrush = MainWindow.Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = stack,
            // Sans Focusable ET sans Focus() à l'ouverture, aucune touche n'arrive
            // jusqu'ici et Échap resterait lettre morte.
            Focusable = true
        };
        content.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            CloseSchedulePopup();
            e.Handled = true;
        };

        modeDaily.Checked += (_, _) =>
        {
            if (syncing) return;
            working = working with { Kind = ScheduleKind.Daily };
            Sync();
        };
        modeWeekly.Checked += (_, _) =>
        {
            if (syncing) return;
            // Les champs des autres modes sont CONSERVÉS le temps de l'édition :
            // basculer en hebdomadaire puis revenir à l'intervalle ne doit pas faire
            // oublier le nombre de jours saisi. C'est JournalDb qui remet à zéro les
            // champs hors sujet au moment d'écrire, via Canonical().
            //
            // Le mode naît avec le jour de la semaine courant : ouvrir un mode déjà
            // en faute serait accueillir par un reproche.
            working = working with
            {
                Kind = ScheduleKind.Weekly,
                Weekdays = working.Weekdays != 0
                    ? working.Weekdays
                    : Schedule.Bit(LogicalDay.Today().DayOfWeek)
            };
            Sync();
        };
        modeInterval.Checked += (_, _) =>
        {
            if (syncing) return;
            working = working with
            {
                Kind = ScheduleKind.Interval,
                Interval = working.Interval >= 2 ? working.Interval : 2,
                Anchor = working.Anchor ?? LogicalDay.Today()
            };
            Sync();
        };

        Sync();

        _schedulePopup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -6,
            // StaysOpen = false : le clic à l'extérieur ferme, et cela vaut abandon.
            StaysOpen = false,
            Child = content
        };
        _schedulePopup.Opened += (_, _) => content.Focus();
        _schedulePopup.Closed += (_, _) => _schedulePopup = null;
        _schedulePopup.IsOpen = true;

        return;

        void Sync()
        {
            syncing = true;
            try
            {
                var weekly = working.Kind == ScheduleKind.Weekly;
                var interval = working.Kind == ScheduleKind.Interval;

                modeDaily.IsChecked = !weekly && !interval;
                modeWeekly.IsChecked = weekly;
                modeInterval.IsChecked = interval;

                weekPanel.Visibility = weekly ? Visibility.Visible : Visibility.Collapsed;
                intervalPanel.Visibility = interval ? Visibility.Visible : Visibility.Collapsed;
                warning.Visibility = weekly && working.Weekdays == 0
                    ? Visibility.Visible : Visibility.Collapsed;

                for (var i = 0; i < toggles.Count; i++)
                {
                    var on = (working.Weekdays & (1 << i)) != 0;
                    toggles[i].IsChecked = on;
                    // Le gabarit par défaut d'un ToggleButton est fait pour un thème
                    // clair et son état coché s'y voit à peine : les trois pinceaux
                    // sont posés ici, sinon les jours choisis ne se distinguent pas.
                    toggles[i].Background = on ? Accent : Brushes.Transparent;
                    toggles[i].BorderBrush = on ? Accent : MainWindow.Line;
                    toggles[i].Foreground = on ? MainWindow.Bg : Brushes.White;
                }

                intervalValue.Text = working.Interval.ToString();
                anchorValue.Text = (working.Anchor ?? LogicalDay.Today()).ToString("ddd d MMM");
            }
            finally
            {
                syncing = false;
            }
        }
    }

    private void CloseSchedulePopup()
    {
        if (_schedulePopup is null) return;
        _schedulePopup.IsOpen = false;
        _schedulePopup = null;
    }

    /// <summary>Intervalle borné : en dessous de 2 la règle n'a plus de sens, au-delà d'un an non plus.</summary>
    private static Schedule Clamp(Schedule s, int interval) =>
        s with { Interval = Math.Clamp(interval, 2, 365) };

    private static Schedule Shift(Schedule s, int days) =>
        s with { Anchor = (s.Anchor ?? LogicalDay.Today()).AddDays(days) };

    private static RadioButton ModeOption(string label) => new()
    {
        Content = label,
        Foreground = Brushes.White,
        FontSize = 13,
        Margin = new Thickness(0, 0, 0, 6),
        Cursor = Cursors.Hand,
        // Un seul popup d'horaire peut être ouvert à la fois : le nom de groupe n'a
        // pas à être unique par instance.
        GroupName = "BujoScheduleMode"
    };

    private static TextBlock Legend(string text) => new()
    {
        Text = text,
        Foreground = MainWindow.Muted,
        FontSize = 12,
        VerticalAlignment = VAlign.Center,
        Margin = new Thickness(0, 0, 6, 0)
    };

    private static Button Stepper(string glyph, Action onClick)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 24,
            Height = 24,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(0),
            FontSize = 13,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderBrush = MainWindow.Line,
            Cursor = Cursors.Hand,
            VerticalAlignment = VAlign.Center
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------------------------------------------------- glisser-déposer

    private void MaybeStartDrag(MouseEventArgs e, Border source)
    {
        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(null) - _pressOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var dragged = _pressed;
        _pressed = null;

        // La ligne va se déplacer sous un popup qui, lui, ne bouge pas : on le ferme
        // plutôt que de le laisser flotter au-dessus d'une habitude qui n'est plus là.
        CloseSchedulePopup();

        source.Opacity = 0.45;
        try
        {
            // Appel BLOQUANT : la boucle de glissement tourne ici jusqu'au relâchement.
            var data = new DataObject(DragFormat, dragged);
            DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
        }
        finally
        {
            source.Opacity = 1.0;
            ClearAllMarks();
        }
    }

    private void ShowInsertionMark(Border target, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        var below = e.GetPosition(target).Y > target.ActualHeight / 2;
        target.BorderBrush = Accent;
        target.BorderThickness = below ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private static void ClearMark(Border b)
    {
        b.BorderBrush = Brushes.Transparent;
        b.BorderThickness = new Thickness(0);
    }

    private void ClearAllMarks()
    {
        foreach (var child in _rows.Children)
            if (child is Border b) ClearMark(b);
    }

    private void HandleDrop(Border target, DragEventArgs e)
    {
        ClearAllMarks();
        if (e.Data.GetData(DragFormat) is not Draft dragged) return;
        if (target.Tag is not Draft over || ReferenceEquals(dragged, over)) return;

        e.Handled = true;

        // Même filtre que RenderRows, obligatoirement : une ligne invisible laissée
        // dans cette liste décalerait l'index d'insertion d'un cran par rapport à
        // ce que tu vois à l'écran.
        var visible = _drafts.Where(x => !x.Archived && !x.Deleted).ToList();
        var below = e.GetPosition(target).Y > target.ActualHeight / 2;

        visible.Remove(dragged);
        var index = visible.IndexOf(over) + (below ? 1 : 0);
        visible.Insert(Math.Clamp(index, 0, visible.Count), dragged);

        // L'ordre de _drafts est l'ordre d'affichage ; celles qui sont sorties de la
        // liste sont reléguées à la fin, elles n'ont plus de position visible.
        _drafts = [.. visible, .. _drafts.Where(x => x.Archived || x.Deleted)];
        MarkDirty();
    }

    /// <summary>
    /// Marque le brouillon comme modifié. rerender = false pour les frappes clavier :
    /// reconstruire la liste à chaque caractère ferait perdre le curseur.
    /// </summary>
    private void MarkDirty(bool rerender = true)
    {
        _dirty = true;
        if (rerender) RenderRows();
        UpdateButtons();
        DraftChanged?.Invoke();
    }

    private void UpdateButtons()
    {
        _apply.IsEnabled = _dirty;
        _cancel.IsEnabled = _dirty;

        if (_dirty)
        {
            // Pulsation lente : un signal périphérique, pas un clignotement agressif.
            var pulse = new ColorAnimation
            {
                To = Color.FromRgb(0x5A, 0xC8, 0x8A),
                Duration = TimeSpan.FromSeconds(1.1),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            _applyBorder.BeginAnimation(SolidColorBrush.ColorProperty, pulse);
        }
        else
        {
            // null retire l'animation ET rend la propriété à sa valeur locale.
            _applyBorder.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _applyBorder.Color = ((SolidColorBrush)MainWindow.Line).Color;
        }
    }

    // -------------------------------------------------------------- écriture

    /// <summary>
    /// Rend false quand l'application a été REFUSÉE — horaire incomplet, ou
    /// suppression franche non confirmée. L'hôte de la présentation en a besoin :
    /// sans cela, un refus fermerait quand même l'assistant en laissant croire que
    /// tout a été enregistré.
    /// </summary>
    private bool ApplyChanges()
    {
        CloseSchedulePopup();

        // Un mode hebdomadaire sans aucun jour coché n'est jamais dû : l'habitude
        // disparaîtrait de la routine et du Suivi sans que rien ne l'ait annoncé.
        // Le refus vaut pour l'application ENTIÈRE, comme pour la suppression franche
        // plus bas : le brouillon reste intact, on corrige et on réapplique. La
        // bordure du bouton d'horaire passe au rouge sur les lignes fautives, pour
        // que le message n'oblige pas à les retrouver à l'œil.
        var invalid = _drafts
            .Where(d => !d.Archived && !d.Deleted
                        && !string.IsNullOrWhiteSpace(d.Name)
                        && d.ScheduleIsEmpty)
            .ToList();

        foreach (var d in _drafts) d.ScheduleInvalid = false;

        if (invalid.Count > 0)
        {
            foreach (var d in invalid) d.ScheduleInvalid = true;
            RenderRows();

            var names = string.Join(", ", invalid.Select(d => d.Name.Trim()));
            MessageBox.Show(
                $"{names}\n\n"
                + $"{(invalid.Count > 1 ? "Ces horaires hebdomadaires n'ont" : "Cet horaire hebdomadaire n'a")} "
                + "aucun jour coché. "
                + $"{(invalid.Count > 1 ? "Ces habitudes ne seraient" : "Cette habitude ne serait")} "
                + "jamais due, ni dans la routine, ni dans le suivi.\n\n"
                + "Rien n'a été enregistré : ton brouillon est intact.",
                "Horaire incomplet",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // La confirmation vit ici, pas au clic sur « Supprimer » : c'est le seul
        // instant où la destruction a réellement lieu, et le seul où l'on peut
        // annoncer le total exact. Refuser annule l'application ENTIÈRE, le
        // brouillon reste intact — appliquer la moitié d'une intention serait pire.
        var doomed = _drafts.Where(d => d.Deleted && d.Id is not null).ToList();
        if (doomed.Count > 0)
        {
            var entries = doomed.Sum(d => _db.CountHabitEntries(d.Id!));
            var names = string.Join(", ", doomed.Select(d => d.Name.Trim()));

            var answer = MessageBox.Show(
                $"{names}\n\n"
                + $"{doomed.Count} habitude{(doomed.Count > 1 ? "s" : "")} et "
                + $"{entries} entrée{(entries > 1 ? "s" : "")} d'historique seront définitivement effacées. "
                + "Cette opération ne laisse aucune trace et ne peut pas être annulée.\n\n"
                + "Pour conserver l'historique, utilise « Archiver » à la place.",
                "Supprimer définitivement ?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return false;
        }

        var position = 0;
        foreach (var d in _drafts)
        {
            // Testé avant Archived : une habitude marquée pour suppression n'a pas
            // à passer par un archivage intermédiaire.
            if (d.Deleted)
            {
                if (d.Id is not null) _db.DeleteHabitForever(d.Id);
                continue;
            }

            if (d.Archived)
            {
                if (d.Id is not null) _db.ArchiveHabit(d.Id);
                continue;
            }

            if (string.IsNullOrWhiteSpace(d.Name)) continue;   // ligne vide : ignorée

            if (d.Id is null)
                _db.AddHabit(d.Name.Trim(), d.Type, d.IsRoutine, position++, d.Schedule);
            else
                _db.UpdateHabit(new Habit(d.Id, d.Name.Trim(), d.Type, d.IsRoutine, d.Active,
                                          position++, d.Schedule));
        }

        Load();

        // En mode intégré, Applied ferait engager le verrou par-dessus la fenêtre
        // modale de l'hôte. C'est celui-ci qui décide de le déclencher ou non, par ses
        // deux boutons « Terminer » et « Terminer et verrouiller maintenant ».
        if (!_embedded) Applied?.Invoke();
        return true;
    }

    /// <summary>
    /// Écrit le brouillon depuis un hôte qui fournit ses propres boutons. Même chemin
    /// que « Appliquer », refus compris : un horaire hebdomadaire vide y bloque tout
    /// aussi bien, et l'hôte ne doit pas contourner cette garde.
    /// </summary>
    public bool ApplyFromHost() => ApplyChanges();

    /// <summary>
    /// Vrai si au moins une habitude du brouillon est due aujourd'hui, donc si le
    /// verrou peut réellement apparaître maintenant.
    ///
    /// Lu sur le BROUILLON et non sur la base : l'hôte pose la question avant d'avoir
    /// écrit, et un bouton « verrouiller maintenant » calculé sur l'état enregistré
    /// serait faux exactement le jour où il compte, celui du premier lancement.
    /// </summary>
    public bool HasWorkToday
    {
        get
        {
            var today = LogicalDay.Today();
            return _drafts.Any(d => !d.Archived && !d.Deleted
                                    && !string.IsNullOrWhiteSpace(d.Name)
                                    && d.IsRoutine && d.Active
                                    && d.Schedule.IsDue(today));
        }
    }

    // ------------------------------------------------------------- fabriques

    private static TextBox Input(string text, double width) => new()
    {
        Text = text,
        Width = width > 0 ? width : double.NaN,
        Margin = new Thickness(0, 0, 8, 0),
        Padding = new Thickness(6, 4, 6, 4),
        Background = Brushes.Transparent,
        Foreground = Brushes.White,
        CaretBrush = Brushes.White,
        BorderBrush = MainWindow.Line,
        VerticalAlignment = VAlign.Center,
        // Un TextBox accepte le dépôt de texte par défaut et avalerait le geste.
        AllowDrop = false
    };

    private static Button MakeButton(string label, Action onClick)
    {
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(14, 6, 14, 6),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderBrush = MainWindow.Line,
            Cursor = Cursors.Hand
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button SmallButton(string glyph, Action onClick)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 30,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0, 2, 0, 2),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderBrush = MainWindow.Line,
            Cursor = Cursors.Hand
        };
        b.Click += (_, _) => onClick();
        return b;
    }
}
