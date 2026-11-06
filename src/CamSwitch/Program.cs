using System.Diagnostics;

namespace CamSwitch;

static class Program
{
    /// Command line:
    ///   (none)              open the window, starting CamSwitch if it isn't running
    ///   --background        start in the tray without opening the window (used at sign-in)
    ///   --quit              ask the running copy to exit (it turns both cameras back on)
    ///   --restore-cameras   turn both cameras on and exit (used by the uninstaller)
    ///   --install-task / --remove-task   manage the start-at-sign-in task (installer)
    [STAThread]
    static int Main(string[] args)
    {
        var arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        switch (arg)
        {
            case "--quit":
                return QuitRunning() ? 0 : 1;
            case "--restore-cameras":
            {
                var cfg = Config.Load();
                Devices.SetEnabled(cfg.DeskInstanceId, true);
                Devices.SetEnabled(cfg.RigInstanceId, true);
                return 0;
            }
            case "--install-task":
                return Platform.InstallTask() ? 0 : 1;
            case "--remove-task":
                return Platform.RemoveTask() ? 0 : 1;
            case "--screenshot":
                Screenshot(args.Length > 1 ? args[1] : "ui-preview.png");
                return 0;
        }

        var background = arg == "--background";

        // Already running? Just bring its window up.
        if (Platform.FindRunning() != IntPtr.Zero)
        {
            if (!background) Platform.SignalRunning(Platform.WM_SHOW);
            return 0;
        }

        // Turning cameras on and off needs admin. Start the elevated copy through the
        // sign-in task (no prompt) if it exists, otherwise ask with UAC.
        if (!Platform.IsElevated)
        {
            if (Platform.TaskExists() && Platform.StartTask())
            {
                if (!background) WaitThenShow();
                return 0;
            }
            return Platform.RelaunchElevated(background ? "--background" : "") ? 0 : 1;
        }

        using var mutex = new Mutex(true, @"Local\CamSwitch.SingleInstance", out var first);
        if (!first)
        {
            if (!background) WaitThenShow();
            return 0;
        }

        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);
        Application.Run(new TrayApp(showWindow: !background));
        return 0;
    }

    /// Renders the window with made-up data, so the look can be checked without cameras.
    static void Screenshot(string path)
    {
        var cfg = new Config { DefaultCamera = Cam.Desk };
        var sw = new Switcher(cfg);
        using var demo = new Bitmap(640, 360);
        using (var g = Graphics.FromImage(demo))
        {
            using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 640, 360), Color.FromArgb(170, 150, 120), Color.FromArgb(60, 55, 50), 60f);
            g.FillRectangle(bg, 0, 0, 640, 360);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(new SolidBrush(Color.FromArgb(200, 70, 80)), 200, 200, 240, 220);
            g.FillEllipse(new SolidBrush(Color.FromArgb(225, 185, 160)), 270, 90, 100, 120);
            using var pen = new Pen(Color.FromArgb(34, 197, 94), 3);
            g.DrawRectangle(pen, 265, 95, 110, 110);
        }
        sw.Demo("you're in view", true, demo, Mode.Auto, Cam.Desk);
        using var form = new MainForm(sw);
        form.ShowUpdate(new Updater.Release("1.2.0", "", "", ""));
        using var bmp = form.Snapshot();
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    static void WaitThenShow()
    {
        for (var i = 0; i < 40 && Platform.FindRunning() == IntPtr.Zero; i++) Thread.Sleep(250);
        Platform.SignalRunning(Platform.WM_SHOW);
    }

    static bool QuitRunning()
    {
        if (!Platform.SignalRunning(Platform.WM_QUIT_APP)) return true;
        // Give it time to turn the cameras back on.
        for (var i = 0; i < 60; i++)
        {
            if (Platform.FindRunning() == IntPtr.Zero) return true;
            Thread.Sleep(250);
        }
        foreach (var p in Process.GetProcessesByName("CamSwitch"))
            if (p.Id != Environment.ProcessId) try { p.Kill(); } catch { }
        return false;
    }
}
