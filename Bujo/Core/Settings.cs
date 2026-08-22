using Microsoft.Win32;

namespace Bujo.Core;

public enum CloseAction { Quit, MinimizeToTray }

/// <summary>
/// Réglages de cet appareil. Stockés dans local_state, donc volontairement HORS
/// du périmètre de synchro : l'heure de verrouillage ou le démarrage automatique
/// n'ont pas à être les mêmes sur le portable et sur le fixe.
/// </summary>
public sealed class Settings(JournalDb db)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Bujo";

    public int CutoffHour
    {
        get => int.TryParse(db.GetLocal("settings.cutoff_hour"), out var h) && h is >= 0 and <= 23 ? h : 6;
        set
        {
            db.SetLocal("settings.cutoff_hour", value.ToString());
            LogicalDay.Cutoff = TimeSpan.FromHours(value);
        }
    }

    public string VpsUrl
    {
        get => db.GetLocal("settings.vps_url") ?? "";
        set => db.SetLocal("settings.vps_url", value);
    }

    public CloseAction CloseAction
    {
        get => db.GetLocal("settings.close_action") == "quit" ? CloseAction.Quit : CloseAction.MinimizeToTray;
        set => db.SetLocal("settings.close_action", value == CloseAction.Quit ? "quit" : "tray");
    }

    /// <summary>
    /// Le registre est la source de vérité, pas la base : si tu retires l'entrée
    /// à la main ou via un outil de démarrage, la case doit refléter la réalité.
    /// </summary>
    public bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (value)
            {
                var exe = Environment.ProcessPath;
                if (exe is null) return;
                // --check : au démarrage de session, on ne veut pas ouvrir la fenêtre principale.
                key.SetValue(RunValue, $"\"{exe}\" --check");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>À appeler une fois au démarrage, avant tout calcul de jour logique.</summary>
    public void Apply() => LogicalDay.Cutoff = TimeSpan.FromHours(CutoffHour);
}
