using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bujo.Core;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using HAlign = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
// System.Windows.Shapes n'est pas importé ici, mais l'alias coûte moins cher que
// le jour où quelqu'un ajoutera un tracé dans cette vue et cassera Path.
using Path = System.IO.Path;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Ui;

/// <summary>Paramètres de l'appareil. Chaque contrôle écrit immédiatement, pas de bouton « Enregistrer ».</summary>
public sealed class SettingsView : ScrollViewer
{
    private readonly Settings _settings;
    private readonly TextBlock _dayState = new();
    private readonly TextBlock _exportState = new();
    private bool _loading = true;

    public event Action? DataChanged;

    /// <summary>
    /// Levé quand le décalage du jour logique change. Distinct de DataChanged : ce
    /// n'est pas une relecture des mêmes données, c'est une entrée dans un autre
    /// jour, et les vues doivent suivre — d'où Activate côté MainWindow.
    /// </summary>
    public event Action? DayOffsetChanged;
    
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

        stack.Children.Add(new BackupSettingsView(db, settings, backupService));

        stack.Children.Add(Section("Export"));

        var exportButtons = new StackPanel { Orientation = Orientation.Horizontal };
        exportButtons.Children.Add(SmallButton("Exporter en CSV", () => RunExport(db)));
        exportButtons.Children.Add(SmallButton("Ouvrir le dossier", OpenExportFolder));
        stack.Children.Add(Field(
            "Habitudes et jours",
            "Écrit deux fichiers dans %APPDATA%\\Bujo\\exports : une ligne par valeur d'habitude, "
            + "une ligne par jour. Séparateur point-virgule et UTF-8 avec BOM, pour qu'Excel les "
            + "ouvre directement. Les lignes supprimées et les habitudes archivées sont incluses, "
            + "avec leurs dates, pour que l'export ne mente pas sur les trous de l'historique.",
            exportButtons));

        _exportState.FontSize = 12;
        _exportState.Foreground = MainWindow.Muted;
        _exportState.TextWrapping = TextWrapping.Wrap;
        _exportState.Margin = new Thickness(0, -8, 0, 20);
        stack.Children.Add(_exportState);

        stack.Children.Add(Section("Avancé"));

        // Tous les outils de mise au point vivent dans le même conteneur : une seule
        // visibilité à basculer, et rien qui puisse rester affiché par oubli.
        var devTools = new StackPanel
        {
            Visibility = _settings.DeveloperMode ? Visibility.Visible : Visibility.Collapsed
        };

        var developerMode = new CheckBox
        {
            IsChecked = _settings.DeveloperMode,
            Foreground = Brushes.White,
            FontSize = 14
        };
        developerMode.Checked += (_, _) => SetDeveloperMode(true, devTools);
        developerMode.Unchecked += (_, _) => SetDeveloperMode(false, devTools);
        stack.Children.Add(Field(
            "Mode développeur",
            "Découvre les outils de mise au point. À laisser désactivé en usage courant.",
            developerMode));

        // ------------------------------------------- décalage du jour logique

        var dayButtons = new StackPanel { Orientation = Orientation.Horizontal };
        dayButtons.Children.Add(SmallButton("− 1 jour", () => ShiftDay(-1)));
        dayButtons.Children.Add(SmallButton("+ 1 jour", () => ShiftDay(+1)));
        dayButtons.Children.Add(SmallButton("Réinitialiser", () => ShiftDay(null)));
        devTools.Children.Add(Field(
            "Décalage du jour logique",
            "Fait croire à l'application qu'on est un autre jour, pour vérifier le retour du verrou "
            + "sans attendre. Le verrou réagit dans les 30 secondes, comme à 6 h du matin. "
            + "Non enregistré : un redémarrage remet le décalage à zéro.",
            dayButtons));

        _dayState.FontSize = 12;
        _dayState.Foreground = MainWindow.Muted;
        _dayState.Margin = new Thickness(0, -8, 0, 20);
        devTools.Children.Add(_dayState);
        RefreshDayState();

        var dangerZone = new DangerZoneView(db);
        dangerZone.DataChanged += () => DataChanged?.Invoke();
        devTools.Children.Add(dangerZone);

        stack.Children.Add(devTools);

        _loading = false;
    }

    /// <summary>
    /// Visibilité basculée à chaud plutôt que reconstruction de la vue : cocher la
    /// case doit découvrir les outils sans redémarrage. Collapsed et non Hidden,
    /// sinon ils laisseraient un trou dans la pile.
    /// </summary>
    private void SetDeveloperMode(bool enabled, UIElement devTools)
    {
        if (_loading) return;
        _settings.DeveloperMode = enabled;
        devTools.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Export synchrone : sur un journal personnel, deux requêtes ordonnées et
    /// quelques milliers de lignes ne gèlent pas l'interface de façon perceptible.
    /// Si le fichier devenait lourd un jour, c'est ici qu'un Task.Run irait.
    /// </summary>
    private void RunExport(JournalDb db)
    {
        try
        {
            var files = db.ExportCsv(JournalDb.ExportDir);
            _exportState.Text = "Export terminé : " + string.Join(", ", files.Select(Path.GetFileName));
        }
        catch (Exception ex)
        {
            Log.Write("export", ex);
            _exportState.Text = $"Échec : {ex.Message}";
        }
    }

    private void OpenExportFolder()
    {
        try
        {
            Directory.CreateDirectory(JournalDb.ExportDir);
            // Explorer accepte un chemin en argument ; UseShellExecute est requis.
            Process.Start(new ProcessStartInfo("explorer.exe", JournalDb.ExportDir)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Write("export", ex);
            _exportState.Text = $"Impossible d'ouvrir le dossier : {ex.Message}";
        }
    }

    /// <summary>Décale d'un jour, ou remet à zéro si days vaut null.</summary>
    private void ShiftDay(int? days)
    {
        LogicalDay.DebugOffsetDays = days is null ? 0 : LogicalDay.DebugOffsetDays + days.Value;
        RefreshDayState();
        DayOffsetChanged?.Invoke();
    }

    private void RefreshDayState()
    {
        var offset = LogicalDay.DebugOffsetDays;
        var suffix = offset == 0 ? "aucun décalage" : $"décalage {offset:+#;-#}";
        _dayState.Text = $"Jour logique courant : {LogicalDay.Key(LogicalDay.Today())} — {suffix}";
    }

    private static Button SmallButton(string label, Action onClick)
    {
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderBrush = MainWindow.Line,
            Cursor = Cursors.Hand
        };
        b.Click += (_, _) => onClick();
        return b;
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
