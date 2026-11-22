using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;

namespace WinIslands.UI;

/// <summary>
/// Renders the desktop behind a screen region as a uniform frosted-glass texture:
/// the real backdrop is blurred, cropped to a rounded rectangle, and cached until
/// geometry, wallpaper, theme, or desktop content changes.
/// </summary>
internal static class GlassBackdrop
{
    // Uniform frosted-glass tuning.
    internal const double BlurRadius = 18.0;
    internal const double TintStrength = 0.55;

    private const int MaxTextureWidth = 1400;
    private const int MaxTextureHeight = 1000;
    private const int CacheLimit = 6;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, WallpaperAsset> Wallpapers = new(StringComparer.OrdinalIgnoreCase);
    private static long _revision = 1;
    private static long _lastBuildTicks;
    private static BitmapSource? _lastBuilt;
    private static bool _eventsHooked;
    private static BitmapSource? _snapshot;
    private static Rect _snapshotRect;
    private static long _snapshotStamp;
    private const double SnapshotInflate = 140;

    internal static long Revision => System.Threading.Volatile.Read(ref _revision);

    static GlassBackdrop()
    {
        HookEvents();
    }

    private static void HookEvents()
    {
        if (_eventsHooked) return;
        _eventsHooked = true;
        try
        {
            SystemEvents.UserPreferenceChanged += (_, _) => BumpRevision();
            SystemEvents.DisplaySettingsChanged += (_, _) => BumpRevision();
        }
        catch
        {
            // Event hookup can fail in restricted sessions; wallpapers are still sampled
            // on demand, only automatic refreshes are lost.
        }
    }

    internal static void BumpRevision()
    {
        System.Threading.Interlocked.Increment(ref _revision);
        lock (Sync) Cache.Clear();
    }

    /// <summary>
    /// Renders the wallpaper behind <paramref name="screenRect"/> (physical pixels) as a
    /// frosted glass texture. Returns null when no wallpaper/background can be read.
    /// </summary>
    internal static BitmapSource? Render(
        Rect screenRect,
        double dpiScale,
        double cornerRadius,
        double intensity,
        out string key)
    {
        key = string.Empty;
        try
        {
            if (screenRect.Width < 2 || screenRect.Height < 2)
                return null;

            cornerRadius = Math.Clamp(
                cornerRadius, 0, Math.Min(screenRect.Width, screenRect.Height) * 0.5);
            intensity = Math.Clamp(intensity, 0, 2);

            var tex = Math.Clamp(0.5 * Math.Max(0.75, dpiScale), 0.25, 1.0);
            if (screenRect.Width * tex > MaxTextureWidth)
                tex = MaxTextureWidth / screenRect.Width;
            if (screenRect.Height * tex > MaxTextureHeight)
                tex = Math.Min(tex, MaxTextureHeight / screenRect.Height);
            tex = Math.Max(tex, 0.18);

            var roundedX = (int)Math.Round(screenRect.X / 6.0) * 6;
            var roundedY = (int)Math.Round(screenRect.Y / 6.0) * 6;
            var roundedW = Math.Max(2, (int)Math.Round(screenRect.Width / 3.0) * 3);
            var roundedH = Math.Max(2, (int)Math.Round(screenRect.Height / 3.0) * 3);

            var monitor = MonitorAt((int)Math.Round(screenRect.X + (screenRect.Width / 2)),
                                    (int)Math.Round(screenRect.Y + (screenRect.Height / 2)));
            var wallpaper = ResolveWallpaper(monitor);
            var revision = Revision;

            BitmapSource? snapshot = null;
            var snapshotRect = Rect.Empty;
            long snapshotStamp = 0;
            lock (Sync)
            {
                if (_snapshot is not null && _snapshotRect.Contains(screenRect))
                {
                    snapshot = _snapshot;
                    snapshotRect = _snapshotRect;
                    snapshotStamp = _snapshotStamp;
                }
            }

            key = string.Join("|",
                roundedX, roundedY, roundedW, roundedH,
                tex.ToString("F3"), revision,
                cornerRadius.ToString("F1"), intensity.ToString("F2"),
                wallpaper.Path ?? "<none>", wallpaper.Stamp,
                (int)wallpaper.Position,
                monitor.Left, monitor.Top, monitor.Width, monitor.Height,
                wallpaper.Background.R, wallpaper.Background.G, wallpaper.Background.B,
                snapshot is null ? "wp" : "ss", snapshotStamp,
                snapshotRect.X, snapshotRect.Y, snapshotRect.Width, snapshotRect.Height);

            lock (Sync)
            {
                if (Cache.TryGetValue(key, out var cached))
                    return cached;

                // During the expand/collapse animation the geometry changes every frame.
                // Reuse the previous texture for ~55ms instead of rebuilding on each frame;
                // the layer schedules one final refresh once the layout settles.
                if (_lastBuilt is not null)
                {
                    var elapsed = (Stopwatch.GetTimestamp() - _lastBuildTicks) / (double)Stopwatch.Frequency;
                    if (elapsed < 0.055)
                        return _lastBuilt;
                }
            }

            var bitmap = RenderCore(
                screenRect, tex, cornerRadius, monitor, wallpaper, snapshot, snapshotRect);
            if (bitmap is null) return null;

            lock (Sync)
            {
                if (Cache.Count >= CacheLimit) Cache.Clear();
                Cache[key] = bitmap;
                _lastBuilt = bitmap;
                _lastBuildTicks = Stopwatch.GetTimestamp();
            }
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Captures the real screen behind a window (physical pixels) before it becomes
    /// visible. This is what makes the glass show the actual desktop/windows behind it
    /// instead of only a blurred wallpaper.
    /// </summary>
    internal static void CaptureForWindow(IntPtr hwnd, Rect? occluder, double inflate = SnapshotInflate)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var native))
                return;

            var windowRect = new Rect(
                native.Left, native.Top,
                Math.Max(1, native.Right - native.Left),
                Math.Max(1, native.Bottom - native.Top));
            if (windowRect.Width < 20 || windowRect.Height < 20)
                return;

            var rect = windowRect;
            rect.Inflate(inflate, inflate);
            CaptureRect(rect, occluder);
        }
        catch
        {
            // Screen capture can be unavailable (locked session / protected content);
            // the layer then falls back to the wallpaper path.
        }
    }

    /// <summary>
    /// Captures a desktop region (physical pixels) right before a popup opens, so the
    /// popup's glass can blur the actual content behind it.
    /// </summary>
    internal static void CaptureRegion(Rect rect, Rect? occluder = null) => CaptureRect(rect, occluder);

    /// <summary>
    /// Re-samples the desktop behind an already visible window (after the user drags it).
    /// The window area itself is patched out of the snapshot so the glass never samples
    /// its own composited output.
    /// </summary>
    internal static void RecaptureBehindWindow(IntPtr hwnd, double inflate = SnapshotInflate)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var native))
                return;

            var windowRect = new Rect(
                native.Left, native.Top,
                Math.Max(1, native.Right - native.Left),
                Math.Max(1, native.Bottom - native.Top));
            if (windowRect.Width < 20 || windowRect.Height < 20)
                return;

            var rect = windowRect;
            rect.Inflate(inflate, inflate);
            CaptureRect(rect, windowRect);
        }
        catch
        {
        }
    }

    private static void CaptureRect(Rect rect, Rect? occluder)
    {
        try
        {
            var bounds = VirtualScreen();
            rect.Intersect(bounds);
            if (rect.Width < 20 || rect.Height < 20)
                return;

            var x = (int)Math.Floor(rect.X);
            var y = (int)Math.Floor(rect.Y);
            var w = Math.Max(2, (int)Math.Ceiling(rect.Width));
            var h = Math.Max(2, (int)Math.Ceiling(rect.Height));

            using var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0,
                    new System.Drawing.Size(w, h),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            if (occluder is Rect hole && hole.Width > 2 && hole.Height > 2)
                PatchOccluder(bitmap, x, y, hole);

            var data = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            byte[] pixels;
            int stride;
            try
            {
                stride = data.Stride;
                pixels = new byte[stride * h];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            source.Freeze();
            lock (Sync)
            {
                _snapshot = source;
                _snapshotRect = new Rect(x, y, w, h);
                _snapshotStamp = Stopwatch.GetTimestamp();
                _lastBuilt = null;
                _lastBuildTicks = 0;
                Cache.Clear();
            }
        }
        catch
        {
            // Locked session / protected content: keep the previous snapshot or the
            // wallpaper fallback rather than crashing.
        }
    }
    /// <summary>Fills the compact capsule hole with the pixels just below it, feathered
    /// toward the real pixels on both sides so the blurred glass shows no black patch.</summary>
    private static void PatchOccluder(System.Drawing.Bitmap bitmap, int originX, int originY, Rect hole)
    {
        var w = bitmap.Width;
        var h = bitmap.Height;
        var left = (int)Math.Round(hole.Left - originX);
        var top = (int)Math.Round(hole.Top - originY);
        var right = (int)Math.Round(hole.Right - originX);
        var bottom = (int)Math.Round(hole.Bottom - originY);
        if (left < 0) left = 0;
        if (top < 0) top = 0;
        if (right > w) right = w;
        if (bottom > h) bottom = h;
        if (right - left < 2 || bottom - top < 2) return;

        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var pixels = new byte[stride * h];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            var holeHeight = bottom - top;
            var copyOffset = holeHeight + 10;
            const int feather = 26;
            for (var y = top; y < bottom; y++)
            {
                var srcY = Math.Min(y + copyOffset, h - 1);
                var row = y * stride;
                var srcRow = srcY * stride;
                for (var x = left; x < right; x++)
                {
                    var di = row + (x * 4);
                    var si = srcRow + (x * 4);
                    var dist = Math.Min(x - left, right - 1 - x);
                    var edgeX = (x - left) < (right - 1 - x) ? Math.Max(0, left - 1) : Math.Min(w - 1, right);
                    var ei = row + (edgeX * 4);
                    var weight = dist >= feather ? 0.0 : 1.0 - (dist / (double)feather);
                    for (var c = 0; c < 3; c++)
                    {
                        pixels[di + c] = (byte)Math.Clamp(
                            (int)Math.Round((pixels[si + c] * (1.0 - weight)) + (pixels[ei + c] * weight)),
                            0, 255);
                    }
                    pixels[di + 3] = 255;
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static BitmapSource? RenderCore(
        Rect screenRect,
        double tex,
        double cornerRadius,
        Rect monitor,
        WallpaperInfo wallpaper,
        BitmapSource? snapshot,
        Rect snapshotRect)
    {
        var tw = Math.Max(2, (int)Math.Round(screenRect.Width * tex));
        var th = Math.Max(2, (int)Math.Round(screenRect.Height * tex));
        var padCss = (BlurRadius * 3.0) + 12.0;
        var pad = (int)Math.Ceiling(padCss * tex) + 6;
        var fullW = tw + (pad * 2);
        var fullH = th + (pad * 2);

        var regionLeft = screenRect.X - (pad / tex);
        var regionTop = screenRect.Y - (pad / tex);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new SolidColorBrush(wallpaper.Background);
            bg.Freeze();
            dc.DrawRectangle(bg, null, new Rect(0, 0, fullW, fullH));

            var image = wallpaper.Image;
            if (image is not null)
            {
                var sw = image.PixelWidth;
                var sh = image.PixelHeight;
                if (sw > 0 && sh > 0)
                {
                    if (wallpaper.Position == WallpaperPosition.Tile)
                    {
                        var brush = new ImageBrush(image)
                        {
                            TileMode = TileMode.Tile,
                            Stretch = Stretch.Fill,
                            ViewportUnits = BrushMappingMode.Absolute,
                            Viewport = new Rect(
                                (monitor.Left - regionLeft) * tex,
                                (monitor.Top - regionTop) * tex,
                                Math.Max(1, sw * tex),
                                Math.Max(1, sh * tex)),
                        };
                        brush.Freeze();
                        dc.DrawRectangle(brush, null, new Rect(0, 0, fullW, fullH));
                    }
                    else
                    {
                        var frame = wallpaper.Position == WallpaperPosition.Span
                            ? VirtualScreen()
                            : monitor;

                        double scaleX = 1, scaleY = 1;
                        switch (wallpaper.Position)
                        {
                            case WallpaperPosition.Fill:
                            case WallpaperPosition.Span:
                            {
                                var s = Math.Max(frame.Width / sw, frame.Height / sh);
                                scaleX = scaleY = s;
                                break;
                            }
                            case WallpaperPosition.Fit:
                            {
                                var s = Math.Min(frame.Width / sw, frame.Height / sh);
                                scaleX = scaleY = s;
                                break;
                            }
                            case WallpaperPosition.Stretch:
                                scaleX = frame.Width / sw;
                                scaleY = frame.Height / sh;
                                break;
                            case WallpaperPosition.Center:
                            default:
                                break;
                        }

                        var dw = sw * scaleX;
                        var dh = sh * scaleY;
                        var dx = frame.Left + ((frame.Width - dw) * 0.5);
                        var dy = frame.Top + ((frame.Height - dh) * 0.5);
                        dc.DrawImage(image, new Rect(
                            (dx - regionLeft) * tex,
                            (dy - regionTop) * tex,
                            Math.Max(1, dw * tex),
                            Math.Max(1, dh * tex)));
                    }
                }
            }
            if (snapshot is not null && !snapshotRect.IsEmpty)
            {
                dc.DrawImage(snapshot, new Rect(
                    (snapshotRect.X - regionLeft) * tex,
                    (snapshotRect.Y - regionTop) * tex,
                    Math.Max(1, snapshotRect.Width * tex),
                    Math.Max(1, snapshotRect.Height * tex)));
            }
        }

        var rtb = new RenderTargetBitmap(fullW, fullH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var stride = fullW * 4;
        var pixels = new byte[stride * fullH];
        rtb.CopyPixels(pixels, stride, 0);

        // A single uniform blur separates frosted glass from refraction: there is
        // no noise displacement and no directional distortion.
        var blurRadius = Math.Max(1, (int)Math.Round(BlurRadius * tex));
        pixels = Blur(pixels, fullW, fullH, blurRadius);

        var output = new byte[tw * th * 4];
        CropRounded(
            pixels, fullW, fullH, output, tw, th, pad,
            Math.Clamp(cornerRadius * tex, 0, Math.Min(tw, th) * 0.5));

        var result = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgra32, null, output, tw * 4);
        result.Freeze();
        return result;
    }

    private static void CropRounded(
        byte[] src,
        int sw,
        int sh,
        byte[] dst,
        int dw,
        int dh,
        int pad,
        double radiusPx)
    {
        var radius = Math.Clamp(radiusPx, 0, Math.Min(dw, dh) * 0.5);
        for (var y = 0; y < dh; y++)
        {
            var row = y * dw * 4;
            var py = y + 0.5;
            for (var x = 0; x < dw; x++)
            {
                var o = row + (x * 4);
                if (RoundedRectSdf(x + 0.5, py, dw, dh, radius) >= 0)
                {
                    // Transparent corners keep the bitmap edge from leaking through
                    // while the WPF rounded clip animates.
                    dst[o] = 0;
                    dst[o + 1] = 0;
                    dst[o + 2] = 0;
                    dst[o + 3] = 0;
                    continue;
                }

                var i = (((y + pad) * sw) + x + pad) * 4;
                dst[o] = src[i];
                dst[o + 1] = src[i + 1];
                dst[o + 2] = src[i + 2];
                dst[o + 3] = 255;
            }
        }
    }

    private static double RoundedRectSdf(double x, double y, double width, double height, double radius)
    {
        radius = Math.Clamp(radius, 0, Math.Min(width, height) * 0.5);
        var qx = Math.Abs(x - (width * 0.5)) - ((width * 0.5) - radius);
        var qy = Math.Abs(y - (height * 0.5)) - ((height * 0.5) - radius);
        var outsideX = Math.Max(qx, 0);
        var outsideY = Math.Max(qy, 0);
        return Math.Sqrt((outsideX * outsideX) + (outsideY * outsideY))
             + Math.Min(Math.Max(qx, qy), 0)
             - radius;
    }

    private static byte[] Blur(byte[] source, int w, int h, int radius)
    {
        if (radius <= 0) return source;
        var a = (byte[])source.Clone();
        var b = new byte[source.Length];
        // Three box passes closely approximate a Gaussian blur while remaining fast
        // enough to rebuild after a window move or resize.
        for (var pass = 0; pass < 3; pass++)
        {
            BoxHorizontal(a, b, w, h, radius);
            BoxVertical(b, a, w, h, radius);
        }
        return a;
    }

    private static void BoxHorizontal(byte[] src, byte[] dst, int w, int h, int radius)
    {
        var div = (radius * 2) + 1;
        var clamp = new int[w + radius + radius + 1];
        for (var i = 0; i < clamp.Length; i++)
            clamp[i] = Math.Clamp(i - radius, 0, w - 1);

        for (var y = 0; y < h; y++)
        {
            var row = y * w * 4;
            for (var c = 0; c < 3; c++)
            {
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                    sum += src[row + (clamp[i + radius] * 4) + c];

                for (var x = 0; x < w; x++)
                {
                    dst[row + (x * 4) + c] = (byte)(sum / div);
                    sum += src[row + (clamp[x + radius + radius + 1] * 4) + c]
                         - src[row + (clamp[x + radius] * 4) + c];
                }
            }
            for (var x = 0; x < w; x++)
                dst[row + (x * 4) + 3] = 255;
        }
    }

    private static void BoxVertical(byte[] src, byte[] dst, int w, int h, int radius)
    {
        var div = (radius * 2) + 1;
        var clamp = new int[h + radius + radius + 1];
        for (var i = 0; i < clamp.Length; i++)
            clamp[i] = Math.Clamp(i - radius, 0, h - 1);

        for (var x = 0; x < w; x++)
        {
            var col = x * 4;
            for (var c = 0; c < 3; c++)
            {
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                    sum += src[((clamp[i + radius] * w * 4) + col) + c];

                for (var y = 0; y < h; y++)
                {
                    dst[((y * w * 4) + col) + c] = (byte)(sum / div);
                    sum += src[((clamp[y + radius + radius + 1] * w * 4) + col) + c]
                         - src[((clamp[y + radius] * w * 4) + col) + c];
                }
            }
        }

        for (var y = 0; y < h; y++)
        {
            var row = y * w * 4;
            for (var x = 0; x < w; x++)
                dst[row + (x * 4) + 3] = 255;
        }
    }

    private static Rect MonitorAt(int x, int y)
    {
        var point = new NativePoint { X = x, Y = y };
        var handle = MonitorFromPoint(point, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(handle, ref info))
            return RectFrom(info.Monitor);
        return VirtualScreen();
    }

    private static Rect VirtualScreen()
    {
        var x = GetSystemMetrics(76);
        var y = GetSystemMetrics(77);
        var w = Math.Max(1, GetSystemMetrics(78));
        var h = Math.Max(1, GetSystemMetrics(79));
        return new Rect(x, y, w, h);
    }

    private static Rect RectFrom(NativeRect rect) =>
        new(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));

    private readonly struct WallpaperInfo
    {
        public WallpaperInfo(Rect monitor, string? path, BitmapSource? image, WallpaperPosition position, Color background, long stamp)
        {
            Monitor = monitor;
            Path = path;
            Image = image;
            Position = position;
            Background = background;
            Stamp = stamp;
        }

        public Rect Monitor { get; }
        public string? Path { get; }
        public BitmapSource? Image { get; }
        public WallpaperPosition Position { get; }
        public Color Background { get; }
        public long Stamp { get; }
    }

    private sealed class WallpaperAsset
    {
        public BitmapSource? Image;
        public long Stamp;
    }

    private enum WallpaperPosition
    {
        Center,
        Tile,
        Stretch,
        Fit,
        Fill,
        Span,
    }

    private static WallpaperInfo ResolveWallpaper(Rect monitor)
    {
        var background = ReadBackgroundColor();
        string? path = null;
        var position = WallpaperPosition.Fill;

        try
        {
            var desktop = DesktopWallpaper.Value;
            if (desktop is not null)
            {
                position = ToPosition(desktop.GetPosition());
                var count = desktop.GetMonitorDevicePathCount();
                string? bestId = null;
                for (uint i = 0; i < count; i++)
                {
                    var id = desktop.GetMonitorDevicePathAt(i);
                    if (string.IsNullOrEmpty(id)) continue;
                    desktop.GetMonitorRECT(id, out var rect);
                    var bounds = RectFrom(rect);
                    if (Math.Abs(bounds.Left - monitor.Left) < 2 &&
                        Math.Abs(bounds.Top - monitor.Top) < 2 &&
                        Math.Abs(bounds.Width - monitor.Width) < 2 &&
                        Math.Abs(bounds.Height - monitor.Height) < 2)
                    {
                        bestId = id;
                        break;
                    }
                    bestId ??= id;
                }

                if (bestId is not null)
                {
                    var candidate = desktop.GetWallpaper(bestId);
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                        path = candidate;
                }
            }
        }
        catch
        {
            // COM wallpaper service unavailable (older builds / restricted session).
        }

        if (path is null)
        {
            path = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", "") as string;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                path = null;

            var style = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", "10") as string;
            var tile = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "TileWallpaper", "0") as string;
            position = tile == "1"
                ? WallpaperPosition.Tile
                : style switch
                {
                    "0" => WallpaperPosition.Center,
                    "2" => WallpaperPosition.Stretch,
                    "6" => WallpaperPosition.Fit,
                    "22" => WallpaperPosition.Span,
                    _ => WallpaperPosition.Fill,
                };
        }

        var asset = LoadWallpaper(path);
        return new WallpaperInfo(monitor, path, asset?.Image, position, background, asset?.Stamp ?? 0);
    }

    private static WallpaperAsset? LoadWallpaper(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            lock (Sync)
            {
                if (Wallpapers.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                    return cached;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            BitmapSource source = bitmap;
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                source = converted;
            }

            var asset = new WallpaperAsset { Image = source, Stamp = stamp };
            lock (Sync)
            {
                if (Wallpapers.Count > 3) Wallpapers.Clear();
                Wallpapers[path] = asset;
            }
            return asset;
        }
        catch
        {
            return null;
        }
    }

    private static Color ReadBackgroundColor()
    {
        try
        {
            var raw = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Colors", "Background", "0 0 0") as string;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    byte.TryParse(parts[0], out var r) &&
                    byte.TryParse(parts[1], out var g) &&
                    byte.TryParse(parts[2], out var b))
                {
                    return Color.FromRgb(r, g, b);
                }
            }
        }
        catch
        {
        }

        return Color.FromRgb(0x14, 0x14, 0x1A);
    }

    private static WallpaperPosition ToPosition(DesktopWallpaperPosition position) => position switch
    {
        DesktopWallpaperPosition.Center => WallpaperPosition.Center,
        DesktopWallpaperPosition.Tile => WallpaperPosition.Tile,
        DesktopWallpaperPosition.Stretch => WallpaperPosition.Stretch,
        DesktopWallpaperPosition.Fit => WallpaperPosition.Fit,
        DesktopWallpaperPosition.Span => WallpaperPosition.Span,
        _ => WallpaperPosition.Fill,
    };

    private static readonly Lazy<IDesktopWallpaper?> DesktopWallpaper = new(() =>
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"));
            if (type is null) return null;
            return Activator.CreateInstance(type) as IDesktopWallpaper;
        }
        catch
        {
            return null;
        }
    });

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);

        uint GetMonitorDevicePathCount();

        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out NativeRect displayRect);

        void SetBackgroundColor(uint color);

        uint GetBackgroundColor();

        void SetPosition(DesktopWallpaperPosition position);

        DesktopWallpaperPosition GetPosition();
    }

    private enum DesktopWallpaperPosition
    {
        Center = 0,
        Tile = 1,
        Stretch = 2,
        Fit = 3,
        Fill = 4,
        Span = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
