using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.FaceAnalysis;
using System.Runtime.InteropServices.WindowsRuntime;

namespace CamSwitch;

/// Watches one camera for a face. Opens the camera in shared read-only mode,
/// so Star Citizen, Discord and so on can still use it at the same time.
public sealed class FaceWatcher : IAsyncDisposable
{
    readonly MediaCapture _capture;
    readonly MediaFrameReader _reader;
    readonly FaceDetector _detector;
    public string InstanceId { get; }

    FaceWatcher(string instanceId, MediaCapture capture, MediaFrameReader reader, FaceDetector detector)
    {
        InstanceId = instanceId; _capture = capture; _reader = reader; _detector = detector;
    }

    /// Finds the Windows Media device ID for a PnP instance ID, or null if it isn't present and enabled.
    public static async Task<string?> FindDeviceIdAsync(string instanceId)
    {
        var needle = instanceId.Replace('\\', '#').ToUpperInvariant();
        var all = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return all.FirstOrDefault(d => d.Id.ToUpperInvariant().Contains(needle))?.Id;
    }

    /// Waits up to timeoutMs for the camera to show up (it takes a moment after being enabled).
    public static async Task<FaceWatcher?> OpenAsync(string instanceId, int timeoutMs = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string? deviceId;
        while ((deviceId = await FindDeviceIdAsync(instanceId)) == null)
        {
            if (DateTime.UtcNow > until) { Log.Write($"Camera not found: {instanceId}"); return null; }
            await Task.Delay(500);
        }

        if (!FaceDetector.IsSupported) { Log.Write("FaceDetector not supported on this PC"); return null; }

        foreach (var mode in new[] { MediaCaptureSharingMode.SharedReadOnly, MediaCaptureSharingMode.ExclusiveControl })
        {
            var mc = new MediaCapture();
            try
            {
                await mc.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = deviceId,
                    SharingMode = mode,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                });
                var source = mc.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                             ?? mc.FrameSources.Values.First();
                var reader = await mc.CreateFrameReaderAsync(source);
                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                var status = await reader.StartAsync();
                if (status != MediaFrameReaderStartStatus.Success)
                    throw new InvalidOperationException("Frame reader: " + status);
                var detector = await FaceDetector.CreateAsync();
                Log.Write($"Opened {instanceId} ({mode})");
                return new FaceWatcher(instanceId, mc, reader, detector);
            }
            catch (Exception ex)
            {
                Log.Write($"Open {instanceId} in {mode} failed: {ex.Message}");
                mc.Dispose();
            }
        }
        return null;
    }

    /// Face = true/false, or null when no frame was available. Preview is a small snapshot when asked for.
    public async Task<(bool? Face, Bitmap? Preview)> CheckAsync(bool wantPreview = false)
    {
        using var frame = _reader.TryAcquireLatestFrame();
        var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bmp == null) return (null, null);

        var fmt = FaceDetector.GetSupportedBitmapPixelFormats().FirstOrDefault(BitmapPixelFormat.Gray8);
        using var gray = bmp.BitmapPixelFormat == fmt ? SoftwareBitmap.Copy(bmp) : SoftwareBitmap.Convert(bmp, fmt);

        // Ignore tiny faces (a poster, someone far behind you).
        try
        {
            var minSide = Math.Max(48u, (uint)(Math.Min(gray.PixelWidth, gray.PixelHeight) / 12));
            _detector.MinDetectableFaceSize = new BitmapSize { Width = minSide, Height = minSide };
        }
        catch { }

        var faces = await _detector.DetectFacesAsync(gray);
        Bitmap? preview = null;
        if (wantPreview)
        {
            try { preview = ToPreview(bmp, faces.Select(f => f.FaceBox).ToList()); }
            catch (Exception ex) { Log.Write("Preview failed: " + ex.Message); }
        }
        return (faces.Count > 0, preview);
    }

    public async Task<bool?> SeesFaceAsync() => (await CheckAsync()).Face;

    static Bitmap ToPreview(SoftwareBitmap src, List<BitmapBounds> faces)
    {
        using var bgra = SoftwareBitmap.Convert(src, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var bytes = new byte[w * h * 4];
        bgra.CopyToBuffer(bytes.AsBuffer());
        using var full = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var data = full.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, full.PixelFormat);
        try
        {
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(bytes, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        }
        finally { full.UnlockBits(data); }

        // Shrink to something light for the window.
        const int maxW = 480;
        var scale = Math.Min(1.0, (double)maxW / w);
        var small = new Bitmap(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
        using var g = Graphics.FromImage(small);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        g.DrawImage(full, 0, 0, small.Width, small.Height);
        using var pen = new Pen(Color.FromArgb(34, 197, 94), 3);
        foreach (var f in faces)
            g.DrawRectangle(pen, (float)(f.X * scale), (float)(f.Y * scale), (float)(f.Width * scale), (float)(f.Height * scale));
        return small;
    }

    /// Samples for up to `seconds`; returns true as soon as a face is seen.
    public async Task<bool> LookForFaceAsync(int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await SeesFaceAsync() == true) return true;
            await Task.Delay(300);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _reader.StopAsync(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { _capture.Dispose(); } catch { }
    }
}
