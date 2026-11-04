using System.IO;
using Forms = System.Windows.Forms;

namespace WowCompanion.Desktop;

/// <summary>
/// The Oracle's eye in the notification area. Double-click (or "Show") brings the window back; "Exit" closes the app.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private bool _hinted;

    public TrayIcon(Action show, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        var showItem = new Forms.ToolStripMenuItem("Show WoW Oracle", null, (_, _) => show());
        showItem.Font = new System.Drawing.Font(showItem.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "WoW Oracle (Ctrl+Shift+_)",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
    }

    /// <summary>First time the window hides, say where it went.</summary>
    public void HintOnce()
    {
        if (_hinted) return;
        _hinted = true;
        _icon.ShowBalloonTip(2500, "WoW Oracle", "Still here in the tray. Ctrl+Shift+_ brings me back.", Forms.ToolTipIcon.None);
    }

    /// <summary>A due reminder, shown from the tray even while the window is hidden.</summary>
    public void ShowReminder(string title, string body)
    {
        var text = body.Trim().Length > 0 ? body.Trim() : "Reminder from WoW Oracle";
        if (text.Length > 200) text = text[..200] + "…";
        _icon.ShowBalloonTip(8000, title.Length > 0 ? title : "Reminder", text, Forms.ToolTipIcon.Info);
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/wow-oracle.ico"));
            if (info is not null)
            {
                using Stream stream = info.Stream;
                // Ask for the small-icon size so Windows picks the crisp 16/24/32 frame instead of shrinking 256.
                return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception)
        {
            // fall through to the exe's own icon
        }
        return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
