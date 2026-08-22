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
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Ui;

/// <summary>
/// Daily log d'un jour logique, avec la routine du jour en tête.
/// Toutes les écritures sont immédiates : c'est un journal, pas un formulaire.
/// </summary>
public sealed class JournalView : DockPanel
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A));

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

    /// <summary>À appeler aussi quand la fenêtre réapparaît : le jour logique a pu basculer.</summary>
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

        foreach (var item in items)
            _routine.Children.Add(new RoutineRow(_db, _day, item, () =>
            {
                RenderRoutine();
                Refresh();
            }));
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
        var glyph = new Button
        {
            Content = e.State switch
            {
                LogState.Done => "✕",
                LogState.Migrated => "→",
                _ => e.Kind switch { LogKind.Event => "○", LogKind.Note => "–", _ => "•" }
            },
            Width = 28,
            FontSize = 15,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = e.State == LogState.Done ? Accent : Brushes.White,
            Cursor = Cursors.Hand,
            VerticalAlignment = VAlign.Center,
            ToolTip = e.Kind == LogKind.Task ? "Basculer terminé / à faire" : null,
            IsEnabled = e.Kind == LogKind.Task && e.State != LogState.Migrated
        };
        glyph.Click += (_, _) =>
        {
            _db.SetLogState(e.Id, e.State == LogState.Done ? LogState.Open : LogState.Done);
            RenderEntries();
        };
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(glyph);

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
                tools.Children.Add(Tiny("✗", "Abandonner", () =>
                {
                    _db.SetLogState(e.Id, e.State == LogState.Dropped ? LogState.Open : LogState.Dropped);
                    RenderEntries();
                }));
            }
            tools.Children.Add(Tiny("🗑", "Supprimer", () =>
            {
                _db.DeleteLogEntry(e.Id);
                RenderEntries();
            }));
        }

        Grid.SetColumn(tools, 2);
        grid.Children.Add(tools);

        return grid;
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
