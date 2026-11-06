using System.Runtime.InteropServices;

namespace CamSwitch;

public sealed class TrayApp : ApplicationContext
{
    readonly Switcher _sw;
    readonly NotifyIcon _tray;
    readonly HostWindow _host;
    readonly ToolStripMenuItem _statusItem, _autoItem, _manualItem, _offItem, _deskItem, _rigItem;
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    MainForm? _form;
    Updater.Release? _update;
    bool _exiting;

    public TrayApp(bool showWindow)
    {
        var cfg = Config.Load();
        _sw = new Switcher(cfg);

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _autoItem = new ToolStripMenuItem("Automatic", null, async (_, _) => await _sw.SetModeAsync(Mode.Auto));
        _manualItem = new ToolStripMenuItem("Manual", null, async (_, _) => await _sw.SetModeAsync(Mode.Manual));
        _offItem = new ToolStripMenuItem("Off (let Windows decide)", null, async (_, _) => await _sw.SetModeAsync(Mode.Off));
        _deskItem = new ToolStripMenuItem("", null, async (_, _) => await _sw.UseAsync(Cam.Desk));
        _rigItem = new ToolStripMenuItem("", null, async (_, _) => await _sw.UseAsync(Cam.Rig));

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open CamSwitch", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoItem);
        menu.Items.Add(_manualItem);
        menu.Items.Add(_offItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_deskItem);
        menu.Items.Add(_rigItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (turns both cameras on)", null, async (_, _) => await ExitAsync());

        _tray = new NotifyIcon { Icon = Icons.ForTray("off"), ContextMenuStrip = menu, Visible = true, Text = "CamSwitch" };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) => ShowWindow();

        _host = new HostWindow(
            onShow: ShowWindow,
            onQuit: async () => await ExitAsync(),
            onHotkey: async () => await _sw.ToggleAsync());

        _sw.Changed += UpdateTray;
        _sw.Notify += (t, m) => _tray.ShowBalloonTip(2500, t, m, ToolTipIcon.Info);
        UpdateTray();

        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(quiet: true);
        _ = StartAsync(showWindow);
    }

    async Task StartAsync(bool showWindow)
    {
        if (showWindow) ShowWindow();
        await _sw.StartAsync(ConfirmSitAtDesk, Message);

        if (_sw.Cfg.StartWithWindows && !Platform.TaskExists())
            await Task.Run(Platform.InstallTask);

        if (_sw.Cfg.CheckForUpdates)
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            await CheckUpdatesAsync(quiet: true);
            _updateTimer.Start();
        }
    }

    void ShowWindow()
    {
        if (_exiting) return;
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(_sw);
            _form.IdentifyRequested += () => _sw.IdentifyAsync(ConfirmSitAtDesk, Message);
            _form.CheckUpdatesRequested += () => CheckUpdatesAsync(quiet: false);
            _form.InstallUpdateRequested += InstallUpdateAsync;
            if (_update != null) _form.ShowUpdate(_update);
        }
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    IWin32Window? Owner => _form is { Visible: true } f ? f : null;

    bool ConfirmSitAtDesk() =>
        MessageBox.Show(Owner, "Sit at your DESK facing its camera, then click OK.\n\nCamSwitch will look through both cameras for a few seconds to work out which one is at your desk.",
            "CamSwitch: identify cameras", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;

    void Message(string text, bool ok) =>
        MessageBox.Show(Owner, text, "CamSwitch", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

    void UpdateTray()
    {
        var c = _sw.Cfg;
        _tray.Icon = Icons.ForTray(_sw.Mode == Mode.Off ? "off" : c.Active == Cam.Rig ? "rig" : "desk");
        var tip = _sw.Mode == Mode.Off ? "CamSwitch: off" : $"CamSwitch: {c.NameOf(c.Active).ToLower()} camera ({_sw.Mode.ToString().ToLower()})";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        _statusItem.Text = _sw.Mode == Mode.Off ? "Off: both cameras on" : $"Using {c.NameOf(c.Active).ToLower()} camera: {_sw.Status}";
        _autoItem.Checked = _sw.Mode == Mode.Auto;
        _manualItem.Checked = _sw.Mode == Mode.Manual;
        _offItem.Checked = _sw.Mode == Mode.Off;
        _deskItem.Text = $"Use {c.DeskName.ToLower()} camera";
        _rigItem.Text = $"Use {c.RigName.ToLower()} camera";
        _deskItem.Checked = _sw.Mode != Mode.Off && c.Active == Cam.Desk;
        _rigItem.Checked = _sw.Mode != Mode.Off && c.Active == Cam.Rig;
    }

    async Task CheckUpdatesAsync(bool quiet)
    {
        try
        {
            var r = await Updater.CheckAsync();
            if (r == null)
            {
                if (!quiet) Message($"You're on the latest version ({Platform.Version}).", true);
                return;
            }
            var isNew = _update?.Version != r.Version;
            _update = r;
            _form?.ShowUpdate(r);
            if (isNew)
                _tray.ShowBalloonTip(5000, "CamSwitch update", $"Version {r.Version} is available. Open CamSwitch to install it.", ToolTipIcon.Info);
            if (!quiet) ShowWindow();
        }
        catch (Exception ex)
        {
            Log.Write("Update check failed: " + ex.Message);
            if (!quiet) Message("Couldn't check for updates: " + ex.Message, false);
        }
    }

    async Task InstallUpdateAsync(Updater.Release r)
    {
        try
        {
            await Updater.DownloadAndRunAsync(r, new Progress<int>(p => _form?.ShowUpdateProgress(p)));
            // The installer will close us; turn both cameras back on first.
            await ExitAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Update failed: " + ex);
            Message("The update didn't download: " + ex.Message + $"\n\nYou can get it from {Updater.ReleasesPage}", false);
            _form?.ShowUpdate(r);
        }
    }

    async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _updateTimer.Stop();
        await _sw.ShutdownAsync();
        _host.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        if (_form != null && !_form.IsDisposed) { _form.Hide(); _form.Dispose(); }
        ExitThread();
    }

    /// Hidden window: receives "show" and "quit" from other copies (installer, Start menu)
    /// and the global hotkey Ctrl+Alt+C.
    sealed class HostWindow : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312, MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000, HOTKEY_ID = 1;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        readonly Action _onShow;
        readonly Action _onQuit;
        readonly Action _onHotkey;

        public HostWindow(Action onShow, Action onQuit, Action onHotkey)
        {
            _onShow = onShow; _onQuit = onQuit; _onHotkey = onHotkey;
            // A top-level (not message-only) window so FindWindow can see it; it's never shown.
            CreateHandle(new CreateParams { Caption = Platform.HostWindowTitle });
            Platform.AllowMessagesFromLowerRights(Handle);
            if (!RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (int)Keys.C))
                Log.Write("Couldn't register Ctrl+Alt+C (another app may be using it)");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) _onHotkey();
            else if (m.Msg == Platform.WM_SHOW) _onShow();
            else if (m.Msg == Platform.WM_QUIT_APP) _onQuit();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, HOTKEY_ID);
            DestroyHandle();
        }
    }
}
