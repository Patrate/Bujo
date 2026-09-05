using Microsoft.Win32;

namespace Bujo.Core;

public enum CloseAction { Quit, MinimizeToTray }

/// <summary>
/// Comment la routine se rappelle au matin.
///   Hard : verrou plein écran, l'ordinateur est pris en otage
///   Soft : encart en bas à droite, dérangeant mais l'ordinateur reste utilisable
/// </summary>
public enum LockMode { Hard, Soft }

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

    /// <summary>
    /// Mode de verrouillage de CET appareil. Comme le reste des réglages, il vit dans
    /// local_state et reste hors du périmètre de synchro : bloquant sur le fixe et
    /// souple sur le portable est un usage prévu, pas un accident.
    ///
    /// Défaut **souple** depuis la V1.5, et c'est un changement assumé : le défaut
    /// valait « bloquant » pour ne rien modifier aux bases antérieures à la V1.4. Une
    /// base qui n'a jamais touché ce réglage bascule donc en souple à la mise à jour.
    /// Le motif l'emporte : sur une installation neuve il n'y a rien à préserver, et se
    /// faire enfermer le jour même n'est pas un accueil. Une seule vérité, ici, plutôt
    /// qu'un défaut de propriété et un défaut d'assistant qui se contrediraient.
    /// </summary>
    public LockMode LockMode
    {
        get => db.GetLocal("settings.lock_mode") == "hard" ? LockMode.Hard : LockMode.Soft;
        set => db.SetLocal("settings.lock_mode", value == LockMode.Hard ? "hard" : "soft");
    }

    /// <summary>
    /// La présentation a déjà été montrée. Une clé EXPLICITE, et non l'absence
    /// d'habitudes : depuis que le semis n'existe plus, une base sans habitude est un
    /// état parfaitement normal — on peut légitimement toutes les archiver — et le
    /// déduire rouvrirait l'assistant à chaque démarrage.
    ///
    /// Posé dès que l'assistant s'ouvre, pas à sa dernière page. Le fermer en cours de
    /// route vaut « vu » : reproposer une fenêtre qu'on vient d'écarter est agaçant, et
    /// le bouton « Revoir la présentation » des Paramètres sert de rattrapage.
    /// </summary>
    public bool SetupDone
    {
        get => db.GetLocal("setup.done") == "1";
        set => db.SetLocal("setup.done", value ? "1" : "0");
    }

    /// <summary>
    /// Durées de snooze proposées, en minutes. Liste FERMÉE et non saisie libre :
    /// une saisie libre autoriserait 240 minutes, c'est-à-dire une sortie forcée
    /// déguisée qui échappe à toute trace. La liste dit ce que le snooze est — le
    /// temps que la bouilloire chauffe.
    /// </summary>
    public static readonly int[] SnoozeChoices = [3, 5, 10, 15, 20, 30];

    /// <summary>
    /// Durée du snooze. Le getter revalide contre la liste fermée : une valeur écrite
    /// à la main dans local_state, ou rapatriée un jour par la synchro, ne doit pas
    /// pouvoir contourner la borne. Toute valeur inconnue retombe sur 5 minutes.
    /// </summary>
    public int SnoozeMinutes
    {
        get => int.TryParse(db.GetLocal("settings.snooze_minutes"), out var m)
               && SnoozeChoices.Contains(m) ? m : 5;
        set => db.SetLocal("settings.snooze_minutes", value.ToString());
    }

    public string VpsUrl
    {
        get => db.GetLocal("settings.vps_url") ?? "";
        set => db.SetLocal("settings.vps_url", value);
    }

    /// <summary>
    /// Déverrouille les outils de mise au point. Local à l'appareil comme le reste
    /// des réglages : activé sur la machine de développement, jamais sur celle
    /// d'usage quotidien, sans que la synchro future ait son mot à dire.
    /// </summary>
    public bool DeveloperMode
    {
        get => db.GetLocal("settings.developer_mode") == "1";
        set => db.SetLocal("settings.developer_mode", value ? "1" : "0");
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
