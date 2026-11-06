namespace CamSwitch;

/// The control window: shows which camera is active and lets you change everything.
public sealed class MainForm : Form
{
    static readonly Color Accent = Color.FromArgb(37, 99, 235);
    static readonly Color RigColor = Color.FromArgb(234, 88, 12);
    static readonly Color Muted = Color.FromArgb(100, 100, 110);
    static readonly Color CardBg = Color.White;
    static readonly Color PageBg = Color.FromArgb(243, 244, 246);

    readonly Switcher _sw;
    readonly Label _status = new() { AutoSize = true, ForeColor = Muted };
    readonly RadioButton _auto, _manual, _off;
    readonly CameraCard _desk, _rig;
    readonly ComboBox _default = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly NumericUpDown _absence = new() { Minimum = 3, Maximum = 120, Width = 60 };
    readonly NumericUpDown _returnMins = new() { Minimum = 0, Maximum = 120, Width = 60 };
    readonly CheckBox _startup = new() { Text = "Start CamSwitch when I sign in", AutoSize = true };
    readonly CheckBox _notify = new() { Text = "Show a notification when the camera changes", AutoSize = true };
    readonly CheckBox _updates = new() { Text = "Check for updates automatically", AutoSize = true };
    readonly Panel _updateBar = new() { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(220, 252, 231), Visible = false, Padding = new Padding(14, 8, 14, 8) };
    readonly Label _updateText = new() { AutoSize = true, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 10f) };
    readonly Button _updateBtn = new() { Text = "Install update", AutoSize = true, Dock = DockStyle.Right };
    Updater.Release? _pendingUpdate;
    bool _loading;

    public event Func<Task>? IdentifyRequested;
    public event Func<Task>? CheckUpdatesRequested;
    public event Func<Updater.Release, Task>? InstallUpdateRequested;

    public MainForm(Switcher sw)
    {
        _sw = sw;
        Text = "CamSwitch";
        Icon = Icons.App;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = PageBg;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(660, 760);

        // ---- header
        var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = CardBg, Padding = new Padding(16, 12, 16, 12) };
        var logo = new PictureBox { Image = Icons.AppBitmap(48), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Location = new Point(16, 14) };
        var title = new Label { Text = "CamSwitch", Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(74, 10) };
        _status.Location = new Point(76, 44);
        _status.Font = new Font("Segoe UI", 10f);
        header.Controls.AddRange([logo, title, _status]);

        _updateBar.Controls.Add(_updateText);
        _updateBar.Controls.Add(_updateBtn);
        _updateBtn.Click += async (_, _) =>
        {
            if (_pendingUpdate == null || InstallUpdateRequested == null) return;
            _updateBtn.Enabled = false;
            _updateBtn.Text = "Downloading…";
            await InstallUpdateRequested(_pendingUpdate);
        };

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(16, 12, 16, 12),
        };

        // ---- mode
        var modeBox = Section("Mode", 612);
        _auto = ModeRadio("Automatic", "Uses the camera that can see you. Switches when you move between the desk and the rig.");
        _manual = ModeRadio("Manual", "Stays on the camera you pick below.");
        _off = ModeRadio("Off", "Both cameras stay on and Windows and your apps choose as usual.");
        var modeFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Location = new Point(12, 30) };
        modeFlow.Controls.AddRange([_auto, _manual, _off]);
        modeBox.Controls.Add(modeFlow);
        modeBox.Height = 30 + 3 * 48 + 8;
        _auto.CheckedChanged += async (_, _) => { if (_auto.Checked && !_loading) await _sw.SetModeAsync(Mode.Auto); };
        _manual.CheckedChanged += async (_, _) => { if (_manual.Checked && !_loading) await _sw.SetModeAsync(Mode.Manual); };
        _off.CheckedChanged += async (_, _) => { if (_off.Checked && !_loading) await _sw.SetModeAsync(Mode.Off); };

        // ---- cameras
        var cams = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Width = 612, Height = 300, Margin = new Padding(0, 0, 0, 12) };
        cams.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cams.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _desk = new CameraCard(Cam.Desk, Accent);
        _rig = new CameraCard(Cam.Rig, RigColor);
        _desk.UseClicked += async c => await _sw.UseAsync(c);
        _rig.UseClicked += async c => await _sw.UseAsync(c);
        cams.Controls.Add(_desk, 0, 0);
        cams.Controls.Add(_rig, 1, 0);

        // ---- settings
        var settings = Section("Settings", 612);
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Location = new Point(12, 30), Width = 588 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6) });
            c.Anchor = AnchorStyles.Left;
            grid.Controls.Add(c);
        }
        _default.Items.AddRange([_sw.Cfg.DeskName, _sw.Cfg.RigName]);
        row("Default camera (used at start-up)", _default);
        row("Seconds without a face before checking the other camera", _absence);
        row("Minutes with nobody around before going back to default (0 = never)", _returnMins);
        var checks = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        checks.Controls.AddRange([_startup, _notify, _updates]);
        grid.Controls.Add(checks);
        grid.SetColumnSpan(checks, 2);
        settings.Controls.Add(grid);
        settings.Height = 30 + 3 * 34 + 3 * 26 + 24;

        _default.SelectedIndexChanged += (_, _) => { if (!_loading) _sw.SetDefault(_default.SelectedIndex == 1 ? Cam.Rig : Cam.Desk); };
        _absence.ValueChanged += (_, _) => { if (!_loading) { _sw.Cfg.AbsenceSeconds = (int)_absence.Value; _sw.Cfg.Save(); } };
        _returnMins.ValueChanged += (_, _) => { if (!_loading) { _sw.Cfg.ReturnToDefaultMinutes = (int)_returnMins.Value; _sw.Cfg.Save(); } };
        _notify.CheckedChanged += (_, _) => { if (!_loading) { _sw.Cfg.ShowNotifications = _notify.Checked; _sw.Cfg.Save(); } };
        _updates.CheckedChanged += (_, _) => { if (!_loading) { _sw.Cfg.CheckForUpdates = _updates.Checked; _sw.Cfg.Save(); } };
        _startup.CheckedChanged += async (_, _) =>
        {
            if (_loading) return;
            _sw.Cfg.StartWithWindows = _startup.Checked;
            _sw.Cfg.Save();
            var on = _startup.Checked;
            await Task.Run(() => Platform.SyncStartWithWindows(on));
        };

        // ---- footer
        var footer = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Width = 612, WrapContents = false };
        var identify = new Button { Text = "Identify cameras…", AutoSize = true };
        identify.Click += async (_, _) => { if (IdentifyRequested != null) await IdentifyRequested(); };
        var log = new Button { Text = "Open log", AutoSize = true };
        log.Click += (_, _) => OpenInNotepad(Config.LogPath);
        var cfg = new Button { Text = "Open settings file", AutoSize = true };
        cfg.Click += (_, _) => OpenInNotepad(Config.FilePath);
        var check = new Button { Text = "Check for updates", AutoSize = true };
        check.Click += async (_, _) => { if (CheckUpdatesRequested != null) await CheckUpdatesRequested(); };
        footer.Controls.AddRange([identify, check, log, cfg]);

        var about = new LinkLabel { Text = $"Version {Platform.Version} · github.com/{Updater.Repo}", AutoSize = true, LinkColor = Muted, Margin = new Padding(3, 8, 3, 3) };
        about.LinkClicked += (_, _) => OpenUrl($"https://github.com/{Updater.Repo}");

        body.Controls.AddRange([modeBox, cams, settings, footer, about]);
        Controls.Add(body);
        Controls.Add(_updateBar);
        Controls.Add(header);

        _sw.Changed += OnChanged;
        LoadSettings();
        OnChanged();
    }

    public void ShowUpdate(Updater.Release r)
    {
        _pendingUpdate = r;
        _updateText.Text = $"CamSwitch {r.Version} is available (you have {Platform.Version}).";
        _updateBtn.Enabled = true;
        _updateBtn.Text = "Install update";
        _updateBar.Visible = true;
    }

    public void ShowUpdateProgress(int pct) => _updateBtn.Text = $"Downloading… {pct}%";

    void LoadSettings()
    {
        _loading = true;
        var c = _sw.Cfg;
        _default.SelectedIndex = c.DefaultCamera == Cam.Rig ? 1 : 0;
        _absence.Value = Math.Clamp(c.AbsenceSeconds, (int)_absence.Minimum, (int)_absence.Maximum);
        _returnMins.Value = Math.Clamp(c.ReturnToDefaultMinutes, (int)_returnMins.Minimum, (int)_returnMins.Maximum);
        _startup.Checked = c.StartWithWindows;
        _notify.Checked = c.ShowNotifications;
        _updates.Checked = c.CheckForUpdates;
        _loading = false;
    }

    void OnChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(OnChanged); return; }
        _loading = true;
        _auto.Checked = _sw.Mode == Mode.Auto;
        _manual.Checked = _sw.Mode == Mode.Manual;
        _off.Checked = _sw.Mode == Mode.Off;
        _loading = false;

        var c = _sw.Cfg;
        var activeName = c.NameOf(c.Active);
        _status.Text = _sw.Mode == Mode.Off
            ? "Switcher off · both cameras on"
            : $"Using the {activeName.ToLower()} camera · {_sw.Status}";

        foreach (var card in new[] { _desk, _rig })
        {
            var isActive = _sw.Mode != Mode.Off && c.Active == card.Cam;
            card.Render(
                c.NameOf(card.Cam),
                badge: _sw.Mode == Mode.Off ? "ON" : isActive ? "IN USE" : "OFF",
                active: isActive,
                isDefault: c.DefaultCamera == card.Cam,
                preview: isActive ? _sw.Preview : null,
                caption: _sw.Mode == Mode.Off ? "Available to all apps"
                    : isActive ? (_sw.FaceSeen == true ? "You're in view" : _sw.FaceSeen == false ? "Nobody in view" : "Live view")
                    : "Turned off until you sit here",
                busy: _sw.Busy);
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _sw.PreviewWanted = Visible;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window just hides it; CamSwitch keeps running in the tray.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        _sw.Changed -= OnChanged;
        base.OnFormClosing(e);
    }

    static GroupBox Section(string title, int width) =>
        new() { Text = title, Width = width, Font = new Font("Segoe UI Semibold", 10f), BackColor = CardBg, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(10) };

    static RadioButton ModeRadio(string title, string detail) => new()
    {
        Text = $"{title}\n{detail}",
        Font = new Font("Segoe UI", 9.5f),
        AutoSize = false, Width = 570, Height = 46,
        TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.MiddleLeft,
    };

    internal static void OpenInNotepad(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    internal static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}

/// One camera's tile in the window.
sealed class CameraCard : Panel
{
    public Cam Cam { get; }
    public event Func<Cam, Task>? UseClicked;

    readonly Color _color;
    readonly Label _name = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 11.5f), Location = new Point(12, 10) };
    readonly Label _badge = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 8f), Padding = new Padding(6, 2, 6, 2), ForeColor = Color.White };
    readonly Label _default = new() { AutoSize = true, Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(100, 100, 110), Text = "★ Default" };
    readonly PictureBox _preview = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(30, 30, 36), Location = new Point(12, 42), Size = new Size(274, 154) };
    readonly Label _caption = new() { AutoSize = true, Location = new Point(12, 202), ForeColor = Color.FromArgb(70, 70, 80) };
    readonly Button _use = new() { Text = "Use this camera", Location = new Point(12, 232), Size = new Size(274, 34) };
    readonly Label _placeholder = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(160, 160, 170) };

    public CameraCard(Cam cam, Color color)
    {
        Cam = cam;
        _color = color;
        Dock = DockStyle.Fill;
        Margin = new Padding(cam == Cam.Desk ? 0 : 6, 0, cam == Cam.Desk ? 6 : 0, 0);
        BackColor = Color.White;
        _preview.Controls.Add(_placeholder);
        _use.Click += async (_, _) => { if (UseClicked != null) await UseClicked(Cam); };
        Controls.AddRange([_name, _badge, _default, _preview, _caption, _use]);
    }

    public void Render(string name, string badge, bool active, bool isDefault, Bitmap? preview, string caption, bool busy)
    {
        _name.Text = name;
        _badge.Text = badge;
        _badge.BackColor = badge == "OFF" ? Color.FromArgb(160, 160, 170) : _color;
        _badge.Location = new Point(_name.Right + 8, 14);
        _default.Visible = isDefault;
        _default.Location = new Point(_badge.Right + 8, 15);
        _preview.Image = preview;
        _placeholder.Visible = preview == null;
        _placeholder.Text = active ? "Starting camera…" : badge == "ON" ? "On" : "Off";
        _caption.Text = caption;
        _use.Enabled = !busy;
        _use.Text = active ? "In use, stay on this one" : "Use this camera";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_badge.Text == "IN USE" ? _color : Color.FromArgb(225, 225, 230), _badge.Text == "IN USE" ? 3 : 1);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }
}
