using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WinIslands.Mac.UI;

/// <summary>紧凑态播放波纹：4 根圆角竖条按正弦起伏（播放中逐帧推进，暂停时静态低幅）。</summary>
public class MusicWave : Control
{
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<MusicWave, bool>(nameof(IsPlaying), true);

    public static readonly StyledProperty<IBrush> BarBrushProperty =
        AvaloniaProperty.Register<MusicWave, IBrush>(nameof(BarBrush), Brushes.White);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _frameQueued;

    static MusicWave()
    {
        AffectsRender<MusicWave>(IsPlayingProperty, BarBrushProperty);
        IsPlayingProperty.Changed.AddClassHandler<MusicWave>((x, _) => x.OnPlayingChanged());
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public IBrush BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsPlaying) QueueFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frameQueued = false;
    }

    private void OnPlayingChanged()
    {
        if (IsPlaying) QueueFrame();
        else InvalidateVisual();
    }

    private void QueueFrame()
    {
        if (_frameQueued) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        _frameQueued = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameQueued = false;
            if (!IsPlaying) return;
            InvalidateVisual();
            QueueFrame();
        });
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        const int bars = 4;
        const double barWidth = 3;
        var gap = (w - bars * barWidth) / (bars - 1);
        var t = _clock.Elapsed.TotalSeconds;
        var brush = BarBrush;

        for (var i = 0; i < bars; i++)
        {
            // 播放中：每根条相位错开的正弦起伏；暂停：静态低幅（按索引微差，形状像停顿的音量条）
            var amp = IsPlaying
                ? Math.Sin(t * 4.4 + i * 1.15) * 0.5 + 0.5
                : 0.22 + (i % 2) * 0.14;
            var bh = Math.Max(3, 3 + amp * (h - 3));
            var rect = new Rect(i * (barWidth + gap), (h - bh) / 2, barWidth, bh);
            context.DrawRectangle(brush, null, new RoundedRect(rect, barWidth / 2));
        }
    }
}
