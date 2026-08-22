using System.Windows;
using System.Windows.Controls;
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
using Cursors = System.Windows.Input.Cursors;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using HAlign = System.Windows.HorizontalAlignment;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Ui;

/// <summary>
/// Édition de la liste d'habitudes. Les modifications restent en brouillon jusqu'à
/// « Appliquer » : rien n'est écrit tant que tu n'as pas confirmé.
/// Réordonnancement par glisser-déposer depuis la poignée à gauche de chaque ligne.
/// </summary>
public sealed class RoutineConfigView : DockPanel
{
    private sealed class Draft
    {
        public string? Id;              // null = habitude à créer
        public string Name = "";
        public HabitValueType Type = HabitValueType.Bool;
        public bool IsRoutine = true;
        public bool Active = true;
        public bool Archived;
    }

    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A));
    private const string DragFormat = "BujoHabitDraft";

    private readonly JournalDb _db;
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


    /// <summary>Levé après écriture en base, pour que l'appelant reteste le verrou.</summary>
    public event Action? Applied;

    public RoutineConfigView(JournalDb db)
    {
        _db = db;

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);

        _status = new TextBlock
        {
            Foreground = MainWindow.Muted,
            FontSize = 12,
            VerticalAlignment = VAlign.Center,
            TextWrapping = TextWrapping.Wrap
        };

        _cancel = MakeButton("Annuler", () => Load());
        _apply = MakeButton("Appliquer", ApplyChanges);
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
        header.Children.Add(new TextBlock
        {
            Text = "Habitudes",
            FontSize = 18,
            Foreground = Brushes.White
        });
        header.Children.Add(new TextBlock
        {
            Text = "Une habitude « dans la routine » bloque l'écran tant qu'elle n'est pas validée. "
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
        Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = scrollContent
        });

        Load();
    }

    // ------------------------------------------------------------- chargement

    private void Load()
    {
        _drafts = _db.GetHabits().Select(h => new Draft
        {
            Id = h.Id,
            Name = h.Name,
            Type = h.Type,
            IsRoutine = h.IsRoutine,
            Active = h.Active
        }).ToList();
        _dirty = false;
        RenderRows();
        UpdateButtons();
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        var visible = _drafts.Where(d => !d.Archived).ToList();

        foreach (var draft in visible)
            _rows.Children.Add(BuildRow(draft));

        var routineCount = visible.Count(d => d.IsRoutine && d.Active);
        _status.Text = routineCount == 0
            ? "Aucune habitude dans la routine : l'écran de verrouillage n'apparaîtra pas."
            : $"{routineCount} habitude{(routineCount > 1 ? "s" : "")} dans la routine du matin.";
    }

    private UIElement BuildRow(Draft d)
    {
        var grid = new Grid();
        foreach (var star in new[] { false, true, false, false, false })
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
        Grid.SetColumn(inRoutine, 3);
        grid.Children.Add(inRoutine);

        var trash = SmallButton("🗑", () =>
        {
            // Archivage, pas suppression : l'historique de cette habitude reste consultable.
            if (d.Id is null) _drafts.Remove(d);
            else d.Archived = true;
            MarkDirty();
        });
        Grid.SetColumn(trash, 4);
        grid.Children.Add(trash);

        border.PreviewDragOver += (_, e) => ShowInsertionMark(border, e);
        border.PreviewDragLeave += (_, _) => ClearMark(border);
        border.PreviewDrop += (_, e) => HandleDrop(border, e);

        return border;
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

        var visible = _drafts.Where(x => !x.Archived).ToList();
        var below = e.GetPosition(target).Y > target.ActualHeight / 2;

        visible.Remove(dragged);
        var index = visible.IndexOf(over) + (below ? 1 : 0);
        visible.Insert(Math.Clamp(index, 0, visible.Count), dragged);

        // L'ordre de _drafts est l'ordre d'affichage ; les archivées sont reléguées
        // à la fin, elles n'ont plus de position visible.
        _drafts = [.. visible, .. _drafts.Where(x => x.Archived)];
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

    private void ApplyChanges()
    {
        var position = 0;
        foreach (var d in _drafts)
        {
            if (d.Archived)
            {
                if (d.Id is not null) _db.ArchiveHabit(d.Id);
                continue;
            }

            if (string.IsNullOrWhiteSpace(d.Name)) continue;   // ligne vide : ignorée

            if (d.Id is null)
                _db.AddHabit(d.Name.Trim(), d.Type, d.IsRoutine, position++);
            else
                _db.UpdateHabit(new Habit(d.Id, d.Name.Trim(), d.Type, d.IsRoutine, d.Active, position++));
        }

        Load();
        Applied?.Invoke();
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
