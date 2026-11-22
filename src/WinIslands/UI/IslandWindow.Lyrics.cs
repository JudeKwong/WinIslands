using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using WinIslands.Services;

namespace WinIslands.UI;

public partial class IslandWindow
{
    // ── 歌词自动滚动 ──────────────────────────────────────────

    private bool _lyricsScrollQueued;
    // ── LiquidMusic 歌词滚动移植：ease-out cubic tween（180~520ms）──
    private DispatcherTimer? _lyricsTweenTimer;
    private double _lyricsTweenFrom;
    private double _lyricsTweenTo;
    private long _lyricsTweenStartMs;
    private double _lyricsTweenDurationMs = 380;
    private int _lyricsActiveIndex = -1;
    /// <summary>上次实际写入 ScrollViewer 的偏移（NaN = 尚未写入）；帧回调与 Settled 落位共用（2.3.3）。</summary>
    private double _lyricsScrollLastWritten = double.NaN;
    /// <summary>滚动写入去重阈值（DIP）：亚像素变化不写，减少 ScrollViewer 布局开销（2.3.3）。</summary>
    internal const double ScrollWriteEpsilon = 0.25;

    /// <summary>计算歌词行居中的目标滚动偏移（纯函数，可测）：视口相对坐标 + 当前偏移 = 内容坐标，
    /// 再减去半个视口、加上半个行高使当前句居中；双向钳制到 [0, maxOffset]，非法输入兜底 0。</summary>
    /// <summary>LiquidMusic：滚动时长 = clamp(|delta|*1.6, 180, 520) 毫秒。</summary>
    internal static double LyricsTweenDuration(double delta)
        => Math.Clamp(Math.Abs(double.IsFinite(delta) ? delta : 0.0) * 1.6, 180.0, 520.0);

    /// <summary>LiquidMusic：ease-out cubic，e = 1 - (1-p)^3。</summary>
    internal static double LyricsTweenEase(double p)
    {
        var t = double.IsFinite(p) ? Math.Clamp(p, 0.0, 1.0) : 1.0;
        return 1.0 - Math.Pow(1.0 - t, 3.0);
    }

    /// <summary>LiquidMusic：距离中心越远越小，下限 0.78；历史行不超过 0.86。</summary>
    internal static double LyricsFocusScale(double distance, bool past)
    {
        var d = double.IsFinite(distance) ? Math.Max(0.0, distance) : 0.0;
        var scale = Math.Max(0.78, 1.0 - d * 0.24);
        return past ? Math.Min(scale, 0.86) : scale;
    }

    /// <summary>LiquidMusic：距离中心越远越淡，下限 0.24；历史行再乘 0.3。</summary>
    internal static double LyricsFocusOpacity(double distance, bool past)
    {
        var d = double.IsFinite(distance) ? Math.Max(0.0, distance) : 0.0;
        var opacity = Math.Max(0.24, 1.0 - d * 0.8);
        return past ? opacity * 0.3 : opacity;
    }

    internal static double ComputeLyricsScrollTarget(double offset, double relY, double viewportH, double lineH, double maxOffset)
    {
        if (!double.IsFinite(offset) || !double.IsFinite(relY) || !double.IsFinite(viewportH) ||
            !double.IsFinite(lineH) || !double.IsFinite(maxOffset))
            return 0.0;                                            // 非法输入兜底：不产生 NaN/无限
        if (viewportH <= 0 || lineH < 0 || maxOffset < 0)
            return 0.0;                                            // 无有效滚动态：停在顶部
        var target = offset + relY - viewportH / 2 + lineH / 2;
        return Math.Clamp(target, 0.0, maxOffset);                 // 双向钳制：底部不超出 ScrollableHeight
    }

    /// <summary>滚动写入去重判定（纯函数，可测）：NaN（尚未写入）必写；非法值丢弃；与上次写入的差达到阈值才写。</summary>
    internal static bool ShouldWriteScrollOffset(double value, double lastWritten)
    {
        if (double.IsNaN(lastWritten)) return true;                // 首次写入
        if (!double.IsFinite(value)) return false;                 // 引擎已兜底，直接丢弃非法值
        return Math.Abs(value - lastWritten) >= ScrollWriteEpsilon;
    }
    private void QueueLyricsScroll(int index)
    {
        if (!IsLoaded || !IsVisible || !_vm.IsExpanded) return;
        if (_lyricsScrollQueued) return;
        _lyricsScrollQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _lyricsScrollQueued = false;
            // 执行时取最新索引：快速切句时排队中的旧索引会被最新句覆盖，滚动始终跟随当前句
            var current = _vm.LyricIndex >= 0 ? _vm.LyricIndex : index;
            ScrollLyricsTo(current);
        }, DispatcherPriority.Loaded);
    }

    private void ScrollLyricsTo(int index)
    {
        if (LyricsList.Items.Count == 0) return;
        if (!_vm.IsExpanded || !IsVisible || !IsLoaded) { StopLyricsScroll(); return; }
        index = Math.Clamp(index, 0, LyricsList.Items.Count - 1);
        _lyricsActiveIndex = index;
        var container = LyricsList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        if (container is null) return;

        var viewer = LyricsScroll;
        var relY = container.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
        var target = ComputeLyricsScrollTarget(
            viewer.VerticalOffset, relY, viewer.ViewportHeight, container.ActualHeight,
            Math.Max(0, viewer.ScrollableHeight));

        if (_settings.Current.ReduceMotion)
        {
            // 减少动态效果：不作补间，直接落位到当前句
            StopLyricsScroll();
            if (ShouldWriteScrollOffset(target, _lyricsScrollLastWritten))
            {
                _lyricsScrollLastWritten = target;
                viewer.ScrollToVerticalOffset(target);
            }
            ApplyLyricsFocusStyles(index);
            return;
        }

        var from = viewer.VerticalOffset;
        var delta = target - from;
        // LiquidMusic：距离小于 2px 直接落位；否则 ease-out cubic，时长 = clamp(|delta|*1.6, 180, 520)
        if (Math.Abs(delta) < 2)
        {
            StopLyricsScroll();
            viewer.ScrollToVerticalOffset(target);
            _lyricsScrollLastWritten = target;
            ApplyLyricsFocusStyles(index);
            return;
        }

        StartLyricsTween(from, target, LyricsTweenDuration(delta));
    }

    private void StartLyricsTween(double from, double to, double durationMs)
    {
        _lyricsTweenFrom = from;
        _lyricsTweenTo = to;
        _lyricsTweenStartMs = Environment.TickCount64;
        _lyricsTweenDurationMs = Math.Max(1, durationMs);
        if (_lyricsTweenTimer is null)
        {
            _lyricsTweenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _lyricsTweenTimer.Tick += OnLyricsTweenTick;
        }
        if (!_lyricsTweenTimer.IsEnabled) _lyricsTweenTimer.Start();
    }

    private void OnLyricsTweenTick(object? sender, EventArgs e)
    {
        if (LyricsScroll is null || LyricsList.Items.Count == 0 || !_vm.IsExpanded || !IsVisible || !IsLoaded)
        {
            StopLyricsScroll();
            return;
        }
        var p = Math.Min(1.0, (Environment.TickCount64 - _lyricsTweenStartMs) / _lyricsTweenDurationMs);
        var eased = LyricsTweenEase(p);
        var v = _lyricsTweenFrom + (_lyricsTweenTo - _lyricsTweenFrom) * eased;
        if (ShouldWriteScrollOffset(v, _lyricsScrollLastWritten))
        {
            _lyricsScrollLastWritten = v;
            LyricsScroll.ScrollToVerticalOffset(v);
        }
        ApplyLyricsFocusStyles(_lyricsActiveIndex);
        if (p >= 1.0)
        {
            StopLyricsScroll();
            LyricsScroll.ScrollToVerticalOffset(_lyricsTweenTo);
            _lyricsScrollLastWritten = _lyricsTweenTo;
            ApplyLyricsFocusStyles(_lyricsActiveIndex);
        }
    }

    /// <summary>
    /// LiquidMusic 同款逐行聚焦：距离视口中心越远越小越淡；已唱过的行再压暗。
    /// scale = max(0.78, 1-dist*0.24)，opacity = max(0.24, 1-dist*0.8)，历史行 opacity*0.3。
    /// </summary>
    private void ApplyLyricsFocusStyles(int activeIndex)
    {
        if (LyricsScroll is null || LyricsList.Items.Count == 0) return;
        var viewer = LyricsScroll;
        var viewportH = viewer.ViewportHeight;
        if (!double.IsFinite(viewportH) || viewportH <= 0) return;
        for (var i = 0; i < LyricsList.Items.Count; i++)
        {
            if (LyricsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el) continue;
            try
            {
                var relY = el.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
                var h = Math.Max(1.0, el.ActualHeight);
                var mid = relY + h / 2.0;
                var dist = Math.Abs(mid - viewportH * 0.5) / (viewportH * 0.66);
                var past = activeIndex >= 0 && i < activeIndex;
                var scale = LyricsFocusScale(dist, past);
                var opacity = LyricsFocusOpacity(dist, past);
                // 文本自身有 LyricEmphasis.OpacityBase 基础透明度，容器按比例补偿，避免叠乘过暗
                el.Opacity = Math.Clamp(opacity / LyricEmphasis.OpacityBase, 0.0, 1.0);
                el.RenderTransformOrigin = new Point(0.5, 0.5);
                if (el.RenderTransform is ScaleTransform st)
                {
                    st.ScaleX = scale;
                    st.ScaleY = scale;
                }
                else
                {
                    el.RenderTransform = new ScaleTransform(scale, scale);
                }
            }
            catch { /* 容器尚未完成布局时忽略 */ }
        }
    }

    /// <summary>停止歌词滚动（收起/隐藏/列表变化时）。</summary>
    private void StopLyricsScroll()
    {
        _lyricsTweenTimer?.Stop();
    }


}
