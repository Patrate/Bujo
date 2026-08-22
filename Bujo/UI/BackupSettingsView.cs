using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bujo.Core;
using Microsoft.Win32;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using HAlign = System.Windows.HorizontalAlignment;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VAlign = System.Windows.VerticalAlignment;

namespace Bujo.Ui;

/// <summary>
/// Section « Sauvegarde » des paramètres. Autonome : à insérer dans SettingsView.
///
/// Les secrets ne sont jamais réaffichés — les champs mot de passe restent vides et
/// un champ laissé vide ne modifie rien. C'est la seule façon honnête de présenter
/// un secret qu'on a chiffré : on ne peut pas prétendre le montrer.
/// </summary>
public sealed class BackupSettingsView : StackPanel
{
    private readonly JournalDb _db;
    private readonly BackupService _backup;

    private readonly TextBox _host;
    private readonly TextBox _user;
    private readonly TextBox _remoteDir;
    private readonly TextBox _keyPath;
    private readonly PasswordBox _passphrase;
    private readonly PasswordBox _password;
    private readonly TextBlock _status = new();
    private readonly Button _runNow;

    public BackupSettingsView(JournalDb db, Settings settings, BackupService backup)
    {
        _db = db;
        _backup = backup;

        Children.Add(Section("Sauvegarde SFTP"));

        _host = Text(settings.VpsUrl, "mon-vps.ovh.net");
        _host.LostFocus += (_, _) => SaveRemote();
        Children.Add(Field("Hôte SSH", "Nom d'hôte nu, sans https:// ni slash.", _host));

        _user = Text(_db.GetLocal("backup.user") ?? "", "bujobackup");
        _user.LostFocus += (_, _) => _backup.SetUser(_user.Text);
        Children.Add(Field("Utilisateur", "Le compte dédié créé sur le serveur.", _user));

        _remoteDir = Text(_db.GetLocal("backup.remote_dir") ?? "backups/bujo", "backups/bujo");
        _remoteDir.LostFocus += (_, _) => SaveRemote();
        Children.Add(Field("Dossier distant",
            "Chemin relatif à la racine vue par SFTP. Sans slash initial si le compte est chrooté.",
            _remoteDir));

        // ------------------------------------------------------- clé privée
        _keyPath = Text(_db.GetLocal("backup.key_path") ?? "", "aucune clé sélectionnée");
        _keyPath.IsReadOnly = true;
        _keyPath.Width = 240;

        var browse = SmallButton("Parcourir…", () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Clé privée OpenSSH",
                InitialDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
                Filter = "Tous les fichiers|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            _keyPath.Text = dialog.FileName;
            SaveKey();
        });

        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        keyRow.Children.Add(_keyPath);
        keyRow.Children.Add(browse);
        Children.Add(Field("Clé privée",
            "Recommandé. Si aucune clé n'est fournie, l'authentification se fait par mot de passe.",
            keyRow));

        _passphrase = Password();
        _passphrase.LostFocus += (_, _) => { if (_passphrase.Password.Length > 0) SaveKey(); };
        Children.Add(Field("Passphrase de la clé",
            "Laisse vide si la clé n'en a pas, ou pour conserver celle déjà enregistrée.",
            _passphrase));

        _password = Password();
        _password.LostFocus += (_, _) =>
        {
            if (_password.Password.Length == 0) return;
            _backup.SetPassword(_password.Password);
            _password.Clear();
            Announce("Mot de passe enregistré (chiffré par DPAPI).");
        };
        Children.Add(Field("Mot de passe SSH",
            "Utilisé uniquement en l'absence de clé. Laisse vide pour conserver l'existant.",
            _password));

        // ---------------------------------------------------------- actions
        _runNow = SmallButton("Sauvegarder maintenant", RunNow);
        _runNow.Margin = new Thickness(0);

        var openFolder = SmallButton("Ouvrir le dossier local", () =>
        {
            Directory.CreateDirectory(BackupService.BackupDir);
            // Explorer accepte un chemin en argument ; UseShellExecute est requis.
            Process.Start(new ProcessStartInfo("explorer.exe", BackupService.BackupDir)
            {
                UseShellExecute = true
            });
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        actions.Children.Add(_runNow);
        actions.Children.Add(openFolder);
        Children.Add(actions);

        _status.FontSize = 12;
        _status.Foreground = MainWindow.Muted;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Text = $"Dernière sauvegarde : {_backup.LastResult}";
        Children.Add(_status);
    }

    // ------------------------------------------------------------ écritures

    private void SaveRemote() => _backup.SetRemote(_host.Text, _remoteDir.Text);

    private void SaveKey()
    {
        _backup.SetKey(_keyPath.Text, _passphrase.Password.Length > 0 ? _passphrase.Password : null);
        _passphrase.Clear();
    }

    /// <summary>
    /// La sauvegarde part sur un thread de fond : un envoi SFTP peut prendre
    /// plusieurs secondes, et geler l'interface pendant ce temps serait inacceptable.
    /// </summary>
    private void RunNow()
    {
        _runNow.IsEnabled = false;
        Announce("Sauvegarde en cours…");

        Task.Run(() => _backup.Run())
            .ContinueWith(task =>
            {
                var path = task.Result;
                Announce(path is null
                    ? $"Échec — {_backup.LastResult}"
                    : $"Terminée — {Path.GetFileName(path)} ({_backup.LastResult})");
                _runNow.IsEnabled = true;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Announce(string message) => _status.Text = message;

    // ------------------------------------------------------------ fabriques

    private static TextBox Text(string value, string hint) => new()
    {
        Text = value,
        Width = 300,
        Padding = new Thickness(6, 4, 6, 4),
        Background = Brushes.Transparent,
        Foreground = Brushes.White,
        CaretBrush = Brushes.White,
        BorderBrush = MainWindow.Line,
        VerticalAlignment = VAlign.Center,
        ToolTip = hint
    };

    private static PasswordBox Password() => new()
    {
        Width = 300,
        Padding = new Thickness(6, 4, 6, 4),
        Background = Brushes.Transparent,
        Foreground = Brushes.White,
        CaretBrush = Brushes.White,
        BorderBrush = MainWindow.Line,
        VerticalAlignment = VAlign.Center
    };

    private static Button SmallButton(string label, Action onClick)
    {
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(8, 0, 0, 0),
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

    private static UIElement Field(string label, string hint, FrameworkElement control)
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

        control.HorizontalAlignment = HAlign.Right;
        control.VerticalAlignment = VAlign.Top;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        return grid;
    }
}
