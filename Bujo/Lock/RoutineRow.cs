using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bujo.Core;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using DataObject = System.Windows.DataObject;
using HAlign = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Lock;

/// <summary>
/// Une ligne de routine. Deux écritures distinctes et jamais liées :
/// la saisie appelle SetHabitValue, la case à cocher appelle SetHabitDone.
///
/// La case est le SEUL geste de validation, quel que soit le type d'habitude —
/// une case pour les booléennes, la même case pour celles qui portent une valeur,
/// dont le champ de saisie se place à droite.
/// </summary>
public sealed class RoutineRow : Grid
{
    private readonly JournalDb _db;
    private readonly DateOnly _day;
    private readonly RoutineItem _item;
    private readonly Action _onChanged;

    private TextBox? _input;

    public RoutineRow(JournalDb db, DateOnly day, RoutineItem item, Action onChanged)
    {
        _db = db;
        _day = day;
        _item = item;
        _onChanged = onChanged;

        Margin = new Thickness(0, 8, 0, 8);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });              // case
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // nom
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });              // saisie

        BuildCheckBox();
        BuildLabel();
        if (_item.Type != HabitValueType.Bool) BuildInput();
    }

    private void BuildCheckBox()
    {
        var box = new CheckBox
        {
            IsChecked = _item.Done,
            FontSize = 20,
            Foreground = Brushes.White,
            VerticalAlignment = VAlign.Center,
            ToolTip = _item.Type == HabitValueType.Bool ? null : "Valider cette ligne"
        };
        // Handlers attachés APRÈS IsChecked : sinon l'initialisation déclencherait une écriture.
        box.Checked += (_, _) => Validate(true);
        box.Unchecked += (_, _) => Validate(false);
        SetColumn(box, 0);
        Children.Add(box);
    }

    private void BuildLabel()
    {
        var label = new TextBlock
        {
            Text = _item.Name,
            FontSize = 20,
            Foreground = Brushes.White,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VAlign.Center,
            TextDecorations = _item.Done && _item.Type == HabitValueType.Bool
                ? TextDecorations.Strikethrough : null,
            Opacity = _item.Done ? 0.45 : 1.0
        };
        SetColumn(label, 1);
        Children.Add(label);
    }

    private void BuildInput()
    {
        _input = new TextBox
        {
            Width = _item.Type == HabitValueType.Number ? 70 : 200,
            FontSize = 18,
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(12, 0, 0, 0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x50)),
            VerticalAlignment = VAlign.Center,
            HorizontalAlignment = HAlign.Right,
            TextAlignment = _item.Type == HabitValueType.Number ? TextAlignment.Right : TextAlignment.Left,
            Text = _item.Type == HabitValueType.Number
                ? _item.ValueNum?.ToString(CultureInfo.CurrentCulture) ?? ""
                : _item.ValueText ?? ""
        };

        if (_item.Type == HabitValueType.Number)
        {
            var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            _input.PreviewTextInput += (_, e) =>
                e.Handled = !e.Text.All(c => char.IsDigit(c) || sep.Contains(c));
            DataObject.AddPastingHandler(_input, (_, e) => e.CancelCommand());
        }

        // La valeur est écrite en continu, mais n'emporte JAMAIS la validation.
        _input.LostFocus += (_, _) => PersistValue();
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            PersistValue();
            Validate(!_item.Done);
        };

        SetColumn(_input, 2);
        Children.Add(_input);
    }

    private void PersistValue()
    {
        if (_input is null) return;
        var raw = _input.Text;

        if (_item.Type == HabitValueType.Number)
        {
            double? n = double.TryParse(raw, NumberStyles.Any, CultureInfo.CurrentCulture, out var v) ? v : null;
            _db.SetHabitValue(_item.HabitId, _day, n, null);
        }
        else
        {
            _db.SetHabitValue(_item.HabitId, _day, null, string.IsNullOrWhiteSpace(raw) ? null : raw.Trim());
        }
    }

    private void Validate(bool done)
    {
        PersistValue();      // on n'écrase pas une saisie non confirmée
        _db.SetHabitDone(_item.HabitId, _day, done);
        _onChanged();
    }
}
