using System.Text.Json;
using System.Text.Json.Serialization;

namespace CamSwitch;

public enum Mode
{
    /// Watch for your face and use the camera you're in front of.
    Auto,
    /// Use the camera you picked and stay on it.
    Manual,
    /// Switcher off: both cameras on, apps and Windows choose as normal.
    Off,
}

public enum Cam { Desk, Rig }

public sealed class Config
{
    // PnP instance IDs of each camera's video interface (Get-PnpDevice -Class Camera).
    public string DeskInstanceId { get; set; } = @"USB\VID_046D&PID_085E&MI_00\9&1D2A6CC1&0&0000";
    public string RigInstanceId { get; set; } = @"USB\VID_046D&PID_085E&MI_00\C&11452C19&0&0000";
    public string DeskName { get; set; } = "Desk";
    public string RigName { get; set; } = "Racing rig";

    /// True once "Identify cameras" has worked out which one is the desk camera.
    public bool Identified { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Mode Mode { get; set; } = Mode.Auto;

    /// Camera in use right now (remembered across restarts in Manual mode).
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Cam Active { get; set; } = Cam.Desk;

    /// Camera used at start-up, and the one Automatic mode returns to when nobody is around.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Cam DefaultCamera { get; set; } = Cam.Desk;

    public int CheckIntervalMs { get; set; } = 1500;
    /// How long the active camera must see nobody before looking through the other one.
    public int AbsenceSeconds { get; set; } = 8;
    /// After looking and finding nobody, wait this long before looking again.
    public int ProbeCooldownSeconds { get; set; } = 20;
    /// How long to look through the other camera for a face.
    public int ProbeSeconds { get; set; } = 4;
    /// In Automatic mode, go back to the default camera after nobody has been seen for this long (0 = never).
    public int ReturnToDefaultMinutes { get; set; } = 2;

    public bool ShowNotifications { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;

    // Old v0 setting, read once and folded into Mode.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Automatic { get; set; }

    public string NameOf(Cam c) => c == Cam.Desk ? DeskName : RigName;
    public string IdOf(Cam c) => c == Cam.Desk ? DeskInstanceId : RigInstanceId;
    public static Cam OtherOf(Cam c) => c == Cam.Desk ? Cam.Rig : Cam.Desk;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamSwitch");
    public static string FilePath => Path.Combine(Dir, "config.json");
    public static string LogPath => Path.Combine(Dir, "camswitch.log");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static Config Load()
    {
        Config c;
        try
        {
            c = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Opts) ?? new Config()
                : new Config();
        }
        catch (Exception ex)
        {
            Log.Write("Config load failed, using defaults: " + ex.Message);
            c = new Config();
        }
        if (c.Automatic.HasValue)
        {
            c.Mode = c.Automatic.Value ? Mode.Auto : Mode.Manual;
            c.Automatic = null;
        }
        c.Save();
        return c;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch (Exception ex) { Log.Write("Config save failed: " + ex.Message); }
    }
}

public static class Log
{
    static readonly object Gate = new();
    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Config.Dir);
                var fi = new FileInfo(Config.LogPath);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(Config.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
