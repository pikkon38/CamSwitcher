using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CamSwitch;

public static class Platform
{
    public const string TaskName = "CamSwitch";
    public const string HostWindowTitle = "CamSwitch.Host.7f3c";

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static string Version
    {
        get
        {
            var v = typeof(Platform).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    // ---------- Start-with-Windows scheduled task (runs elevated, no UAC prompt) ----------

    public static bool TaskExists() => Run("schtasks.exe", ["/Query", "/TN", TaskName]).code == 0;

    /// Creates or updates the logon task. Needs admin.
    public static bool InstallTask()
    {
        var user = WindowsIdentity.GetCurrent().Name.Replace("'", "''");
        var exe = ExePath.Replace("'", "''");
        var ps = $@"
$a = New-ScheduledTaskAction -Execute '{exe}' -Argument '--background'
$t = New-ScheduledTaskTrigger -AtLogOn -User '{user}'
$p = New-ScheduledTaskPrincipal -UserId '{user}' -LogonType Interactive -RunLevel Highest
$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName '{TaskName}' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null";
        var (code, output) = Devices.RunPowerShell(ps);
        Log.Write($"Install task: exit {code} {output.Trim()}");
        return code == 0;
    }

    public static bool RemoveTask() => Run("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"]).code == 0;

    public static bool StartTask() => Run("schtasks.exe", ["/Run", "/TN", TaskName]).code == 0;

    /// Makes the task match the setting. Needs admin.
    public static void SyncStartWithWindows(bool enabled)
    {
        try
        {
            if (enabled) InstallTask();
            else if (TaskExists()) RemoveTask();
        }
        catch (Exception ex) { Log.Write("Startup task error: " + ex.Message); }
    }

    public static (int code, string output) Run(string file, string[] args, int timeoutMs = 30000)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, o.Result + e.Result);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// Restarts this exe elevated (shows a UAC prompt).
    public static bool RelaunchElevated(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExePath, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; }
    }

    // ---------- Talking to the running copy ----------
    // The running copy is elevated; a normal-rights copy (Start menu shortcut, installer)
    // can still reach it because the host window allows these messages through.

    public static readonly int WM_SHOW = RegisterWindowMessage("CamSwitch.Show");
    public static readonly int WM_QUIT_APP = RegisterWindowMessage("CamSwitch.Quit");

    public static IntPtr FindRunning() => FindWindow(null, HostWindowTitle);

    public static bool SignalRunning(int msg)
    {
        var h = FindRunning();
        return h != IntPtr.Zero && PostMessage(h, msg, IntPtr.Zero, IntPtr.Zero);
    }

    public static void AllowMessagesFromLowerRights(IntPtr hwnd)
    {
        ChangeWindowMessageFilterEx(hwnd, (uint)WM_SHOW, 1 /*MSGFLT_ALLOW*/, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, (uint)WM_QUIT_APP, 1, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string s);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
}
