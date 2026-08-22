using System.Drawing;
using Forms = System.Windows.Forms;

namespace Bujo.Ui;

/// <summary>
/// Icône de zone de notification. C'est elle qui rend l'app résidente utilisable :
/// sans point de rentrée visible, une app sans fenêtre est une app perdue.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

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

        _icon = new Forms.NotifyIcon
        {
            // TODO : remplacer par une vraie icône embarquée (Resources\bujo.ico).
            Icon = SystemIcons.Application,
            Text = "Bujo",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => { if (!isLocked()) open(); };
    }

    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.Info);

    /// <summary>Sans Dispose explicite, l'icône reste affichée jusqu'au survol de la souris.</summary>
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
