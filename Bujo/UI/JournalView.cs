using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bujo.Core;
using Bujo.Lock;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using HAlign = System.Windows.HorizontalAlignment;
// UseWindowsForms ajoute System.Windows.Forms aux usings implicites : sans cet
// alias, MessageBox est ambigu entre WPF et WinForms. Même raison que les alias
// ci-dessus, et que celui de DangerZoneView.
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Ui;

/// <summary>
/// Daily log d'un jour logique, avec la routine du jour en tête.
/// Toutes les écritures sont immédiates : c'est un journal, pas un formulaire.
/// </summary>
public sealed class JournalView : DockPanel, IRefreshable
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A));

    /// <summary>
    /// Nombre de reports à partir duquel le compteur apparaît. En dessous, silence :
    /// repousser une ou deux fois est le fonctionnement normal d'un bullet journal,
    /// pas un écart à signaler.
    /// </summary>
    private const int ReportsThreshold = 3;

    private readonly JournalDb _db;
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly StackPanel _routine = new();
    private readonly StackPanel _entries = new();
    private readonly StackPanel _moodRow = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _dayNote;
    private readonly TextBox _newEntry;
    private readonly ComboBox _newKind;

    private DateOnly _day;

    public JournalView(JournalDb db)
    {
        _db = db;
        _day = LogicalDay.Today();

        // ------------------------------------------------------- barre de jour

        var nav = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(nav, Dock.Top);

        var navButtons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(navButtons, Dock.Right);
        navButtons.Children.Add(Small("◀", () => Go(-1)));
        navButtons.Children.Add(Small("Aujourd'hui", () => GoTo(LogicalDay.Today())));
        navButtons.Children.Add(Small("▶", () => Go(+1)));
        nav.Children.Add(navButtons);

        _title.FontSize = 20;
        _title.Foreground = Brushes.White;
        _subtitle.FontSize = 12;
        _subtitle.Foreground = MainWindow.Muted;
        var titles = new StackPanel();
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        nav.Children.Add(titles);

        Children.Add(nav);

        // ------------------------------------------------------------ contenu

        _newKind = new ComboBox { Width = 100, VerticalAlignment = VAlign.Center, AllowDrop = false };
        _newKind.Items.Add("Tâche");
        _newKind.Items.Add("Événement");
        _newKind.Items.Add("Note");
        _newKind.SelectedIndex = 0;

        _newEntry = new TextBox
        {
            Margin = new Thickness(8, 0, 8, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = MainWindow.Line,
            VerticalAlignment = VAlign.Center
        };
        _newEntry.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddEntry(); };

        var addRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_newKind, 0);
        Grid.SetColumn(_newEntry, 1);
        var addButton = Small("Ajouter", AddEntry);
        Grid.SetColumn(addButton, 2);
        addRow.Children.Add(_newKind);
        addRow.Children.Add(_newEntry);
        addRow.Children.Add(addButton);

        _dayNote = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = MainWindow.Line,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _dayNote.LostFocus += (_, _) =>
            _db.SetDayNote(_day, string.IsNullOrWhiteSpace(_dayNote.Text) ? null : _dayNote.Text.Trim());

        var content = new StackPanel();
        content.Children.Add(Section("Daily log"));
        content.Children.Add(_entries);
        content.Children.Add(addRow);
        content.Children.Add(Section("Humeur"));
        content.Children.Add(_moodRow);
        content.Children.Add(Section("Note du jour"));
        content.Children.Add(_dayNote);
        content.Children.Add(Section("Routine du jour"));
        content.Children.Add(_routine);

        Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content,
            Padding = new Thickness(0, 0, 14, 0)
        });

        Refresh();
    }

    // ------------------------------------------------------------ navigation

    private void Go(int days) => GoTo(_day.AddDays(days));

    private void GoTo(DateOnly day)
    {
        _day = day;
        Refresh();
    }

    /// <summary>
    /// Entrée dans l'onglet : on revient au jour courant, qui a pu basculer pendant
    /// que la fenêtre était ailleurs ou cachée en zone de notification.
    ///
    /// C'est la seule chose qui distingue cette vue des autres, et c'est la raison
    /// d'être de l'interface : si ce saut vivait dans Refresh, cocher une case
    /// ramènerait à aujourd'hui en pleine consultation d'un jour passé.
    /// </summary>
    public void Activate() => GoTo(LogicalDay.Today());

    /// <summary>Relit le jour affiché. Ne déplace jamais la navigation.</summary>
    public void Refresh()
    {
        var today = LogicalDay.Today();
        var info = _db.GetDay(_day);

        _title.Text = _day.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
        _subtitle.Text = _day == today
            ? info.RoutineCompleted ? "Aujourd'hui — routine validée" : "Aujourd'hui — routine en attente"
            : _day > today ? "Jour à venir" : "Jour passé";

        RenderRoutine();
        RenderEntries();
        RenderMood(info.Mood);

        _dayNote.Text = info.Note ?? "";
    }

    private void RenderRoutine()
    {
        _routine.Children.Clear();
        var items = _db.GetRoutine(_day);

        if (items.Count == 0)
        {
            _routine.Children.Add(Hint("Aucune habitude dans la routine."));
            return;
        }

        // Refresh() reconstruit déjà la routine : appeler RenderRoutine() en plus
        // bâtissait les lignes deux fois, et détruisait deux fois la case à cocher
        // depuis son propre gestionnaire — de quoi perdre le curseur d'un champ
        // de saisie numérique. Pas de récursion en revanche : RenderRoutine ne
        // déclenche aucune écriture, RoutineRow attache ses gestionnaires après
        // avoir posé IsChecked.
        foreach (var item in items)
            _routine.Children.Add(new RoutineRow(_db, _day, item, OnRoutineItemChanged));
    }

    /// <summary>
    /// Marque le jour comme validé dès que toutes les lignes le sont.
    ///
    /// Sans cela, une routine terminée depuis le Journal — le cas typique après une
    /// sortie de secours — ne compterait jamais dans la série : seul LockWindow
    /// écrivait routine_completed_at. MarkRoutineCompleted utilise COALESCE, la
    /// rappeler ne déplace donc pas l'heure de la première validation.
    ///
    /// Aucun risque de déverrouillage accidentel : pendant le verrou, la fenêtre
    /// principale est inaccessible, l'icône de zone de notification grise « Ouvrir ».
    /// </summary>
    private void OnRoutineItemChanged()
    {
        if (_db.IsRoutineDone(_day)) _db.MarkRoutineCompleted(_day);
        Refresh();
    }

    private void RenderEntries()
    {
        _entries.Children.Clear();
        var entries = _db.GetLog(_day);

        if (entries.Count == 0)
        {
            _entries.Children.Add(Hint("Rien pour ce jour. Ajoute une tâche, un événement ou une note."));
            return;
        }

        foreach (var entry in entries)
            _entries.Children.Add(BuildEntry(entry));
    }

    private UIElement BuildEntry(LogEntry e)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var closed = e.State is LogState.Done or LogState.Migrated or LogState.Dropped;

        // Puce BuJo : • tâche, ○ événement, – note ; × terminée, → migrée.
        var symbol = e.State switch
        {
            LogState.Done => "✕",
            LogState.Migrated => "→",
            _ => e.Kind switch { LogKind.Event => "○", LogKind.Note => "–", _ => "•" }
        };

        // Seule une tâche vivante se bascule. Pour tout le reste — événements, notes,
        // tâches déjà migrées — la puce n'est qu'un signe typographique.
        var togglable = e.Kind == LogKind.Task && e.State != LogState.Migrated;

        // PIÈGE WPF, et il était visible depuis la V1.0 sans qu'on l'identifie : le
        // gabarit par défaut d'un Button peint son état DÉSACTIVÉ sur le Border interne
        // du template, pas sur la propriété Background de l'élément. Un
        // « Background = Transparent » posé de l'extérieur est donc écrasé, et sur fond
        // sombre chaque puce non cliquable devenait un gros carré clair.
        //
        // Le contournement aurait été de réécrire le gabarit. La vraie correction est
        // de ne pas employer un bouton là où il n'y a rien à cliquer.
        UIElement bullet;
        if (togglable)
        {
            var button = new Button
            {
                Content = symbol,
                Width = 28,
                FontSize = 15,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = e.State == LogState.Done ? Accent : Brushes.White,
                Cursor = Cursors.Hand,
                VerticalAlignment = VAlign.Center,
                ToolTip = "Basculer terminé / à faire"
            };
            button.Click += (_, _) =>
            {
                _db.SetLogState(e.Id, e.State == LogState.Done ? LogState.Open : LogState.Done);
                RenderEntries();
            };
            bullet = button;
        }
        else
        {
            bullet = new TextBlock
            {
                Text = symbol,
                // Largeur et corps recopiés du bouton : les deux puces doivent tomber
                // sur la même colonne, sinon les lignes se décalent selon leur genre.
                Width = 28,
                FontSize = 15,
                TextAlignment = TextAlignment.Center,
                // La flèche d'une entrée migrée suit le gris de sa ligne ; un événement
                // ou une note reste une ligne vivante, donc en blanc.
                Foreground = e.State == LogState.Migrated ? MainWindow.Muted : Brushes.White,
                VerticalAlignment = VAlign.Center
            };
        }

        Grid.SetColumn(bullet, 0);
        grid.Children.Add(bullet);

        var text = new TextBox
        {
            Text = e.Content,
            FontSize = 14,
            Padding = new Thickness(4, 3, 4, 3),
            Background = Brushes.Transparent,
            Foreground = closed ? MainWindow.Muted : Brushes.White,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            TextDecorations = e.State == LogState.Dropped ? TextDecorations.Strikethrough : null,
            IsReadOnly = e.State == LogState.Migrated,
            VerticalAlignment = VAlign.Center,
            AllowDrop = false
        };
        text.LostFocus += (_, _) =>
        {
            var value = text.Text.Trim();
            if (value.Length > 0 && value != e.Content) _db.UpdateLogContent(e.Id, value);
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VAlign.Center };

        var reports = ReportBadge(e);
        if (reports is not null) tools.Children.Add(reports);

        if (e.State == LogState.Migrated)
        {
            tools.Children.Add(new TextBlock
            {
                Text = $"migrée au {e.MigratedTo:dd/MM}",
                Foreground = MainWindow.Muted,
                FontSize = 11,
                VerticalAlignment = VAlign.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
        }
        else
        {
            if (e.Kind == LogKind.Task)
            {
                tools.Children.Add(Tiny("→", "Migrer au lendemain", () =>
                {
                    _db.MigrateLogEntry(e.Id, _day.AddDays(1));
                    RenderEntries();
                }));
                // Le glyphe et l'infobulle suivent l'état : « Abandonner » posé sur
                // une tâche déjà barrée se lit comme un bouton inerte, alors que la
                // bascule fonctionne. C'est l'étiquette qui manquait, pas la logique.
                var dropped = e.State == LogState.Dropped;
                tools.Children.Add(Tiny(
                    dropped ? "↺" : "✗",
                    dropped ? "Reprendre la tâche" : "Abandonner",
                    () =>
                    {
                        _db.SetLogState(e.Id, dropped ? LogState.Open : LogState.Dropped);
                        RenderEntries();
                    }));
            }
            tools.Children.Add(Tiny("🗑", "Supprimer", () => DeleteEntry(e)));
        }

        Grid.SetColumn(tools, 2);
        grid.Children.Add(tools);

        return grid;
    }

    /// <summary>
    /// Compteur de reports, affiché seulement au-delà du seuil.
    ///
    /// Une tâche repoussée une fois, c'est la vie ordinaire ; le signaler serait un
    /// reproche adressé à un geste normal, exactement ce que la V1.1 a démonté en
    /// retirant le compteur de sorties forcées. Repoussée quatre fois, c'est une
    /// information : soit la tâche est mal découpée, soit elle n'a pas sa place ici.
    ///
    /// Gris sourd, jamais rouge, et de la même taille quel qu'en soit le nombre : il
    /// informe, il n'alerte pas. Conservé sur une entrée terminée — une tâche faite
    /// après cinq reports est précisément le cas que la revue mensuelle veut voir.
    ///
    /// Rend null quand il n'y a rien à montrer, pour que l'appelant n'ait pas à
    /// connaître la règle.
    /// </summary>
    private UIElement? ReportBadge(LogEntry e)
    {
        var trail = _db.GetMigrationTrail(e);
        if (trail.Reports < ReportsThreshold) return null;

        var origin = trail.Origin is null
            ? ""
            : $" depuis le {trail.Origin.Value.ToString("dddd d MMMM", CultureInfo.CurrentCulture)}";

        return new TextBlock
        {
            Text = $"↻ {trail.Reports}",
            Foreground = MainWindow.Muted,
            FontSize = 11,
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = $"Repoussée {trail.Reports} fois{origin}."
        };
    }

    /// <summary>
    /// Supprimer efface toute la chaîne de reports : une tâche qui n'aurait jamais
    /// dû exister n'a pas d'histoire à raconter. Abandonner (✗) fait l'inverse et
    /// conserve la chaîne entière, dernier maillon barré.
    ///
    /// La confirmation n'apparaît qu'au-delà d'un maillon. Sur une chaîne d'un seul
    /// élément — de très loin le cas courant — il n'y a rien d'invisible à
    /// détruire, et un dialogue systématique ferait du bruit sans protéger.
    /// </summary>
    private void DeleteEntry(LogEntry entry)
    {
        var chain = _db.GetMigrationChain(entry.Id);

        if (chain.Count > 1)
        {
            var reports = chain.Count - 1;
            var answer = MessageBox.Show(
                $"Cette tâche a été repoussée {(reports == 1 ? "une fois" : $"{reports} fois")} "
                + $"avant d'arriver ici. La supprimer effacera les {chain.Count} entrées de la chaîne, "
                + "sur des jours qui ne sont pas affichés.\n\n"
                + "Pour garder la trace des reports, utilise plutôt « Abandonner ».",
                "Supprimer la tâche et son historique ?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            // Bouton par défaut « Non » : sur une action destructrice, un Entrée
            // réflexe doit annuler, comme dans la zone dangereuse.
            if (answer != MessageBoxResult.Yes) return;
        }

        _db.DeleteLogEntry(entry.Id);
        RenderEntries();
    }

    private void RenderMood(int? current)
    {
        _moodRow.Children.Clear();
        for (var value = 1; value <= 5; value++)
        {
            var v = value;
            var selected = current == v;
            var b = new Button
            {
                Content = v.ToString(),
                Width = 38,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(0, 4, 0, 4),
                Background = Brushes.Transparent,
                Foreground = selected ? Accent : Brushes.White,
                BorderBrush = selected ? Accent : MainWindow.Line,
                Cursor = Cursors.Hand
            };
            b.Click += (_, _) =>
            {
                // Recliquer sur la valeur active l'efface : ne rien noter est une réponse valable.
                _db.SetDayMood(_day, selected ? null : v);
                RenderMood(selected ? null : v);
            };
            _moodRow.Children.Add(b);
        }
    }

    private void AddEntry()
    {
        var content = _newEntry.Text.Trim();
        if (content.Length == 0) return;

        var kind = _newKind.SelectedIndex switch
        {
            1 => LogKind.Event,
            2 => LogKind.Note,
            _ => LogKind.Task
        };

        _db.AddLogEntry(_day, kind, content);
        _newEntry.Clear();
        _newEntry.Focus();
        RenderEntries();
    }

    // ------------------------------------------------------------- fabriques

    private static UIElement Section(string title) => new TextBlock
    {
        Text = title.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = MainWindow.Muted,
        Margin = new Thickness(0, 20, 0, 8)
    };

    private static UIElement Hint(string text) => new TextBlock
    {
        Text = text,
        FontSize = 12,
        Foreground = MainWindow.Muted,
        Margin = new Thickness(0, 2, 0, 2)
    };

    private static Button Small(string label, Action onClick)
    {
        var b = new Button
        {
            Content = label,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(12, 5, 12, 5),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderBrush = MainWindow.Line,
            Cursor = Cursors.Hand
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button Tiny(string glyph, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 28,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0, 2, 0, 2),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderBrush = MainWindow.Line,
            Cursor = Cursors.Hand,
            ToolTip = tip
        };
        b.Click += (_, _) => onClick();
        return b;
    }
}
