using System.Drawing;
using Bujo.Core;
using Forms = System.Windows.Forms;

namespace Bujo.Ui;

/// <summary>
/// Icône de zone de notification. C'est elle qui rend l'app résidente utilisable :
/// sans point de rentrée visible, une app sans fenêtre est une app perdue.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    /// <summary>
    /// Icône extraite de l'exe, dont nous sommes propriétaires. NotifyIcon ne libère
    /// pas l'icône qu'on lui donne. Reste null si l'extraction a échoué : SystemIcons
    /// rend une instance partagée par tout le processus, la libérer casserait le reste.
    /// </summary>
    private readonly Icon? _ownedIcon;

    public TrayIcon(Action open, Action lockNow, Action quit, Func<bool> isLocked)
    {
        var menu = new Forms.ContextMenuStrip();

        var openItem = menu.Items.Add("Ouvrir Bujo");
        openItem.Click += (_, _) => open();

        var lockItem = menu.Items.Add("Verrouiller maintenant");
        lockItem.Click += (_, _) => lockNow();

        menu.Items.Add(new Forms.ToolStripSeparator());

        var quitItem = menu.Items.Add("Quitter");
        quitItem.Click += (_, _) => quit();

        // Pendant le verrou, ouvrir la fenêtre principale n'aurait aucun sens
        // (elle passerait sous le verrou) et offrirait un contournement gratuit.
        menu.Opening += (_, _) =>
        {
            var locked = isLocked();
            openItem.Enabled = !locked;
            lockItem.Enabled = !locked;
        };

        _ownedIcon = TryExtractExeIcon();

        _icon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = "Bujo",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => { if (!isLocked()) open(); };
    }

    /// <summary>
    /// Réutilise l'icône de l'exécutable, celle que déclare ApplicationIcon dans le
    /// csproj : aucun fichier embarqué en double, et la zone de notification montre
    /// exactement ce que montre la barre des tâches.
    ///
    /// Retourne null si l'extraction échoue, auquel cas l'appelant retombe sur
    /// l'icône système. Une icône manquante ne doit pas empêcher l'application de
    /// démarrer : sans zone de notification, une app sans fenêtre est perdue.
    /// </summary>
    private static Icon? TryExtractExeIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            // ExtractAssociatedIcon lève si le chemin est vide ou introuvable, et
            // rend null si le fichier ne porte pas d'icône.
            return exe is null ? null : Icon.ExtractAssociatedIcon(exe);
        }
        catch (Exception ex)
        {
            Log.Write("tray", ex);
            return null;
        }
    }

    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.Info);

    /// <summary>Sans Dispose explicite, l'icône reste affichée jusqu'au survol de la souris.</summary>
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        // Après le NotifyIcon, jamais avant : il tient encore le handle.
        _ownedIcon?.Dispose();
    }
}
