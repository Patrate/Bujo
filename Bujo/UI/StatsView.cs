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
public sealed class StatsView : DockPanel, IRefreshable
{
    private const int WindowDays = 84;        // 12 semaines, la largeur de la grille
    private const int RateWindow = 30;

    // Géométrie de la grille. Nommées parce que la colonne de libellés recopie la
    // hauteur d'une case : deux valeurs en dur auraient fini par diverger.
    private const double CellSize = 13;
    private const double CellGap = 2;

    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A));
    private static readonly Brush Empty  = new SolidColorBrush(Color.FromRgb(0x25, 0x29, 0x30));
    private static readonly Brush Dim    = new SolidColorBrush(Color.FromRgb(0x6A, 0x70, 0x7C));

    /// <summary>
    /// Gris de trame des cases sans objet : jour non dû, ou jour hors de la vie de
    /// l'habitude. Posé à mi-chemin exact entre le fond du panneau et Empty, le gris
    /// du raté — quatre points de luminance de part et d'autre. Plus sombre, la trame
    /// devenait invisible ; plus clair, elle se mettait à concurrencer le raté.
    ///
    /// Ne rien dessiner du tout, comme le prévoyait la décision initiale, faisait
    /// disparaître la forme même du calendrier — les jours antérieurs à la création
    /// d'une habitude tombant dans le même cas, c'était l'essentiel de la surface qui
    /// s'évanouissait. Ce gris ne porte aucun signal : il tient la trame, rien de plus.
    /// </summary>
    private static readonly Brush Faint = new SolidColorBrush(Color.FromRgb(0x21, 0x24, 0x29));

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

    /// <summary>
    /// Relit tout. La vue est en lecture seule et n'a pas de navigation à préserver
    /// au-delà de l'habitude sélectionnée, restaurée plus bas : Activate garde donc
    /// l'implémentation par défaut de l'interface.
    /// </summary>
    public void Refresh()
    {
        var today = LogicalDay.Today();
        var from = today.AddDays(-(RateWindow - 1));

        var completed = _db.GetRoutineCompletedDays(from, today).Count;

        // Plus de mention des sorties forcées : elles restent enregistrées, elles ne
        // sont plus affichées. Le taux dit déjà la même chose sans faire la morale.
        _summary.Text =
            $"Routine validée {completed} jour{(completed > 1 ? "s" : "")} sur les {RateWindow} derniers.";

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

        // Les jours dus sont demandés sur la MÊME fenêtre que l'historique. Une
        // fenêtre plus courte tronquerait les séries sans rien dire ; c'est Stats qui
        // en extrait ensuite la sous-fenêtre du taux.
        var dueDays = habit.Due.DueDays(from, today);
        var streak = Stats.Compute(history, dueDays, today, RateWindow);

        _detail.Children.Add(new TextBlock
        {
            Text = habit.Name,
            FontSize = 20,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 4)
        });

        // L'horaire est rappelé sous le nom : c'est lui qui explique les trous de la
        // grille et le dénominateur invisible du taux. Sans cette ligne, une grille
        // clairsemée se lit comme un historique en dents de scie.
        _detail.Children.Add(new TextBlock
        {
            Text = habit.Due.Rule.Describe(),
            FontSize = 12,
            Foreground = Dim,
            Margin = new Thickness(0, 0, 0, 14)
        });

        // « jours » pour une habitude quotidienne — l'affichage de la V1.1 reste mot
        // pour mot — et « fois » dès que la série compte des occurrences dues et non
        // des jours de calendrier. « 3 jours d'affilée » pour trois lundis tenus
        // serait la félicitation mensongère qu'on cherche à éviter.
        var unit = habit.Due.Rule.IsEveryDay ? "jours" : "fois";

        var cards = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        cards.Children.Add(Card("Série en cours", $"{streak.Current}", unit));
        cards.Children.Add(Card("Meilleure série", $"{streak.Best}", unit));
        // Rate vaut null quand rien n'était dû sur la fenêtre — habitude créée
        // aujourd'hui, ou horaire très espacé. « 0 % » serait un reproche adressé à
        // une exigence qui ne s'est pas encore présentée.
        cards.Children.Add(streak.Rate is { } rate
            ? Card($"Sur {RateWindow} jours", $"{rate * 100:0}", "%")
            : Card($"Sur {RateWindow} jours", "—", ""));
        _detail.Children.Add(cards);

        _detail.Children.Add(Section("Régularité"));
        _detail.Children.Add(BuildHeatmap(history, habit.Due, today));

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
    /// Grille façon calendrier : une colonne par semaine, une ligne par jour de la
    /// semaine, précédée d'une colonne de libellés.
    ///
    /// Quatre niveaux d'encre, et le jour NON DÛ n'en reçoit presque aucune. Le peindre
    /// en gris Empty, couleur du raté, donnerait cinq cases d'échec sur sept à une
    /// habitude « lundi et mercredi » parfaitement tenue — le 28 % transposé au visuel.
    /// Mais ne rien peindre du tout faisait disparaître la trame : le gris Faint tient
    /// la forme sans rien affirmer.
    ///
    /// Le bénéfice indirect est le vrai : le gris Empty cesse de vouloir dire « pas
    /// fait » pour vouloir dire « manqué », qui est la seule information intéressante.
    ///
    /// Le cinquième cas, une case cochée un jour devenu non dû, n'est plus atteignable
    /// par l'interface — GetRoutine filtre sur les jours dus. Il naît d'un changement
    /// d'horaire rétroactif, et l'effacer serait mentir sur ce qui a eu lieu.
    /// </summary>
    private UIElement BuildHeatmap(IReadOnlyList<HabitDay> history, HabitSchedule schedule, DateOnly today)
    {
        var done = history.Where(h => h.Done).Select(h => h.Day).ToHashSet();

        // On démarre au lundi de la première semaine affichée pour aligner les lignes.
        var start = today.AddDays(-(WindowDays - 1));
        start = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));

        var weeks = (int)Math.Ceiling((today.DayNumber - start.DayNumber + 1) / 7.0);

        // Une colonne par semaine, empilée horizontalement : les sept lignes
        // correspondent alors toujours au même jour de la semaine.
        var wrapper = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Left };
        wrapper.Children.Add(WeekdayLabels());

        for (var week = 0; week < weeks; week++)
        {
            var column = new StackPanel { Orientation = Orientation.Vertical };
            for (var d = 0; d < 7; d++)
            {
                var day = start.AddDays(week * 7 + d);
                var future = day > today;
                var before = day < schedule.From;
                var after = schedule.To is not null && day > schedule.To.Value;
                var due = schedule.IsDue(day);
                var held = done.Contains(day);

                var cell = new Border
                {
                    Width = CellSize,
                    Height = CellSize,
                    Margin = new Thickness(CellGap),
                    CornerRadius = new CornerRadius(2),
                    Background = Faint,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0)
                };

                if (future)
                {
                    // À venir : un contour et pas de fond. C'est le seul état qui se
                    // distingue par la forme et non par la valeur du gris.
                    cell.Background = Brushes.Transparent;
                    cell.BorderBrush = Empty;
                    cell.BorderThickness = new Thickness(1);
                }
                else if (due)
                {
                    cell.Background = held ? Accent : Empty;
                    cell.ToolTip = $"{day:dddd d MMMM} — {(held ? "fait" : "manqué")}";
                }
                else if (held)
                {
                    // Case cochée un jour non dû : conservée, mais atténuée. La cause
                    // est nommée précisément — « non prévu » serait faux pour un jour
                    // antérieur à la création, où ce n'est pas l'horaire qui exclut.
                    cell.Background = Accent;
                    cell.Opacity = 0.45;
                    cell.ToolTip = $"{day:dddd d MMMM} — fait, {Reason(before, after)}";
                }
                else
                {
                    // Trois causes derrière le même gris, que seule l'infobulle sépare.
                    // L'œil n'a pas à les distinguer : dans les trois cas, rien n'était
                    // attendu ce jour-là.
                    cell.ToolTip = $"{day:dddd d MMMM} — {Reason(before, after)}";
                }

                column.Children.Add(cell);
            }
            wrapper.Children.Add(column);
        }
        return wrapper;
    }

    /// <summary>
    /// Pourquoi rien n'était attendu ce jour-là. Partagé par les deux branches qui en
    /// ont besoin : une case cochée hors des jours dus doit nommer la même cause
    /// qu'une case vide au même endroit.
    /// </summary>
    private static string Reason(bool before, bool after) =>
        before ? "avant la création de l'habitude"
        : after ? "après l'archivage"
        : "non prévu";

    /// <summary>
    /// Colonne de libellés à gauche de la grille. Deux lettres et non une : « M »
    /// désignerait aussi bien mardi que mercredi.
    ///
    /// La hauteur de chaque libellé recopie exactement celle d'une case, marges
    /// comprises. Toute autre valeur ferait dériver l'alignement d'une ligne à l'autre,
    /// et une grille de suivi dont les libellés glissent ne vaut pas mieux que pas de
    /// libellés du tout.
    /// </summary>
    private static UIElement WeekdayLabels()
    {
        string[] names = ["Lu", "Ma", "Me", "Je", "Ve", "Sa", "Di"];
        var column = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 6, 0) };

        foreach (var name in names)
        {
            column.Children.Add(new Border
            {
                Height = CellSize + 2 * CellGap,
                Child = new TextBlock
                {
                    Text = name,
                    FontSize = 9,
                    Foreground = Dim,
                    VerticalAlignment = VAlign.Center,
                    HorizontalAlignment = HAlign.Right
                }
            });
        }
        return column;
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
