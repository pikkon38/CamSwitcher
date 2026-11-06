using System.Diagnostics;

namespace CamSwitch;

/// Turns a whole physical camera on or off in Device Manager.
/// A Brio exposes more than one camera interface (colour, and the infrared one Windows Hello uses).
/// They share the same instance "stem", so we toggle every Camera/Image-class interface with that stem.
/// The microphone is a different class and is left alone.
public static class Devices
{
    /// "USB\VID_046D&PID_085E&MI_00\9&1D2A6CC1&0&0000" -> ("USB\VID_046D&PID_085E", "9&1D2A6CC1&0")
    static (string hw, string stem) Split(string instanceId)
    {
        var slash = instanceId.LastIndexOf('\\');
        var hwPart = instanceId[..slash];
        var mi = hwPart.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        var hw = mi >= 0 ? hwPart[..mi] : hwPart;
        var inst = instanceId[(slash + 1)..];
        var amp = inst.LastIndexOf('&');
        var stem = amp > 0 ? inst[..amp] : inst;
        return (hw, stem);
    }

    static string Filter(string instanceId)
    {
        var (hw, stem) = Split(instanceId);
        hw = hw.Replace("'", "''");
        stem = stem.Replace("'", "''");
        // Escape -like wildcards that could appear in IDs.
        string esc(string s) => s.Replace("[", "`[").Replace("]", "`]");
        return $"Get-PnpDevice | Where-Object {{ ($_.InstanceId -like '{esc(hw)}*\\{esc(stem)}&*' -or $_.InstanceId -eq '{instanceId.Replace("'", "''")}') -and ($_.Class -in 'Camera','Image') }}";
    }

    public static bool SetEnabled(string instanceId, bool enabled)
    {
        var verb = enabled ? "Enable-PnpDevice" : "Disable-PnpDevice";
        var script = $"$d = @({Filter(instanceId)}); if ($d.Count -eq 0) {{ Write-Output 'NONE'; exit 2 }}; $d | {verb} -Confirm:$false -ErrorAction Stop; $d | ForEach-Object {{ Write-Output $_.InstanceId }}";
        var (code, output) = RunPowerShell(script);
        Log.Write($"{verb} {instanceId}: exit {code} {output.Trim()}");
        return code == 0;
    }

    public static string Describe(string instanceId)
    {
        var (_, output) = RunPowerShell($"{Filter(instanceId)} | ForEach-Object {{ \"$($_.Status)  $($_.Class)  $($_.FriendlyName)  $($_.InstanceId)\" }}");
        return output.Trim();
    }

    public static (int code, string output) RunPowerShell(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
