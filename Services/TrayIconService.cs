using System.Drawing;
using Forms = System.Windows.Forms;

namespace MouseAccelerator.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;

    public TrayIconService(Action showSettings, Action quit)
    {
        _icon = AppIconFactory.CreateIcon();
        var menu = new Forms.ContextMenuStrip();
        var settingsItem = new Forms.ToolStripMenuItem(
            "Settings…",
            null,
            (_, _) => showSettings())
        {
            Font = new Font(Forms.Control.DefaultFont, FontStyle.Bold)
        };
        var quitItem = new Forms.ToolStripMenuItem("Quit", null, (_, _) => quit());
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Precision Jump — ready",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                showSettings();
            }
        };
    }

    public void SetJumpActive(bool active)
    {
        _notifyIcon.Text = active
            ? "Precision Jump — continuous map active"
            : "Precision Jump — ready";
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
