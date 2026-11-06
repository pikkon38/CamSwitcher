namespace CamSwitch;

public static class Icons
{
    static Icon Load(string name, int size = 0)
    {
        using var s = typeof(Icons).Assembly.GetManifestResourceStream(name + ".ico")
                      ?? throw new InvalidOperationException("Missing icon " + name);
        return size > 0 ? new Icon(s, size, size) : new Icon(s);
    }

    public static readonly Icon App = Load("app");
    static readonly Dictionary<string, Icon> Tray = new();

    /// Small icon sized for the notification area at the current DPI.
    public static Icon ForTray(string name)
    {
        if (!Tray.TryGetValue(name, out var i))
            Tray[name] = i = Load(name, SystemInformation.SmallIconSize.Width);
        return i;
    }

    public static Bitmap AppBitmap(int size)
    {
        using var i = Load("app", 256);
        return new Bitmap(i.ToBitmap(), size, size);
    }
}
