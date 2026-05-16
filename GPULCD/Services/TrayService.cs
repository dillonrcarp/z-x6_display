using System.Drawing;
using System.Windows;
using GPULCD.Models;
using GPULCD.Rendering;
using WinForms = System.Windows.Forms;

namespace GPULCD.Services;

public class TrayService : IDisposable
{
    private WinForms.NotifyIcon? _notifyIcon;
    private Window? _mainWindow;
    private readonly Action? _onExit;

    public TrayService(Action? onExit = null)
    {
        _onExit = onExit;
    }

    public void Initialize(Window mainWindow, FrameRenderer? renderer = null)
    {
        _mainWindow = mainWindow;

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = CreateDefaultIcon(),
            Text = "GPULCD Monitor — Disconnected",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        // Page switching submenu
        if (renderer != null && renderer.Pages.Count > 0)
        {
            var pagesMenu = new WinForms.ToolStripMenuItem("Pages");
            for (int i = 0; i < renderer.Pages.Count; i++)
            {
                int idx = i; // capture for closure
                var page = renderer.Pages[i];
                pagesMenu.DropDownItems.Add(page.Name, null, (_, _) => renderer.SetPage(idx));
            }
            pagesMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
            pagesMenu.DropDownItems.Add("Next Page", null, (_, _) => renderer.NextPage());
            pagesMenu.DropDownItems.Add("Previous Page", null, (_, _) => renderer.PrevPage());
            menu.Items.Add(pagesMenu);
            menu.Items.Add(new WinForms.ToolStripSeparator());
        }

        menu.Items.Add("Exit", null, (_, _) => _onExit?.Invoke());
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                ToggleWindow();
        };
    }

    public void UpdateState(ConnectionState state, bool sensorAvailable)
    {
        if (_notifyIcon == null) return;

        var (tooltip, icon) = state switch
        {
            ConnectionState.Connected when sensorAvailable =>
                ("GPULCD Monitor — Running", CreateIcon(Color.FromArgb(80, 200, 80))),
            ConnectionState.Connected =>
                ("GPULCD Monitor — No sensor data (check HWiNFO)", CreateIcon(Color.FromArgb(255, 200, 50))),
            ConnectionState.Reconnecting =>
                ("GPULCD Monitor — Reconnecting...", CreateIcon(Color.FromArgb(255, 200, 50))),
            ConnectionState.Connecting =>
                ("GPULCD Monitor — Connecting...", CreateIcon(Color.FromArgb(255, 200, 50))),
            _ =>
                ("GPULCD Monitor — Disconnected", CreateIcon(Color.FromArgb(200, 60, 60)))
        };

        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Icon = icon;
    }

    private void ShowWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ToggleWindow()
    {
        if (_mainWindow == null) return;
        if (_mainWindow.IsVisible) _mainWindow.Hide();
        else ShowWindow();
    }

    private static Icon CreateDefaultIcon() => CreateIcon(Color.FromArgb(100, 100, 120));

    private static Icon CreateIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 2, 2, 12, 12);
        using var font = new Font("Arial", 7, System.Drawing.FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString("L", font, textBrush, 3.5f, 2.5f);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        GC.SuppressFinalize(this);
    }
}
