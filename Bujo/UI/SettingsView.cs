using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bujo.Core;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using HAlign = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Ui;

/// <summary>Paramètres de l'appareil. Chaque contrôle écrit immédiatement, pas de bouton « Enregistrer ».</summary>
public sealed class SettingsView : ScrollViewer
{
    private readonly Settings _settings;
    private bool _loading = true;
    public event Action? DataChanged;
    
    public SettingsView(Settings settings, JournalDb db, BackupService backupService)
    {
        _settings = settings;
        var stack = new StackPanel();
        Content = stack;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Padding = new Thickness(0, 0, 14, 0);

        stack.Children.Add(Section("Verrouillage"));

        var hours = new ComboBox { Width = 100 };
        for (var h = 0; h <= 12; h++) hours.Items.Add($"{h:00}:00");
        hours.SelectedIndex = Math.Clamp(_settings.CutoffHour, 0, 12);
        hours.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.CutoffHour = hours.SelectedIndex;
        };
        stack.Children.Add(Field(
            "Début du jour logique",
            "Avant cette heure, la routine reste celle de la veille. Se coucher à 2 h ne fait pas basculer le jour.",
            hours));

        stack.Children.Add(Section("Fenêtre"));

        var closeAction = new ComboBox { Width = 260 };
        closeAction.Items.Add("Réduire dans la zone de notification");
        closeAction.Items.Add("Quitter l'application");
        closeAction.SelectedIndex = _settings.CloseAction == CloseAction.Quit ? 1 : 0;
        closeAction.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.CloseAction = closeAction.SelectedIndex == 1 ? CloseAction.Quit : CloseAction.MinimizeToTray;
        };
        stack.Children.Add(Field(
            "Bouton fermer",
            "En quittant, plus rien ne surveille la bascule du jour : le verrou ne reviendra qu'au prochain déclenchement de la tâche planifiée.",
            closeAction));

        var autostart = new CheckBox
        {
            IsChecked = _settings.StartWithWindows,
            Foreground = Brushes.White,
            FontSize = 14
        };
        autostart.Checked += (_, _) => { if (!_loading) _settings.StartWithWindows = true; };
        autostart.Unchecked += (_, _) => { if (!_loading) _settings.StartWithWindows = false; };
        stack.Children.Add(Field(
            "Démarrer avec Windows",
            "Ajoute une entrée dans la clé Run de l'utilisateur courant, lancée avec --check.",
            autostart));

        stack.Children.Add(Section("Sauvegarde"));

        var vps = new TextBox
        {
            Text = _settings.VpsUrl,
            Width = 300,
            Padding = new Thickness(6, 4, 6, 4)
        };
        vps.LostFocus += (_, _) => { if (!_loading) _settings.VpsUrl = vps.Text.Trim(); };
        stack.Children.Add(new BackupSettingsView(db, settings, backupService));
        
        var dangerZone = new DangerZoneView(db);
        dangerZone.DataChanged += () => DataChanged?.Invoke();
        stack.Children.Add(dangerZone);
        
        _loading = false;
    }

    private static UIElement Section(string title) => new TextBlock
    {
        Text = title.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = MainWindow.Muted,
        Margin = new Thickness(0, 18, 0, 10)
    };

    /// <summary>
    /// Libellé et description à gauche, contrôle à droite sur la ligne du titre.
    /// Le contrôle est aligné en haut : il reste en face du libellé même quand la
    /// description passe sur deux lignes.
    /// </summary>
    private static UIElement Field(string label, string hint, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
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
            MaxWidth = 460
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        control.HorizontalAlignment = HAlign.Right;
        control.VerticalAlignment = VAlign.Top;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        return grid;
    }
}
