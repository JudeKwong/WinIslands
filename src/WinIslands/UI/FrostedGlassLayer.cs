using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace WinIslands.UI;

/// <summary>
/// WPF frosted-glass surface: the wallpaper behind the window is uniformly blurred
/// and covered by a translucent tint. No specular highlight, refraction or desktop
/// capture is used; it is a quiet, conventional frosted blur.
/// </summary>
public sealed class FrostedGlassLayer : FrameworkElement
{
    private Window? _host;
    private DispatcherTimer? _refreshTimer;
    private string? _lastSampleKey;
    private bool _refreshQueued;

    public static readonly DependencyProperty TintBrushProperty = DependencyProperty.Register(
        nameof(TintBrush), typeof(Brush), typeof(FrostedGlassLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDarkProperty = DependencyProperty.Register(
        nameof(IsDark), typeof(bool?), typeof(FrostedGlassLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(FrostedGlassLayer),
        new FrameworkPropertyMetadata(new CornerRadius(22), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(
        nameof(Intensity), typeof(double), typeof(FrostedGlassLayer),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender),
        static value => value is double d && d >= 0 && d <= 2);

    public Brush? TintBrush
    {
        get => (Brush?)GetValue(TintBrushProperty);
        set => SetValue(TintBrushProperty, value);
    }

    /// <summary>Null means infer from the tint luminance.</summary>
    public bool? IsDark
    {
        get => (bool?)GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>0..2. 1.0 keeps the default 55% frosted tint strength.</summary>
    public double Intensity
    {
        get => (double)GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    public FrostedGlassLayer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host = Window.GetWindow(this);
        if (_host is not null)
        {
            _host.LocationChanged += OnHostGeometryChanged;
            _host.SizeChanged += OnHostGeometryChanged;
        }
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host is not null)
        {
            _host.LocationChanged -= OnHostGeometryChanged;
            _host.SizeChanged -= OnHostGeometryChanged;
            _host = null;
        }
        _refreshTimer?.Stop();
        _refreshTimer = null;
        _lastSampleKey = null;
        _refreshQueued = false;
    }

    private void OnHostGeometryChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (!(w > 0.5) || !(h > 0.5))
            return;

        var intensity = Math.Clamp(Intensity, 0, 2);
        if (intensity <= 0.001)
            return;

        var tint = ResolveTintColor(TintBrush);
        var dark = IsDark ?? IsColorDark(tint);
        var radius = Math.Max(0, CornerRadius.TopLeft);
        radius = Math.Min(radius, Math.Min(w, h) * 0.5);
        var rect = new Rect(0, 0, w, h);
        var clip = new RectangleGeometry(rect, radius, radius);
        clip.Freeze();
        dc.PushClip(clip);

        var sampled = TryDrawSampledBackdrop(dc, rect, w, h);
        if (!sampled)
            DrawFallbackBody(dc, rect, tint, dark, w, h);

        if (sampled)
        {
            // Keep the frosted surface readable without hiding the blurred desktop.
            var tintAlpha = Alpha(255.0 * GlassBackdrop.TintStrength * Math.Min(intensity, 1.0));
            var tintLayer = new SolidColorBrush(WithAlpha(tint, tintAlpha));
            tintLayer.Freeze();
            dc.DrawRectangle(tintLayer, null, rect);
        }

        dc.Pop();

    }

    private bool TryDrawSampledBackdrop(DrawingContext dc, Rect rect, double w, double h)
    {
        try
        {
            if (!IsVisible || PresentationSource.FromVisual(this) is null)
                return false;

            var p0 = PointToScreen(new Point(0, 0));
            var p1 = PointToScreen(new Point(w, h));
            var sw = p1.X - p0.X;
            var sh = p1.Y - p0.Y;
            if (!(sw > 4) || !(sh > 4) || sw > 6000 || sh > 6000)
                return false;

            var dpi = VisualTreeHelper.GetDpi(this);
            var radius = Math.Min(CornerRadius.TopLeft, Math.Min(w, h) * 0.5);
            var bitmap = GlassBackdrop.Render(
                new Rect(p0.X, p0.Y, sw, sh), dpi.DpiScaleX, radius, Intensity, out var key);
            if (bitmap is null)
                return false;

            dc.DrawImage(bitmap, rect);
            if (!string.Equals(key, _lastSampleKey, StringComparison.Ordinal))
            {
                _lastSampleKey = key;
                QueueSampledRefresh();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void QueueSampledRefresh()
    {
        // During animated resizes the sampled key changes every frame. One debounced
        // refresh is enough to rebuild the final texture; a matching key never
        // schedules another render, so this cannot become an idle redraw loop.
        if (_refreshQueued) return;
        _refreshQueued = true;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(110),
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;
            _refreshQueued = false;
            InvalidateVisual();
        };
        _refreshTimer.Start();
    }

    private void DrawFallbackBody(DrawingContext dc, Rect rect, Color tint, bool dark, double w, double h)
    {
        // Restricted sessions may not allow wallpaper sampling: keep a readable,
        // completely flat tinted fallback with the same rounded shape.
        var body = new SolidColorBrush(WithAlpha(tint, Alpha((dark ? 0.82 : 0.78) * 255)));
        body.Freeze();
        var radius = Math.Min(CornerRadius.TopLeft, Math.Min(w, h) * 0.5);
        dc.DrawRoundedRectangle(body, null, rect, radius, radius);
    }

    private static byte Alpha(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static Color ResolveTintColor(Brush? brush) => brush switch
    {
        SolidColorBrush solid => solid.Color,
        GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops[0].Color,
        _ => Color.FromRgb(0x1C, 0x1C, 0x20),
    };

    private static bool IsColorDark(Color color)
    {
        var luma = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
        return luma < 132;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

}
