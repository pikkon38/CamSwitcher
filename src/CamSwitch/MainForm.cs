using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CamSwitch;

/// The control window. Drawn entirely by hand for a frosted-glass look:
/// a soft blurred background (tinted by the live camera picture) behind translucent cards.
public sealed class MainForm : Form
{
    // ---- palette
    static readonly Color Ink = Color.FromArgb(245, 255, 255, 255);
    static readonly Color InkSoft = Color.FromArgb(175, 255, 255, 255);
    static readonly Color InkFaint = Color.FromArgb(120, 255, 255, 255);
    static readonly Color Dark = Color.FromArgb(255, 22, 28, 24);
    static readonly Color Green = Color.FromArgb(255, 74, 222, 128);
    static readonly Color Amber = Color.FromArgb(255, 251, 191, 36);
    static readonly Color TitleBar = Color.FromArgb(255, 44, 62, 66);

    readonly Switcher _sw;
    readonly string _family;
    readonly string? _semibold;
    readonly Bitmap _noise = MakeNoise();
    readonly Bitmap _logo = Icons.AppBitmap(128);

    // layout / interaction state
    sealed record Hit(string Id, RectangleF R, Action Act);
    readonly List<Hit> _hits = new();
    string? _hover, _pressed;
    float _scroll, _contentH;

    // background cache
    Bitmap? _bg;
    bool _glass; // real frosted glass (Windows 11 acrylic) showing the desktop behind
    Bitmap? _frame;
    bool _bgDirty = true;

    Updater.Release? _update;
    string? _updateBusy;

    public event Func<Task>? IdentifyRequested;
    public event Func<Task>? CheckUpdatesRequested;
    public event Func<Updater.Release, Task>? InstallUpdateRequested;

    float S => DeviceDpi / 96f;

    public MainForm(Switcher sw)
    {
        _sw = sw;
        var names = FontFamily.Families.Select(f => f.Name).ToHashSet();
        _family = names.Contains("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
        _semibold = names.Contains(_family + " Semibold") ? _family + " Semibold" : names.Contains("Segoe UI Semibold") ? "Segoe UI Semibold" : null;
        Text = "CamSwitch";
        Icon = Icons.App;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Dark;
        DoubleBuffered = false; // we buffer ourselves so the alpha channel reaches the glass
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;

        var s = DeviceDpi / 96f;
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size((int)(720 * s), Math.Min((int)(812 * s), (int)(work.Height * 0.92)));
        MinimumSize = new Size((int)(560 * s), (int)(420 * s));

        _sw.Changed += OnChanged;
    }

    // ------------------------------------------------------------------ public API (used by TrayApp)

    public void ShowUpdate(Updater.Release r) { _update = r; _updateBusy = null; Invalidate(); }
    public void ShowUpdateProgress(int pct) { _updateBusy = $"Downloading {pct}%"; Invalidate(); }

    /// Renders the window contents to a bitmap (used by --screenshot in CI).
    internal Bitmap Snapshot()
    {
        var b = new Bitmap(ClientSize.Width, ClientSize.Height);
        using var g = Graphics.FromImage(b);
        if (_bgDirty || _bg == null || _bg.Size != ClientSize) RebuildBackground();
        g.DrawImageUnscaled(_bg!, 0, 0);
        Draw(g);
        return b;
    }

    // ------------------------------------------------------------------ window plumbing

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, 4);            // dark title bar

            // Windows 11 (22H2+): acrylic backdrop, the frosted glass that blurs whatever is behind the window.
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
                DwmExtendFrameIntoClientArea(Handle, ref m);
                int acrylic = 3;                                       // DWMSBT_TRANSIENTWINDOW
                _glass = DwmSetWindowAttribute(Handle, 38, ref acrylic, 4) == 0;
            }
            if (!_glass)
            {
                int caption = TitleBar.R | (TitleBar.G << 8) | (TitleBar.B << 16);
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);     // title bar colour
            }
            _bgDirty = true;
        }
        catch { _glass = false; }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _sw.PreviewWanted = Visible;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing just hides the window; CamSwitch keeps running in the tray.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        _sw.Changed -= OnChanged;
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); _bgDirty = true; ClampScroll(); Invalidate(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); _bgDirty = true; Invalidate(); }

    void OnChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(OnChanged); return; }
        Invalidate();
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = _hits.LastOrDefault(x => x.R.Contains(e.Location))?.Id;
        if (h != _hover) { _hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != null) { _hover = null; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _pressed = _hits.LastOrDefault(x => x.R.Contains(e.Location))?.Id; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var hit = _hits.LastOrDefault(x => x.R.Contains(e.Location));
        var pressed = _pressed;
        _pressed = null;
        if (hit != null && hit.Id == pressed) hit.Act();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll -= e.Delta / 120f * 60 * S;
        ClampScroll();
        Invalidate();
    }

    void ClampScroll() => _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentH - ClientSize.Height));

    // ------------------------------------------------------------------ painting

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var size = ClientSize;
        if (size.Width < 1 || size.Height < 1) return;
        if (_bgDirty || _bg == null || _bg.Size != size) RebuildBackground();
        if (_frame == null || _frame.Size != size) { _frame?.Dispose(); _frame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb); }

        using (var g = Graphics.FromImage(_frame))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(_bg!, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            Draw(g);
        }

        if (_glass)
        {
            // Copy with the alpha channel intact so the acrylic shows through the see-through parts.
            var hdc = e.Graphics.GetHdc();
            var mem = CreateCompatibleDC(hdc);
            var hbm = _frame.GetHbitmap(Color.FromArgb(0));
            var old = SelectObject(mem, hbm);
            BitBlt(hdc, 0, 0, size.Width, size.Height, mem, 0, 0, 0x00CC0020);
            SelectObject(mem, old);
            DeleteObject(hbm);
            DeleteDC(mem);
            e.Graphics.ReleaseHdc(hdc);
        }
        else e.Graphics.DrawImageUnscaled(_frame, 0, 0);
    }

    void Draw(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _hits.Clear();

        var s = S;
        var pad = 22 * s;
        var gap = 14 * s;
        var W = ClientSize.Width - 2 * pad;
        var x0 = pad;
        var y = pad - _scroll;
        var c = _sw.Cfg;

        // ---- header
        g.DrawImage(_logo, x0, y, 46 * s, 46 * s);
        using (var f = F(16.5f, FontStyle.Bold)) g.DrawString("CamSwitch", f, B(Ink), x0 + 60 * s, y - 1 * s);
        var status = _sw.Mode == Mode.Off ? "Off · both cameras are on" : $"Using the {c.NameOf(c.Active).ToLower()} camera · {_sw.Status}";
        using (var f = F(10f)) g.DrawString(status, f, B(InkSoft), new RectangleF(x0 + 61 * s, y + 27 * s, W - 61 * s - 190 * s, 22 * s), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
        if (_update != null)
        {
            var label = _updateBusy ?? $"Update to {_update.Version}";
            var r = new RectangleF(x0 + W - 170 * s, y + 6 * s, 170 * s, 34 * s);
            Button(g, "update", r, label, primary: true, enabled: _updateBusy == null, () =>
            {
                if (_update == null || InstallUpdateRequested == null) return;
                _updateBusy = "Downloading…";
                Invalidate();
                _ = InstallUpdateRequested(_update);
            });
        }
        y += 46 * s + 20 * s;

        // ---- mode
        {
            var cardH = 106 * s;
            var card = new RectangleF(x0, y, W, cardH);
            Glass(g, card, 18 * s);
            var inner = Inset(card, 16 * s);
            var seg = new RectangleF(inner.X, inner.Y, inner.Width, 40 * s);
            var modes = new[] { Mode.Auto, Mode.Manual, Mode.Off };
            Segmented(g, "mode", seg, ["Automatic", "Manual", "Off"], Array.IndexOf(modes, _sw.Mode),
                i => _ = _sw.SetModeAsync(modes[i]));
            var desc = _sw.Mode switch
            {
                Mode.Auto => "Follows your face. Only the camera you're in front of is on, so every app uses it.",
                Mode.Manual => "Stays on the camera you pick below until you change it.",
                _ => "CamSwitch is off. Both cameras stay on and Windows and your apps choose as usual.",
            };
            using (var f = F(9.75f)) g.DrawString(desc, f, B(InkSoft), new RectangleF(inner.X + 4 * s, seg.Bottom + 11 * s, inner.Width, 22 * s));
            y += cardH + gap;
        }

        // ---- camera cards
        {
            var cw = (W - gap) / 2;
            var previewH = (cw - 32 * s) * 9 / 16;
            var cardH = 16 * s + 26 * s + 12 * s + previewH + 10 * s + 20 * s + 12 * s + 38 * s + 16 * s;
            CameraCard(g, Cam.Desk, new RectangleF(x0, y, cw, cardH), previewH);
            CameraCard(g, Cam.Rig, new RectangleF(x0 + cw + gap, y, cw, cardH), previewH);
            y += cardH + gap;
        }

        // ---- settings
        {
            var rowH = 44 * s;
            var cardH = 16 * s + 24 * s + 8 * s + 3 * rowH + 10 * s;
            var card = new RectangleF(x0, y, W, cardH);
            Glass(g, card, 18 * s);
            var inner = Inset(card, 16 * s);
            using (var f = F(11.5f, FontStyle.Bold)) g.DrawString("Settings", f, B(Ink), inner.X, inner.Y);
            var top = inner.Y + 32 * s;
            var colW = (inner.Width - 28 * s) / 2;
            var lx = inner.X;
            var rx = inner.X + colW + 28 * s;

            // left column
            RowLabel(g, "Default camera", lx, top, rowH);
            Segmented(g, "default", new RectangleF(lx + colW - 180 * s, top + 6 * s, 176 * s, 32 * s), [c.DeskName, c.RigName],
                c.DefaultCamera == Cam.Desk ? 0 : 1, i => _sw.SetDefault(i == 0 ? Cam.Desk : Cam.Rig), small: true);

            RowLabel(g, "Check other camera after", lx, top + rowH, rowH);
            Stepper(g, "absence", new RectangleF(lx + colW - 130 * s, top + rowH + 6 * s, 130 * s, 32 * s), $"{c.AbsenceSeconds} s",
                () => SetCfg(() => c.AbsenceSeconds = Math.Max(3, c.AbsenceSeconds - 1)),
                () => SetCfg(() => c.AbsenceSeconds = Math.Min(120, c.AbsenceSeconds + 1)));

            RowLabel(g, "Back to default when away", lx, top + 2 * rowH, rowH);
            Stepper(g, "return", new RectangleF(lx + colW - 130 * s, top + 2 * rowH + 6 * s, 130 * s, 32 * s),
                c.ReturnToDefaultMinutes == 0 ? "Never" : $"{c.ReturnToDefaultMinutes} min",
                () => SetCfg(() => c.ReturnToDefaultMinutes = Math.Max(0, c.ReturnToDefaultMinutes - 1)),
                () => SetCfg(() => c.ReturnToDefaultMinutes = Math.Min(120, c.ReturnToDefaultMinutes + 1)));

            // right column
            Toggle(g, "startup", rx, top, colW, rowH, "Start when I sign in", c.StartWithWindows, () =>
            {
                c.StartWithWindows = !c.StartWithWindows; c.Save(); Invalidate();
                var on = c.StartWithWindows;
                _ = Task.Run(() => Platform.SyncStartWithWindows(on));
            });
            Toggle(g, "notify", rx, top + rowH, colW, rowH, "Notify me when it switches", c.ShowNotifications,
                () => SetCfg(() => c.ShowNotifications = !c.ShowNotifications));
            Toggle(g, "updates", rx, top + 2 * rowH, colW, rowH, "Check for updates", c.CheckForUpdates,
                () => SetCfg(() => c.CheckForUpdates = !c.CheckForUpdates));

            // divider between columns
            using (var pen = new Pen(Color.FromArgb(30, 255, 255, 255), 1)) g.DrawLine(pen, inner.X + colW + 14 * s, top + 6 * s, inner.X + colW + 14 * s, top + 3 * rowH - 6 * s);
            y += cardH + gap;
        }

        // ---- footer
        {
            var fx = x0;
            var fh = 34 * s;
            void link(string id, string text, Action act)
            {
                using var f = F(9.5f);
                var w = g.MeasureString(text, f).Width + 26 * s;
                Button(g, id, new RectangleF(fx, y, w, fh), text, primary: false, enabled: true, act);
                fx += w + 8 * s;
            }
            link("identify", "Identify cameras", () => { if (IdentifyRequested != null) _ = IdentifyRequested(); });
            link("check", "Check for updates", () => { if (CheckUpdatesRequested != null) _ = CheckUpdatesRequested(); });
            link("log", "Log", () => OpenInNotepad(Config.LogPath));
            link("cfg", "Settings file", () => OpenInNotepad(Config.FilePath));

            using var vf = F(9f);
            var ver = $"v{Platform.Version}";
            var vw = g.MeasureString(ver, vf).Width;
            var vr = new RectangleF(x0 + W - vw - 4 * s, y + 8 * s, vw + 4 * s, 20 * s);
            var hov = _hover == "repo";
            g.DrawString(ver, vf, B(hov ? Ink : InkFaint), vr.X, vr.Y);
            _hits.Add(new Hit("repo", vr, () => OpenUrl($"https://github.com/{Updater.Repo}")));
            y += fh + pad;
        }

        _contentH = y + _scroll;
        // scroll indicator
        if (_contentH > ClientSize.Height + 1)
        {
            var track = ClientSize.Height - 16 * s;
            var thumbH = Math.Max(40 * s, track * ClientSize.Height / _contentH);
            var thumbY = 8 * s + (track - thumbH) * (_scroll / (_contentH - ClientSize.Height));
            using var p = RoundRect(new RectangleF(ClientSize.Width - 8 * s, thumbY, 4 * s, thumbH), 2 * s);
            g.FillPath(B(Color.FromArgb(90, 255, 255, 255)), p);
        }
    }

    void CameraCard(Graphics g, Cam cam, RectangleF card, float previewH)
    {
        var s = S;
        var c = _sw.Cfg;
        var off = _sw.Mode == Mode.Off;
        var active = !off && c.Active == cam;
        Glass(g, card, 18 * s, highlight: active);
        var inner = Inset(card, 16 * s);

        // title + status pill
        using (var f = F(13f, FontStyle.Bold)) g.DrawString(c.NameOf(cam), f, B(Ink), inner.X, inner.Y);
        var (pillText, dot) = off ? ("On", Ink) : active ? ("In use", Green) : ("Off", InkFaint);
        using (var f = F(8.75f, FontStyle.Bold))
        {
            var tw = g.MeasureString(pillText, f).Width;
            var pr = new RectangleF(inner.Right - tw - 30 * s, inner.Y + 2 * s, tw + 30 * s, 24 * s);
            using var pp = RoundRect(pr, 12 * s);
            g.FillPath(B(Color.FromArgb(active ? 60 : 35, 0, 0, 0)), pp);
            using var pen = new Pen(Color.FromArgb(50, 255, 255, 255), 1);
            g.DrawPath(pen, pp);
            g.FillEllipse(B(dot), pr.X + 10 * s, pr.Y + 8.5f * s, 7 * s, 7 * s);
            g.DrawString(pillText, f, B(Ink), pr.X + 21 * s, pr.Y + 4 * s);
        }
        if (c.DefaultCamera == cam)
            using (var f = F(8.5f))
            {
                using var nf = F(13f, FontStyle.Bold);
                var nameW = g.MeasureString(c.NameOf(cam), nf).Width;
                g.DrawString("★ default", f, B(Amber), inner.X + nameW + 4 * s, inner.Y + 6 * s);
            }

        // preview
        var pv = new RectangleF(inner.X, inner.Y + 38 * s, inner.Width, previewH);
        using (var path = RoundRect(pv, 12 * s))
        {
            g.FillPath(B(Color.FromArgb(110, 0, 0, 0)), path);
            var img = active ? _sw.Preview : null;
            if (img != null)
            {
                var state = g.Save();
                g.SetClip(path);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                // cover-fit
                var scale = Math.Max(pv.Width / img.Width, pv.Height / img.Height);
                var w = img.Width * scale; var h = img.Height * scale;
                g.DrawImage(img, pv.X + (pv.Width - w) / 2, pv.Y + (pv.Height - h) / 2, w, h);
                g.Restore(state);
            }
            else
            {
                var msg = off ? "On · available to every app" : active ? "Starting camera…" : "Off · turns on when you sit here";
                using var f = F(9.5f);
                g.DrawString(msg, f, B(InkFaint), pv, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
            using var pen = new Pen(Color.FromArgb(40, 255, 255, 255), 1);
            g.DrawPath(pen, path);
        }

        // caption
        var caption = off ? "Windows and your apps choose"
            : active ? (_sw.FaceSeen == true ? "You're in view" : _sw.FaceSeen == false ? "Nobody in view" : "Live view")
            : _sw.Mode == Mode.Auto ? "Watching for you here" : "Not in use";
        using (var f = F(9.5f)) g.DrawString(caption, f, B(InkSoft), inner.X + 2 * s, pv.Bottom + 9 * s);

        // button
        var br = new RectangleF(inner.X, card.Bottom - 16 * s - 38 * s, inner.Width, 38 * s);
        var label = active ? (_sw.Mode == Mode.Manual ? "In use" : "Stay on this camera") : "Use this camera";
        Button(g, "use" + cam, br, label, primary: !active, enabled: !_sw.Busy && !(active && _sw.Mode == Mode.Manual),
            () => _ = _sw.UseAsync(cam));
    }

    // ------------------------------------------------------------------ widgets

    void Glass(Graphics g, RectangleF r, float radius, bool highlight = false)
    {
        using var path = RoundRect(r, radius);
        // soft drop shadow
        using (var sp = RoundRect(new RectangleF(r.X, r.Y + 3 * S, r.Width, r.Height), radius))
            g.FillPath(B(Color.FromArgb(30, 0, 0, 0)), sp);
        g.FillPath(B(Color.FromArgb(highlight ? 44 : 26, 255, 255, 255)), path);
        // top sheen
        using (var sheen = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height * 0.55f + 2),
                   Color.FromArgb(highlight ? 40 : 28, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
        {
            var st = g.Save();
            g.SetClip(path);
            g.FillRectangle(sheen, r.X, r.Y, r.Width, r.Height * 0.55f);
            g.Restore(st);
        }
        using var pen = new Pen(Color.FromArgb(highlight ? 120 : 60, 255, 255, 255), highlight ? 1.5f * S : 1f);
        g.DrawPath(pen, path);
    }

    void Button(Graphics g, string id, RectangleF r, string text, bool primary, bool enabled, Action act)
    {
        var hov = enabled && _hover == id;
        var down = hov && _pressed == id;
        using var path = RoundRect(r, r.Height / 2);
        if (primary)
            g.FillPath(B(Color.FromArgb(enabled ? (down ? 200 : hov ? 255 : 232) : 90, 255, 255, 255)), path);
        else
        {
            g.FillPath(B(Color.FromArgb(down ? 60 : hov ? 46 : 26, 255, 255, 255)), path);
            using var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1);
            g.DrawPath(pen, path);
        }
        var col = primary ? Color.FromArgb(enabled ? 255 : 160, 24, 32, 28) : (enabled ? Ink : InkFaint);
        using var f = F(9.75f, primary ? FontStyle.Bold : FontStyle.Regular);
        g.DrawString(text, f, B(col), r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        if (enabled) _hits.Add(new Hit(id, r, act));
    }

    void Segmented(Graphics g, string id, RectangleF r, string[] options, int selected, Action<int> pick, bool small = false)
    {
        var s = S;
        using (var path = RoundRect(r, r.Height / 2))
        {
            g.FillPath(B(Color.FromArgb(70, 0, 0, 0)), path);
            using var pen = new Pen(Color.FromArgb(45, 255, 255, 255), 1);
            g.DrawPath(pen, path);
        }
        var w = r.Width / options.Length;
        using var f = F(small ? 9f : 10f, FontStyle.Bold);
        for (var i = 0; i < options.Length; i++)
        {
            var seg = new RectangleF(r.X + i * w + 3 * s, r.Y + 3 * s, w - 6 * s, r.Height - 6 * s);
            var sid = $"{id}{i}";
            using var sp = RoundRect(seg, seg.Height / 2);
            if (i == selected) g.FillPath(B(Color.FromArgb(240, 255, 255, 255)), sp);
            else if (_hover == sid) g.FillPath(B(Color.FromArgb(34, 255, 255, 255)), sp);
            g.DrawString(options[i], f, B(i == selected ? Color.FromArgb(255, 24, 32, 28) : InkSoft), seg,
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            var idx = i;
            if (i != selected) _hits.Add(new Hit(sid, seg, () => pick(idx)));
        }
    }

    void Stepper(Graphics g, string id, RectangleF r, string value, Action minus, Action plus)
    {
        var s = S;
        using (var path = RoundRect(r, r.Height / 2))
        {
            g.FillPath(B(Color.FromArgb(70, 0, 0, 0)), path);
            using var pen = new Pen(Color.FromArgb(45, 255, 255, 255), 1);
            g.DrawPath(pen, path);
        }
        var d = r.Height - 6 * s;
        var mr = new RectangleF(r.X + 3 * s, r.Y + 3 * s, d, d);
        var pr = new RectangleF(r.Right - 3 * s - d, r.Y + 3 * s, d, d);
        foreach (var (rr, sid, glyph, act) in new[] { (mr, id + "-", "−", minus), (pr, id + "+", "+", plus) })
        {
            g.FillEllipse(B(Color.FromArgb(_hover == sid ? 70 : 34, 255, 255, 255)), rr);
            using var gf = F(11f, FontStyle.Bold);
            g.DrawString(glyph, gf, B(Ink), rr, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            _hits.Add(new Hit(sid, rr, act));
        }
        using var f = F(9.75f, FontStyle.Bold);
        g.DrawString(value, f, B(Ink), r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
    }

    void Toggle(Graphics g, string id, float x, float y, float w, float h, string label, bool on, Action flip)
    {
        var s = S;
        RowLabel(g, label, x, y, h);
        var tr = new RectangleF(x + w - 46 * s, y + (h - 26 * s) / 2, 46 * s, 26 * s);
        var row = new RectangleF(x, y, w, h);
        using (var path = RoundRect(tr, 13 * s))
        {
            g.FillPath(B(on ? Green : Color.FromArgb(_hover == id ? 70 : 50, 255, 255, 255)), path);
            if (!on) { using var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1); g.DrawPath(pen, path); }
        }
        var k = 20 * s;
        var kx = on ? tr.Right - 3 * s - k : tr.X + 3 * s;
        g.FillEllipse(B(Color.FromArgb(40, 0, 0, 0)), kx, tr.Y + 4 * s, k, k);
        g.FillEllipse(B(Color.White), kx, tr.Y + 3 * s, k, k);
        _hits.Add(new Hit(id, row, flip));
    }

    void RowLabel(Graphics g, string text, float x, float y, float h)
    {
        using var f = F(9.75f);
        g.DrawString(text, f, B(Ink), new RectangleF(x, y, 400 * S, h), new StringFormat { LineAlignment = StringAlignment.Center });
    }

    void SetCfg(Action change) { change(); _sw.Cfg.Save(); Invalidate(); }

    // ------------------------------------------------------------------ background

    void RebuildBackground()
    {
        _bgDirty = false;
        var size = ClientSize;
        if (size.Width < 1 || size.Height < 1) return;
        _bg?.Dispose();
        _bg = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);

        if (_glass)
        {
            // The desktop shows through the acrylic; add a light green-grey tint and grain on top,
            // a bit darker towards the bottom so white text stays readable.
            using var g = Graphics.FromImage(_bg);
            g.Clear(Color.Transparent);
            using (var tint = new LinearGradientBrush(new Rectangle(0, 0, size.Width, size.Height),
                       Color.FromArgb(70, 40, 62, 66), Color.FromArgb(125, 18, 26, 22), LinearGradientMode.Vertical))
                g.FillRectangle(tint, 0, 0, size.Width, size.Height);
            using var tb = new TextureBrush(_noise);
            g.FillRectangle(tb, 0, 0, size.Width, size.Height);
            return;
        }

        // Paint a tiny picture and stretch it: stretching a tiny bitmap is a cheap, smooth blur.
        using var small = new Bitmap(36, Math.Max(2, 36 * size.Height / size.Width));
        using (var g = Graphics.FromImage(small))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(28, 40, 36));
            void blob(float cx, float cy, float r, Color col)
            {
                using var p = new GraphicsPath();
                p.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
                using var b = new PathGradientBrush(p) { CenterColor = col, SurroundColors = [Color.FromArgb(0, col)] };
                g.FillPath(b, p);
            }
            var w = small.Width; var h = small.Height;
            blob(w * 0.12f, h * 0.05f, w * 0.75f, Color.FromArgb(220, 96, 140, 170));   // misty blue
            blob(w * 0.95f, h * 0.30f, w * 0.65f, Color.FromArgb(200, 104, 128, 72));   // olive
            blob(w * 0.30f, h * 0.70f, w * 0.70f, Color.FromArgb(170, 58, 84, 60));     // forest
            blob(w * 0.85f, h * 0.95f, w * 0.55f, Color.FromArgb(200, 22, 30, 26));     // shade
        }
        using (var g = Graphics.FromImage(_bg))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, new Rectangle(0, 0, size.Width, size.Height));
            // darken towards the bottom for readability
            using (var shade = new LinearGradientBrush(new Rectangle(0, 0, size.Width, size.Height), Color.FromArgb(40, 0, 0, 0), Color.FromArgb(120, 0, 0, 0), LinearGradientMode.Vertical))
                g.FillRectangle(shade, 0, 0, size.Width, size.Height);
            // film grain
            using var tb = new TextureBrush(_noise);
            g.FillRectangle(tb, 0, 0, size.Width, size.Height);
        }
    }

    static Bitmap MakeNoise()
    {
        var rnd = new Random(7);
        var bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
            {
                var v = rnd.Next(256);
                bmp.SetPixel(x, y, Color.FromArgb(14, v, v, v));
            }
        return bmp;
    }

    // ------------------------------------------------------------------ helpers

    Font F(float pt, FontStyle style = FontStyle.Regular)
    {
        // Semibold where available reads better than Bold on glass.
        if (style == FontStyle.Bold && _semibold != null)
            return new Font(_semibold, pt, FontStyle.Regular);
        return new Font(_family, pt, style);
    }

    static readonly Dictionary<Color, SolidBrush> BrushCache = new();
    static SolidBrush B(Color c)
    {
        if (!BrushCache.TryGetValue(c, out var b)) BrushCache[c] = b = new SolidBrush(c);
        return b;
    }

    static RectangleF Inset(RectangleF r, float d) => new(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d);

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    internal static void OpenInNotepad(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    internal static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
