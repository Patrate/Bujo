using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Bujo.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HAlign = System.Windows.HorizontalAlignment;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Ui;

/// <summary>
/// Vues de suivi : régularité de la routine, séries par habitude, et évolution
/// des valeurs numériques. Lecture seule, rien n'est modifiable ici.
/// </summary>
public sealed class StatsView : DockPanel
{
    private const int WindowDays = 84;        // 12 semaines, la largeur de la grille
    private const int RateWindow = 30;

    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A));
    private static readonly Brush Empty  = new SolidColorBrush(Color.FromRgb(0x25, 0x29, 0x30));
    private static readonly Brush Dim    = new SolidColorBrush(Color.FromRgb(0x6A, 0x70, 0x7C));
    private static readonly Brush Warn   = new SolidColorBrush(Color.FromRgb(0xD8, 0x8A, 0x5A));

    private readonly JournalDb _db;
    private readonly ListBox _habitList = new();
    private readonly StackPanel _detail = new();
    private readonly TextBlock _summary = new();

    public StatsView(JournalDb db)
    {
        _db = db;

        _summary.FontSize = 13;
        _summary.Foreground = Dim;
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Margin = new Thickness(0, 0, 0, 16);
        DockPanel.SetDock(_summary, Dock.Top);
        Children.Add(_summary);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _habitList.Background = Brushes.Transparent;
        _habitList.BorderBrush = MainWindow.Line;
        _habitList.Foreground = Brushes.White;
        _habitList.SelectionChanged += (_, _) => RenderDetail();
        Grid.SetColumn(_habitList, 0);
        columns.Children.Add(_habitList);

        var scroll = new ScrollViewer
        {
            Margin = new Thickness(20, 0, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _detail,
            Padding = new Thickness(0, 0, 14, 0)
        };
        Grid.SetColumn(scroll, 1);
        columns.Children.Add(scroll);

        Children.Add(columns);
        Refresh();
    }

    /// <summary>À appeler quand l'onglet redevient visible.</summary>
    public void Refresh()
    {
        var today = LogicalDay.Today();
        var from = today.AddDays(-(RateWindow - 1));

        var completed = _db.GetRoutineCompletedDays(from, today).Count;
        var bypasses = _db.GetBypassDays(from, today);

        _summary.Inlines.Clear();
        _summary.Inlines.Add(new System.Windows.Documents.Run(
            $"Routine validée {completed} jour{(completed > 1 ? "s" : "")} sur les {RateWindow} derniers.  "));
        _summary.Inlines.Add(new System.Windows.Documents.Run(
            bypasses.Count == 0
                ? "Aucune sortie forcée."
                : $"{bypasses.Count} sortie{(bypasses.Count > 1 ? "s" : "")} forcée{(bypasses.Count > 1 ? "s" : "")}, "
                  + $"la dernière le {bypasses[0]:dd/MM}.")
        { Foreground = bypasses.Count == 0 ? Dim : Warn });

        var previous = (_habitList.SelectedItem as ListBoxItem)?.Tag as HabitSummary;

        _habitList.Items.Clear();
        foreach (var habit in _db.GetHabitsForStats())
        {
            var item = new ListBoxItem
            {
                Content = habit.Archived ? $"{habit.Name}  (archivée)" : habit.Name,
                Tag = habit,
                Foreground = habit.Archived ? Dim : Brushes.White,
                Padding = new Thickness(8, 6, 8, 6)
            };
            _habitList.Items.Add(item);
            if (previous is not null && habit.Id == previous.Id) _habitList.SelectedItem = item;
        }

        if (_habitList.SelectedItem is null && _habitList.Items.Count > 0)
            _habitList.SelectedIndex = 0;

        RenderDetail();
    }

    private void RenderDetail()
    {
        _detail.Children.Clear();
        if ((_habitList.SelectedItem as ListBoxItem)?.Tag is not HabitSummary habit)
        {
            _detail.Children.Add(new TextBlock
            {
                Text = "Aucune habitude à afficher.",
                Foreground = Dim,
                FontSize = 13
            });
            return;
        }

        var today = LogicalDay.Today();
        var from = today.AddDays(-(WindowDays - 1));
        var history = _db.GetHabitHistory(habit.Id, from, today);
        var streak = Stats.Compute(history, today, RateWindow);

        _detail.Children.Add(new TextBlock
        {
            Text = habit.Name,
            FontSize = 20,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var cards = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        cards.Children.Add(Card("Série en cours", $"{streak.Current}", "jours"));
        cards.Children.Add(Card("Meilleure série", $"{streak.Best}", "jours"));
        cards.Children.Add(Card($"Sur {RateWindow} jours", $"{streak.Rate * 100:0}", "%"));
        _detail.Children.Add(cards);

        _detail.Children.Add(Section("Régularité"));
        _detail.Children.Add(BuildHeatmap(history, today));

        if (habit.Type == HabitValueType.Number)
        {
            var series = Stats.NumberSeries(history);
            _detail.Children.Add(Section("Progression"));
            _detail.Children.Add(series.Count < 2
                ? new TextBlock
                {
                    Text = "Au moins deux valeurs sont nécessaires pour tracer une courbe.",
                    Foreground = Dim,
                    FontSize = 13
                }
                : BuildChart(series));
        }
    }

    // ---------------------------------------------------------------- grille

    /// <summary>
    /// Grille façon calendrier : une colonne par semaine, une ligne par jour.
    /// Les cases futures de la semaine en cours restent vides, pas grisées en « manqué ».
    /// </summary>
    private UIElement BuildHeatmap(IReadOnlyList<HabitDay> history, DateOnly today)
    {
        var done = history.Where(h => h.Done).Select(h => h.Day).ToHashSet();

        // On démarre au lundi de la première semaine affichée pour aligner les lignes.
        var start = today.AddDays(-(WindowDays - 1));
        start = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));

        var weeks = (int)Math.Ceiling((today.DayNumber - start.DayNumber + 1) / 7.0);

        // Une colonne par semaine, empilée horizontalement : les sept lignes
        // correspondent alors toujours au même jour de la semaine.
        var wrapper = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Left };
        for (var week = 0; week < weeks; week++)
        {
            var column = new StackPanel { Orientation = Orientation.Vertical };
            for (var d = 0; d < 7; d++)
            {
                var day = start.AddDays(week * 7 + d);
                var future = day > today;

                column.Children.Add(new Border
                {
                    Width = 13,
                    Height = 13,
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(2),
                    Background = future ? Brushes.Transparent : done.Contains(day) ? Accent : Empty,
                    BorderBrush = future ? Empty : Brushes.Transparent,
                    BorderThickness = new Thickness(future ? 1 : 0),
                    ToolTip = future ? null : $"{day:dddd d MMMM} — {(done.Contains(day) ? "fait" : "non fait")}"
                });
            }
            wrapper.Children.Add(column);
        }
        return wrapper;
    }

    // ----------------------------------------------------------------- courbe

    /// <summary>
    /// Tracé maison sur un Canvas : pas de dépendance de graphiques pour une
    /// polyligne. Les points sont positionnés à la première mise en page réelle,
    /// donc après que le Canvas connaît sa largeur.
    /// </summary>
    private UIElement BuildChart(IReadOnlyList<(DateOnly Day, double Value)> series)
    {
        var canvas = new Canvas { Height = 170, Margin = new Thickness(0, 4, 0, 0), ClipToBounds = true };

        var line = new Polyline
        {
            Stroke = Accent,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };
        canvas.Children.Add(line);

        var min = series.Min(p => p.Value);
        var max = series.Max(p => p.Value);
        var span = Math.Abs(max - min) < 0.001 ? 1 : max - min;

        var maxLabel = Label($"{max:0.##}");
        var minLabel = Label($"{min:0.##}");
        canvas.Children.Add(maxLabel);
        canvas.Children.Add(minLabel);

        canvas.SizeChanged += (_, _) =>
        {
            var w = canvas.ActualWidth - 46;
            var h = canvas.ActualHeight - 22;
            if (w <= 0 || h <= 0) return;

            line.Points.Clear();
            for (var i = 0; i < series.Count; i++)
            {
                var x = 42 + (series.Count == 1 ? w / 2 : w * i / (series.Count - 1.0));
                var y = 6 + h * (1 - (series[i].Value - min) / span);
                line.Points.Add(new Point(x, y));
            }

            Canvas.SetLeft(maxLabel, 0);
            Canvas.SetTop(maxLabel, 0);
            Canvas.SetLeft(minLabel, 0);
            Canvas.SetTop(minLabel, h);
        };

        var footer = new DockPanel { Margin = new Thickness(42, 2, 0, 0) };
        var first = new TextBlock { Text = $"{series[0].Day:dd/MM}", Foreground = Dim, FontSize = 11 };
        var last = new TextBlock { Text = $"{series[^1].Day:dd/MM}", Foreground = Dim, FontSize = 11 };
        DockPanel.SetDock(first, Dock.Left);
        DockPanel.SetDock(last, Dock.Right);
        footer.Children.Add(first);
        footer.Children.Add(last);

        var stack = new StackPanel();
        stack.Children.Add(canvas);
        stack.Children.Add(footer);
        stack.Children.Add(new TextBlock
        {
            Text = $"{series.Count} valeurs — dernière : {series[^1].Value:0.##}",
            Foreground = Dim,
            FontSize = 12,
            Margin = new Thickness(42, 6, 0, 0)
        });
        return stack;
    }

    // -------------------------------------------------------------- fabriques

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = Dim,
        FontSize = 11
    };

    private static UIElement Card(string label, string value, string suffix)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = MainWindow.Muted,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 26,
            FontWeight = FontWeights.Light,
            Foreground = Brushes.White
        });
        row.Children.Add(new TextBlock
        {
            Text = suffix,
            FontSize = 12,
            Foreground = Dim,
            Margin = new Thickness(4, 0, 0, 5),
            VerticalAlignment = VAlign.Bottom
        });
        panel.Children.Add(row);

        return new Border
        {
            Background = Empty,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16, 12, 20, 12),
            Margin = new Thickness(0, 0, 12, 0),
            MinWidth = 130,
            Child = panel
        };
    }

    private static UIElement Section(string title) => new TextBlock
    {
        Text = title.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = MainWindow.Muted,
        Margin = new Thickness(0, 8, 0, 10)
    };
}
