using System.ComponentModel;
using LiDPI.Core;
using LiDPI.UI.ViewModels;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace LiDPI.UI.Services;

/// <summary>Notification-area icon with a dark context menu. UI thread only.</summary>
public sealed class TrayService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _toggleItem;
    private readonly WinForms.ToolStripMenuItem _statusItem;
    private readonly WinForms.ToolStripMenuItem _showItem;
    private readonly WinForms.ToolStripMenuItem _updateItem;
    private readonly WinForms.ToolStripMenuItem _exitItem;
    private readonly Dictionary<EngineState, Drawing.Icon> _icons = new();
    private bool _disposed;

    public TrayService(MainViewModel vm, Action showWindow, Action exitApp)
    {
        _vm = vm;

        _statusItem = new WinForms.ToolStripMenuItem { Enabled = false };
        _toggleItem = new WinForms.ToolStripMenuItem(Strings.TrayConnect, null, (_, _) => _ = _vm.ToggleAsync());
        var showItem = _showItem = new WinForms.ToolStripMenuItem(Strings.TrayShow, null, (_, _) => showWindow());
        var updateItem = _updateItem = new WinForms.ToolStripMenuItem(Strings.TrayUpdateCheck, null, (_, _) => _ = CheckUpdatesAsync());
        var exitItem = _exitItem = new WinForms.ToolStripMenuItem(Strings.TrayExit, null, (_, _) => exitApp());
        showItem.Font = new Drawing.Font(showItem.Font, Drawing.FontStyle.Bold);

        _menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColorTable()) { RoundedEdges = false },
            ShowImageMargin = false,
            Padding = new WinForms.Padding(2, 4, 2, 4),
            Font = new Drawing.Font("Segoe UI", 9.5f),
        };
        _menu.Items.AddRange([_statusItem, new WinForms.ToolStripSeparator(), _toggleItem, showItem, updateItem, new WinForms.ToolStripSeparator(), exitItem]);
        foreach (WinForms.ToolStripItem item in _menu.Items)
        {
            item.ForeColor = Drawing.Color.FromArgb(0xE6, 0xE6, 0xEB);
            item.Padding = new WinForms.Padding(4, 3, 4, 3);
        }
        _statusItem.ForeColor = Drawing.Color.FromArgb(0x8A, 0x8A, 0x99);

        _notifyIcon = new WinForms.NotifyIcon { ContextMenuStrip = _menu, Visible = false };
        _notifyIcon.MouseUp += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) showWindow(); };
        _notifyIcon.BalloonTipClicked += (_, _) => showWindow();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.BalloonRequested += OnBalloonRequested;
        LiDPI.Localization.Loc.LanguageChanged += OnLanguageChanged;
        Update();
        _notifyIcon.Visible = true;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.State) or nameof(MainViewModel.Subtitle)) Update();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _showItem.Text = Strings.TrayShow;
        _updateItem.Text = Strings.TrayUpdateCheck;
        _exitItem.Text = Strings.TrayExit;
        Update();
    }

    private void Update()
    {
        if (_disposed) return;
        var state = _vm.State;
        if (!_icons.TryGetValue(state, out var icon))
            _icons[state] = icon = TrayIconFactory.Create(state);
        _notifyIcon.Icon = icon;

        var text = $"LiDPI — {_vm.StatusText}";
        _notifyIcon.Text = text.Length > 120 ? text[..120] : text;
        _statusItem.Text = $"{_vm.StatusText} · {_vm.Subtitle}";

        _toggleItem.Text = _vm.IsActive ? Strings.TrayDisconnect : Strings.TrayConnect;
        _toggleItem.Enabled = state != EngineState.Stopping;
    }

    private void OnBalloonRequested(string title, string text)
    {
        if (_disposed) return;
        try { _notifyIcon.ShowBalloonTip(8000, title, text, WinForms.ToolTipIcon.Warning); }
        catch { /* notifications disabled / shell not ready */ }
    }

    /// <summary>Tray "Check for updates": same manual check as Settings; the result arrives as a balloon.</summary>
    private async Task CheckUpdatesAsync()
    {
        var text = await _vm.CheckForUpdatesManualAsync();
        if (_disposed || text is null) return;
        try { _notifyIcon.ShowBalloonTip(5000, Strings.TrayUpdateBalloonTitle, text, WinForms.ToolTipIcon.Info); }
        catch { /* notifications disabled / shell not ready */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.BalloonRequested -= OnBalloonRequested;
        LiDPI.Localization.Loc.LanguageChanged -= OnLanguageChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        _icons.Clear();
    }

    /// <summary>Dark palette for the WinForms context menu.</summary>
    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(0x16, 0x16, 0x1D);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(0x2A, 0x2A, 0x34);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x24, 0x24, 0x2E);
        private static readonly Drawing.Color Sep = Drawing.Color.FromArgb(0x26, 0x26, 0x30);

        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color SeparatorDark => Sep;
        public override Drawing.Color SeparatorLight => Sep;
    }
}
