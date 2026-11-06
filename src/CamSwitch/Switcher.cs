namespace CamSwitch;

/// The switching engine. Everything runs on the UI thread; one operation at a time.
public sealed class Switcher
{
    public Config Cfg { get; }
    public Mode Mode => Cfg.Mode;
    public Cam Active => Cfg.Active;
    public string Status { get; private set; } = "Starting…";
    public bool? FaceSeen { get; private set; }
    public Bitmap? Preview { get; private set; }
    public bool Busy => _gate.CurrentCount == 0;

    /// Set by the window while it's open, so we keep a live picture even in Manual mode.
    public bool PreviewWanted
    {
        get => _previewWanted;
        set { _previewWanted = value; if (!value) SetPreview(null); }
    }

    public event Action? Changed;
    /// (title, text) for a tray notification.
    public event Action<string, string>? Notify;

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly System.Windows.Forms.Timer _timer = new();
    FaceWatcher? _watcher;
    bool _previewWanted;
    DateTime _lastFace = DateTime.UtcNow, _lastAnyFace = DateTime.UtcNow, _nextProbe = DateTime.UtcNow;

    public Switcher(Config cfg)
    {
        Cfg = cfg;
        _timer.Interval = Math.Max(500, cfg.CheckIntervalMs);
        _timer.Tick += async (_, _) => await TickAsync();
    }

    // ------------------------------------------------------------------ public operations

    public async Task StartAsync(Func<bool> confirmSitAtDesk, Action<string, bool> message)
    {
        Log.Write($"Starting CamSwitch {Platform.Version}, mode {Cfg.Mode}");
        await Locked(async () =>
        {
            // Both on first, in case we were killed while one was off.
            await BothOnAsync();
            if (Cfg.Mode != Mode.Off && !Cfg.Identified)
                await IdentifyCoreAsync(confirmSitAtDesk, message);
            if (Cfg.Mode == Mode.Auto) Cfg.Active = Cfg.DefaultCamera;
            await ApplyCoreAsync("start-up", notify: false);
        });
        _timer.Start();
    }

    public Task SetModeAsync(Mode mode) => Locked(async () =>
    {
        if (Cfg.Mode == mode) return;
        Cfg.Mode = mode;
        Cfg.Save();
        Log.Write("Mode: " + mode);
        await ApplyCoreAsync(mode switch { Mode.Off => "switcher off", Mode.Manual => "manual", _ => "automatic" }, notify: false);
    });

    /// Use this camera and stay on it (switches to Manual if Automatic was on).
    public Task UseAsync(Cam cam) => Locked(async () =>
    {
        if (Cfg.Mode != Mode.Manual) { Cfg.Mode = Mode.Manual; Log.Write("Mode: Manual"); }
        Cfg.Active = cam;
        await ApplyCoreAsync("picked by you");
    });

    public Task ToggleAsync() => UseAsync(Cfg.Mode == Mode.Off ? Cfg.DefaultCamera : Config.OtherOf(Cfg.Active));

    public void SetDefault(Cam cam)
    {
        Cfg.DefaultCamera = cam;
        Cfg.Save();
        Changed?.Invoke();
    }

    public Task IdentifyAsync(Func<bool> confirmSitAtDesk, Action<string, bool> message) => Locked(async () =>
    {
        await IdentifyCoreAsync(confirmSitAtDesk, message);
        await ApplyCoreAsync("identified", notify: false);
    });

    /// Turns both cameras back on. Used on exit and before updating.
    public async Task ShutdownAsync()
    {
        _timer.Stop();
        await _gate.WaitAsync();
        try
        {
            await CloseWatcherAsync();
            await BothOnAsync();
            Log.Write("Stopped, both cameras on");
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ the loop

    async Task TickAsync()
    {
        if (Busy || Cfg.Mode == Mode.Off) return;
        await _gate.WaitAsync();
        try
        {
            var watch = Cfg.Mode == Mode.Auto || PreviewWanted;
            if (!watch) { await CloseWatcherAsync(); return; }

            if (_watcher == null)
            {
                _watcher = await FaceWatcher.OpenAsync(Cfg.IdOf(Cfg.Active), 3000);
                if (_watcher == null) { SetStatus($"can't open the {Cfg.NameOf(Cfg.Active).ToLower()} camera"); return; }
            }

            var (face, preview) = await _watcher.CheckAsync(PreviewWanted);
            if (preview != null) SetPreview(preview);
            if (face == null) return;
            FaceSeen = face;

            if (Cfg.Mode != Mode.Auto) { SetStatus(face.Value ? "you're in view" : "nobody in view"); return; }

            var now = DateTime.UtcNow;
            if (face.Value)
            {
                _lastFace = _lastAnyFace = now;
                SetStatus("you're in view");
                return;
            }

            var away = (now - _lastFace).TotalSeconds;
            SetStatus(away < 3 ? "you're in view" : $"no face for {away:0}s");

            if (Cfg.ReturnToDefaultMinutes > 0 && Cfg.Active != Cfg.DefaultCamera
                && (now - _lastAnyFace).TotalMinutes >= Cfg.ReturnToDefaultMinutes)
            {
                Cfg.Active = Cfg.DefaultCamera;
                await ApplyCoreAsync("nobody around, back to default");
                return;
            }

            if (away < Cfg.AbsenceSeconds || now < _nextProbe) return;

            // Nobody at the active camera: turn the other one on and have a look.
            var other = Config.OtherOf(Cfg.Active);
            Log.Write($"No face at {Cfg.Active} for {away:0}s, checking {other}");
            SetStatus($"looking through the {Cfg.NameOf(other).ToLower()} camera…");
            await Task.Run(() => Devices.SetEnabled(Cfg.IdOf(other), true));
            var found = false;
            await using (var probe = await FaceWatcher.OpenAsync(Cfg.IdOf(other)))
                if (probe != null) found = await probe.LookForFaceAsync(Cfg.ProbeSeconds);

            if (found)
            {
                _lastAnyFace = DateTime.UtcNow;
                Cfg.Active = other;
                await ApplyCoreAsync("saw you there");
            }
            else
            {
                Log.Write($"Nobody at {other} either");
                await Task.Run(() => Devices.SetEnabled(Cfg.IdOf(other), false));
                _nextProbe = DateTime.UtcNow.AddSeconds(Cfg.ProbeCooldownSeconds);
                SetStatus($"no face for {(DateTime.UtcNow - _lastFace).TotalSeconds:0}s");
            }
        }
        catch (Exception ex) { Log.Write("Tick error: " + ex); }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ internals

    async Task Locked(Func<Task> work)
    {
        await _gate.WaitAsync();
        try { await work(); }
        catch (Exception ex) { Log.Write("Error: " + ex); SetStatus("error, see log"); }
        finally { _gate.Release(); }
    }

    /// Puts the cameras in the state the current mode and Active camera call for.
    async Task ApplyCoreAsync(string reason, bool notify = true)
    {
        await CloseWatcherAsync();
        FaceSeen = null;
        SetPreview(null);

        if (Cfg.Mode == Mode.Off)
        {
            await BothOnAsync();
            Cfg.Save();
            SetStatus("off: both cameras on, Windows decides");
            if (notify) Notify?.Invoke("CamSwitch is off", "Both cameras are on.");
            return;
        }

        var on = Cfg.IdOf(Cfg.Active);
        var off = Cfg.IdOf(Config.OtherOf(Cfg.Active));
        await Task.Run(() => { Devices.SetEnabled(on, true); Devices.SetEnabled(off, false); });
        Cfg.Save();
        _lastFace = DateTime.UtcNow;
        _nextProbe = DateTime.UtcNow;
        Log.Write($"Active camera: {Cfg.Active} ({reason})");
        if (Cfg.Mode == Mode.Auto || PreviewWanted)
            _watcher = await FaceWatcher.OpenAsync(on);
        SetStatus(reason);
        if (notify && Cfg.ShowNotifications)
            Notify?.Invoke("CamSwitch", $"Now using the {Cfg.NameOf(Cfg.Active).ToLower()} camera ({reason}).");
    }

    async Task IdentifyCoreAsync(Func<bool> confirmSitAtDesk, Action<string, bool> message)
    {
        await CloseWatcherAsync();
        await BothOnAsync();
        SetStatus("identifying cameras…");
        if (!confirmSitAtDesk()) return;

        async Task<bool> sees(string id)
        {
            await using var w = await FaceWatcher.OpenAsync(id);
            return w != null && await w.LookForFaceAsync(5);
        }
        var first = await sees(Cfg.DeskInstanceId);
        var second = await sees(Cfg.RigInstanceId);
        Log.Write($"Identify: first={first} second={second}");

        if (first == second)
        {
            message(first
                ? "Both cameras saw a face, so I couldn't tell which one is at your desk. Try again when only the desk camera can see you."
                : "Neither camera saw a face. Check both cameras are plugged in, then use Identify cameras again.", false);
            return;
        }
        if (second) (Cfg.DeskInstanceId, Cfg.RigInstanceId) = (Cfg.RigInstanceId, Cfg.DeskInstanceId);
        Cfg.Identified = true;
        Cfg.Active = Cam.Desk;
        Cfg.Save();
        message("Got it, the desk camera is set. CamSwitch will now keep only the camera you're in front of turned on.", true);
    }

    async Task BothOnAsync() =>
        await Task.Run(() => { Devices.SetEnabled(Cfg.DeskInstanceId, true); Devices.SetEnabled(Cfg.RigInstanceId, true); });

    async Task CloseWatcherAsync()
    {
        if (_watcher == null) return;
        var w = _watcher;
        _watcher = null;
        await w.DisposeAsync();
    }

    void SetStatus(string s) { Status = s; Changed?.Invoke(); }

    void SetPreview(Bitmap? b)
    {
        var old = Preview;
        Preview = b;
        Changed?.Invoke();
        old?.Dispose();
    }
}
