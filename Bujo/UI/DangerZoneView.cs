using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bujo.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HAlign = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;

namespace Bujo.Ui;

/// <summary>
/// Actions irréversibles. Chacune est confirmée par une boîte de dialogue, et
/// chacune produit d'abord une copie de sûreté locale horodatée — la confirmation
/// protège de l'erreur de clic, la copie protège du regret.
/// </summary>
public sealed class DangerZoneView : StackPanel
{
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xD1, 0x50, 0x50));

    private readonly JournalDb _db;
    private readonly TextBlock _status = new();

    /// <summary>
    /// Levé après une opération destructrice réussie. Les vues affichent des données
    /// lues à leur dernier rafraîchissement : sans ce signal, elles montrent un
    /// historique qui n'existe plus jusqu'au prochain redémarrage.
    /// </summary>
    public event Action? DataChanged;

    public DangerZoneView(JournalDb db)
    {
        _db = db;

        Children.Add(new TextBlock
        {
            Text = "ZONE DANGEREUSE",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Danger,
            Margin = new Thickness(0, 26, 0, 10)
        });

        Children.Add(Row(
            "Réinitialiser le compteur de sorties forcées",
            "Efface les sorties forcées du journal du verrou. Les séries d'habitudes ne sont pas touchées.",
            "Réinitialiser",
            () => Confirm(
                "Réinitialiser le compteur de sorties forcées ?",
                "Les sorties forcées enregistrées seront définitivement effacées.\n\n"
                + "Une copie de sûreté locale est créée avant l'opération.",
                () => { _db.ResetBypassCounter(); return "Compteur réinitialisé."; })));

        Children.Add(Row(
            "Supprimer l'historique",
            "Efface les jours, les entrées de journal, les valeurs d'habitudes et les événements du verrou. "
            + "Les habitudes et les paramètres sont conservés.",
            "Supprimer",
            () => Confirm(
                "Supprimer tout l'historique ?",
                "Tout le contenu du journal et tout le suivi seront définitivement effacés. "
                + "Les habitudes et les paramètres sont conservés.\n\n"
                + "Une copie de sûreté locale est créée avant l'opération.",
                () => { _db.DeleteAllHistory(); return "Historique supprimé."; })));

        _status.FontSize = 12;
        _status.Foreground = MainWindow.Muted;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 6, 0, 0);
        Children.Add(_status);
    }

    /// <summary>
    /// Le bouton par défaut est « Non » : sur une action irréversible, un Entrée
    /// réflexe doit annuler, pas confirmer.
    /// </summary>
    private void Confirm(string title, string message, Func<string> action)
    {
        var answer = MessageBox.Show(message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var backup = _db.BackupBeforeWipe();
            var result = action();
            _status.Text = $"{result} Copie de sûreté : {System.IO.Path.GetFileName(backup)}";
            DataChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _status.Text = $"Échec : {ex.Message}";
        }
    }

    private static UIElement Row(string label, string hint, string buttonLabel, Action onClick)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 14,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 3)
        });
        text.Children.Add(new TextBlock
        {
            Text = hint,
            FontSize = 12,
            Foreground = MainWindow.Muted,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var button = new Button
        {
            Content = buttonLabel,
            Padding = new Thickness(14, 5, 14, 5),
            Background = Brushes.Transparent,
            Foreground = Danger,
            BorderBrush = Danger,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HAlign.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        button.Click += (_, _) => onClick();
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        return grid;
    }
}
