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
    // ── 声音波纹 / 封面取色 ─────────────────────────────────────

    private void ScheduleVisibilityRefresh()
    {
        if (_visibilityRefreshQueued) return;
        _visibilityRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _visibilityRefreshQueued = false;
            RefreshWave();
            ApplyCoverTint();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }
    private void RefreshWave()
    {
        var on = HasWave;
        _cachedWaveStyle = _settings.Current.WaveStyle ?? "Bars";
        _cachedWaveHeight = Math.Clamp(_settings.Current.WaveHeight, 0.25, 2.0);
        ApplyWaveStyleVisibility();
        var lowPower = _settings.Current.LowPowerMode;
        _cachedWaveFps = AnimationFrameRate.Current(lowPower);
        _cachedWaveLowPower = lowPower; // 2.3.9: 同步到缓存，供 OnWaveFrame 每帧直接读取
        // 三态：关闭 / 普通（CompositionTarget.Rendering 跟随显示器）/ 低功耗定时器（~120fps）
        var wantTimer = on && lowPower;
        var wantComposition = on && !lowPower;
        var isTimer = _waveTimer is not null;
        var isComposition = _waveRendering && !isTimer;
        if (wantTimer != isTimer || wantComposition != isComposition)
        {
            StopWaveRender();
            _nextWaveFrameTime = WaveNow(_waveClock); // 2.5.1: 直读刻度，免 TimeSpan 结构体构造
            if (wantTimer)
            {
                _waveRendering = true;
                _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };     // CompositionTarget.Rendering（跟随显示器刷新率）
                _waveTimer.Tick += (_, _) => OnWaveFrame(null, EventArgs.Empty);
                _waveTimer.Start();
            }
            else if (wantComposition)
            {
                _waveRendering = true;
                CompositionTarget.Rendering += OnWaveFrame;
            }
        }
        if (!on)
        {
            foreach (var sc in _waveBarsExpanded) sc.ScaleY = 0.16;
            foreach (var sc in _waveBarsCompact) sc.ScaleY = 0.16;
            foreach (var sc in _waveSpectrumExpanded) sc.ScaleY = 0.05;
            foreach (var sc in _waveSpectrumCompact) sc.ScaleY = 0.05;
            if (_waveRingScaleExpanded is not null) { _waveRingScaleExpanded.ScaleX = _waveRingScaleExpanded.ScaleY = 1.0; }
            if (_waveRingScaleCompact is not null) { _waveRingScaleCompact.ScaleX = _waveRingScaleCompact.ScaleY = 1.0; }
            foreach (var tr in _waveParticleTransformsExpanded) tr.Y = 0;
            foreach (var tr in _waveParticleTransformsCompact) tr.Y = 0;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWave)));
    }
    /// <summary>停止波纹渲染（摘除合成帧事件 + 停止降帧定时器）。</summary>
    private void StopWaveRender()
    {
        _waveRendering = false;
        if (_waveTimer is not null)
        {
            _waveTimer.Stop();
            _waveTimer = null;
        }
        CompositionTarget.Rendering -= OnWaveFrame;
    }
    /// <summary>合成帧回调：按帧间隔指数平滑，随真实音频电平起伏，动画连贯不卡顿。</summary>
    /// <summary>波形时钟秒值（2.5.1）：直读原始刻度 / 常量频率——比 Elapsed.TotalSeconds 少一次
    /// TimeSpan 结构体构造与两级属性解引用，与 SpringTicker.CurrentSeconds 同一换算模式。</summary>
    private static double WaveNow(System.Diagnostics.Stopwatch sw) => sw.ElapsedTicks / (double)System.Diagnostics.Stopwatch.Frequency;

    private void OnWaveFrame(object? sender, EventArgs e)
    {
        try
        {
            // 卡片弹簧与声波纹共用同一合成帧（开销极低）：动画期间波纹继续跟随音频，
            // 避免用户展开时看到波纹“冻住”（iOS 风格：形变与声波并行、互不打断）。
            // 仅旧 Storyboard 动效皮肤路径仍让路（该路径确实是多动画争抢）。
            if (_currentStoryboard is not null) return;
            if (!IsLoaded || !HasWave)
            {
                RefreshWave();
                return;
            }
            var now = WaveNow(_waveClock); // 2.5.1: 直读刻度（同 SpringTicker.CurrentSeconds 换算模式）
            if (!_cachedWaveLowPower)
            {
                if (!AnimationFrameRate.ShouldProcessFrame(now, ref _nextWaveFrameTime, _cachedWaveFps)) return;
            }

            var dt = KaraokeMath.ClampTickDelta(now - _lastWaveTime); // 2.4.8: 与歌词共用同一化钳制（分支链，免 Math 调用）
            _lastWaveTime = now;

            var level = WaveMath.ClampUnit(_vm.WaveLevel);
            var height = _cachedWaveHeight;
            var alpha = WaveMath.SmoothAlpha(dt, 22.0); // 帧率无关的指数平滑（2.4.8: NaN 安全纯函数）
            // 1.2.1 性能优化：只更新当前可见的波纹集合（展开=大波纹、紧凑=小波纹），
            // 隐藏面板每帧的 ScaleTransform 更新全部省掉，降低媒体播放时的 CPU 占用
            var expanded = _vm.IsExpanded;
            switch (_cachedWaveStyle)
            {
                case "Spectrum":
                    UpdateWaveSet(expanded ? _waveSpectrumExpanded : _waveSpectrumCompact, level, now, alpha, height, bias: 1);
                    break;
                case "Ring":
                    UpdateRingVisual(expanded ? _waveRingScaleExpanded : _waveRingScaleCompact, level, now, alpha);
                    break;
                case "Particles":
                    UpdateParticlesVisual(expanded ? _waveParticleTransformsExpanded : _waveParticleTransformsCompact, level, now, alpha, expanded ? 8.0 : 5.0);
                    break;
                default:
                    UpdateWaveSet(expanded ? _waveBarsExpanded : _waveBarsCompact, level, now, alpha, height);
                    break;
            }
        }
        catch
        {
            // 渲染异常绝不影响主流程
        }
    }

    private void UpdateWaveSet(List<ScaleTransform> bars, double level, double t, double alpha, double height, double bias = 0)
    {
        var n = bars.Count;
        if (n == 0) return;
        var (sinBase, cosBase) = WaveMath.WaveBase(t); // 2.5.0: 单次 SinCos，同参数一次主元换算
        var isPlaying = _vm.IsPlaying;
        // 2.3.5: 循环不变量提升到循环外（每帧少 n-1 次乘加）；i/n 改用预计算倒数乘法代替除法
        var invN = 1.0 / n;
        var ampA = (0.12 + 0.72 * level) * height;   // 非 bias 模式的振幅系数（含高度）
        var baseA = 0.10 * height;
        var ampB = (0.14 + 0.66 * level) * height;   // bias（谱状）模式的振幅系数
        var baseB = 0.05 * height;
        var minClamp = bias > 0 ? 0.05 : 0.08;
        // 2.5.6: bias 斜坡预计算——每迭代一次加法替代两次乘法（0.55+0.45*i/n），默认 Bars 路径不执行
        var rampStep = 0.45 * invN;
        var ramp = 0.55;
        for (var i = 0; i < n; i++)
        {
            var sc = bars[i];
            double target;
            if (isPlaying)
            {
                var wave = WaveValue(sinBase, cosBase, i, WaveOffsets09);
                if (bias > 0)
                    target = WaveMath.ClampRange(baseB + ampB * wave * ramp, 0.05, 1.0);
                else
                    target = WaveMath.ClampRange(baseA + ampA * wave, 0.08, 1.0);
            }
            else
            {
                target = minClamp;
            }
            if (bias > 0) ramp += rampStep;
            // 2.3.5: 亚像素写入去重——单帧位移量小于阈值时跳过属性写入，
            // 歌曲节拍飘动的微小蠕动不再每秒触发合成线程的脏标记，视觉不变但帧节奏更稳
            if (!double.IsFinite(sc.ScaleY)) { sc.ScaleY = target; continue; }
            if (WaveMath.ShouldWriteEased(sc.ScaleY, target, alpha, WaveMath.ScaleEpsilon))
                sc.ScaleY += (target - sc.ScaleY) * alpha;
        }
    }

    private void ApplyWaveStyleVisibility()
    {
        var style = _cachedWaveStyle;
        if (string.Equals(_appliedWaveStyle, style, StringComparison.Ordinal)) return;
        _appliedWaveStyle = style;
        var bars = style == "Bars" ? Visibility.Visible : Visibility.Collapsed;
        var spec = style == "Spectrum" ? Visibility.Visible : Visibility.Collapsed;
        var ring = style == "Ring" ? Visibility.Visible : Visibility.Collapsed;
        var part = style == "Particles" ? Visibility.Visible : Visibility.Collapsed;
        if (WaveBarsPanelCompact is not null) WaveBarsPanelCompact.Visibility = bars;
        if (WaveBarsPanelExpanded is not null) WaveBarsPanelExpanded.Visibility = bars;
        if (WaveSpectrumHostCompact is not null) WaveSpectrumHostCompact.Visibility = spec;
        if (WaveSpectrumHostExpanded is not null) WaveSpectrumHostExpanded.Visibility = spec;
        if (WaveRingHostCompact is not null) WaveRingHostCompact.Visibility = ring;
        if (WaveRingHostExpanded is not null) WaveRingHostExpanded.Visibility = ring;
        if (WaveParticlesHostCompact is not null) WaveParticlesHostCompact.Visibility = part;
        if (WaveParticlesHostExpanded is not null) WaveParticlesHostExpanded.Visibility = part;
    }

    /// <summary>构建频谱/环形/粒子三种备选波纹（启动时一次性创建，颜色随主题绑定）。</summary>
    private void InitWaveVisualStyles()
    {
        try
        {
            BuildSpectrumBars(WaveSpectrumHostCompact, _waveSpectrumCompact, 12, 1.8, 1.2);
            BuildSpectrumBars(WaveSpectrumHostExpanded, _waveSpectrumExpanded, 16, 2.2, 1.2);
            _waveRingScaleCompact = BuildRing(WaveRingHostCompact, 12);
            _waveRingScaleExpanded = BuildRing(WaveRingHostExpanded, 16);
            BuildParticles(WaveParticlesHostCompact, _waveParticleTransformsCompact, 8);
            BuildParticles(WaveParticlesHostExpanded, _waveParticleTransformsExpanded, 10);
            ApplyWaveStyleVisibility();
        }
        catch
        {
            // 备选样式构建失败时仅保留默认柱状，不影响主流程
        }
    }

    /// <summary>频谱条：窄条下对齐，右高左低频段分布，随节奏起伏。</summary>
    private void BuildSpectrumBars(Grid? host, List<ScaleTransform> list, int count, double barW, double gap)
    {
        if (host is null) return;
        var left = (host.Width - (count * barW + (count - 1) * gap)) / 2.0;
        for (var i = 0; i < count; i++)
        {
            var sc = new ScaleTransform(1, 0.05);
            var bar = new Border
            {
                Width = barW,
                Height = host.Height,
                CornerRadius = new CornerRadius(Math.Max(0.3, barW / 2)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(left, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 1.0),
                RenderTransform = sc,
            };
            BindingOperations.SetBinding(bar, Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(TextPrimary)) { Source = this });
            host.Children.Add(bar);
            list.Add(sc);
            left += barW + gap;
        }
    }

    /// <summary>环形波纹：圆点中心，随节奏缩放。</summary>
    private ScaleTransform? BuildRing(Grid? host, double diameter)
    {
        if (host is null) return null;
        var sc = new ScaleTransform(1, 1);
        var ring = new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = sc,
        };
        BindingOperations.SetBinding(ring, Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(TextPrimary)) { Source = this });
        host.Children.Add(ring);
        return sc;
    }

    /// <summary>粒子波纹：散布小圆点，随节奏上下脉冲。</summary>
    private void BuildParticles(Grid? host, List<TranslateTransform> list, int count)
    {
        if (host is null) return;
        var spacing = host.Width / count;
        for (var i = 0; i < count; i++)
        {
            var tr = new TranslateTransform(0, 0);
            var p = new System.Windows.Shapes.Ellipse
            {
                Width = 2.5,
                Height = 2.5,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(spacing * (i + 0.5) - 1.25, 0, 0, 0),
                RenderTransform = tr,
            };
            BindingOperations.SetBinding(p, System.Windows.Shapes.Ellipse.FillProperty, new System.Windows.Data.Binding(nameof(TextPrimary)) { Source = this });
            host.Children.Add(p);
            list.Add(tr);
        }
    }

    private void UpdateRingVisual(ScaleTransform? ring, double level, double t, double alpha)
    {
        if (ring is null) return;
        double target = 1.0;
        if (_vm.IsPlaying)
        {
            var wave = WaveMath.Pulse01(t * 6.0); // 2.9.6: 与 0.5+0.5*Sin 逐位一致
            target = 1.0 + 0.24 * level * wave;
        }
        if (!double.IsFinite(ring.ScaleX)) { ring.ScaleX = target; ring.ScaleY = target; return; }
        // 2.3.5: 亚像素写入去重（同 UpdateWaveSet）
        if (!WaveMath.ShouldWriteEased(ring.ScaleX, target, alpha, WaveMath.ScaleEpsilon)) return;
        // 2.3.9: 一次求值写入两个轴，不再把刚写入的 ScaleX 读回给 ScaleY
        var next = WaveMath.EaseToward(ring.ScaleX, target, alpha);
        ring.ScaleX = next;
        ring.ScaleY = next;
    }

    private void UpdateParticlesVisual(List<TranslateTransform> parts, double level, double t, double alpha, double maxY)
    {
        var n = parts.Count;
        if (n == 0) return;
        var (sinBase, cosBase) = WaveMath.WaveBase(t); // 2.5.0: 单次 SinCos，同参数一次主元换算
        var isPlaying = _vm.IsPlaying;
        var amp = level * maxY; // 2.3.5: 循环不变量提升
        for (var i = 0; i < n; i++)
        {
            var tr = parts[i];
            double target = 0;
            if (isPlaying)
            {
                var wave = WaveValue(sinBase, cosBase, i, WaveOffsets13);
                target = -wave * amp;
            }
            if (!double.IsFinite(tr.Y)) { tr.Y = target; continue; }
            // 2.3.5: 亚像素写入去重（同 UpdateWaveSet，位移门限 OffsetEpsilon）
            if (WaveMath.ShouldWriteEased(tr.Y, target, alpha, WaveMath.OffsetEpsilon))
                tr.Y += (target - tr.Y) * alpha;
        }
    }

    private static double WaveValue(double sinBase, double cosBase, int index, (double Sin, double Cos)[] offsets)
        => WaveMath.WaveValue(sinBase, cosBase, offsets[index]);

    private void ApplyCoverTint(bool forceRebuild = false)
    {
        try
        {
            if (_theme.IsDaozai) { ClearCoverTint(); SubscribeTintRendering(false); return; } // 岛仔经典黑不随封面取色
            var src = _vm.Artwork;
            if (src is null || !_settings.Current.CoverTintBackground || !IsVisible || !_vm.IsExpanded)
            {
                ClearCoverTint();
                SubscribeTintRendering(false);
                return;
            }
            // 缓存封面取色结果：同封面不重复采样（避免展开/收起时重新 RenderTargetBitmap）
            if (!ReferenceEquals(src, _lastSampledArtwork))
            {
                _lastSampledColor = SampleCoverColor(src);
                _lastSampledArtwork = src;
            }
            var color = _lastSampledColor;
            if (color is null)
            {
                ClearCoverTint();
                SubscribeTintRendering(false);
                return;
            }

            // 封面主色变化（换曲）时重建渐变；同曲只复用并更新 Alpha（呼吸）
            if (_tintBrush is null || _tintCoverColor != color || forceRebuild) // 主题切换时强制重建（基色随新主题）
            {
                _tintCoverColor = color;
                var baseColor = (_theme.CardBackground as SolidColorBrush)?.Color
                    ?? System.Windows.Media.Color.FromArgb(0xF0, 0x14, 0x14, 0x1E);
                _tintBrush = new LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint = new System.Windows.Point(1, 1),
                };
                _tintStop0 = new GradientStop(System.Windows.Media.Color.FromArgb((byte)0xE6, color.Value.R, color.Value.G, color.Value.B), 0);
                _tintStop1 = new GradientStop(baseColor, 1);
                _tintBrush.GradientStops.Add(_tintStop0);
                _tintBrush.GradientStops.Add(_tintStop1);
            }
            Card.Background = _tintBrush;
            // 主题底色玻璃层归零，避免与封面渐变双重叠加
            _glassAnimSb?.Stop();
            _glassAnimSb = null;
            if (GlassLayer is not null) GlassLayer.Opacity = 0;
            _tintPhaseTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            SubscribeTintRendering(true);
        }
        catch
        {
            ClearCoverTint();
            SubscribeTintRendering(false);
        }
    }

    /// <summary>订阅 / 取消合成帧驱动（空闲时不占 CPU）。</summary>
    private void SubscribeTintRendering(bool subscribe)
    {
        if (subscribe == _tintRenderingSubscribed) return;
        _tintRenderingSubscribed = subscribe;
        if (_tintTimer is null)
        {
            _tintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _tintTimer.Tick += OnTintFrame;
        }
        if (subscribe) _tintTimer.Start();
        else _tintTimer.Stop();
    }

    private void OnTintFrame(object? sender, EventArgs e)
    {
        // 展开/收起动画期间暂停封面取色呼吸，减少渲染线程争抢（动画结束即恢复）
        if (_cardAnimating || _currentStoryboard is not null) return;
        if (!_vm.IsExpanded || !_settings.Current.CoverTintBackground || _vm.Artwork is null || _tintStop0 is null)
        {
            SubscribeTintRendering(false);
            return;
        }
        var c = _tintCoverColor;
        if (c is null) { SubscribeTintRendering(false); return; }
        var t = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _tintPhaseTicks) / System.Diagnostics.Stopwatch.Frequency;
        var alpha = 0.85 + 0.06 * WaveMath.Pulse01(t * 0.35); // 0.85..0.97 慢周期（2.9.6：与 (0.5+0.5*Sin) 逐位一致）
        var a = (byte)WaveMath.RoundEven(alpha * 255); // 2.9.6: 与 (byte)Math.Round 逐位一致
        if (_tintStop0.Color.A != a)
            _tintStop0.Color = System.Windows.Media.Color.FromArgb(a, c.Value.R, c.Value.G, c.Value.B);
    }

    /// <summary>恢复 Card 背景为绑定的主题色（移除封面取色）。</summary>
    private void ClearCoverTint()
    {
        try
        {
            Card.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(CardBackground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
            });
        }
        catch
        {
            Card.Background = _theme.CardBackground;
        }
        // 无封面取色时恢复主题 tint，并重新启用玻璃分层（展开态更实、紧凑态通透）
        ApplyGlassLayer();
        if (GlassLayer is not null && (_vm.IsExpanded || !_settings.Current.CoverTintBackground))
            AnimateGlass(_vm.IsExpanded);
    }

    /// <summary>把封面渲染到 1x1 位图采样主色（RGBA）。</summary>
    private static System.Windows.Media.Color? SampleCoverColor(ImageSource src)
    {
        try
        {
            var rtb = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
            var img = new System.Windows.Controls.Image { Source = src, Stretch = Stretch.UniformToFill };
            rtb.Render(img);
            var px = new byte[4];
            rtb.CopyPixels(px, 4, 0);
            if (px[3] < 40) return null; // 透明/未加载完成，放弃取色
            return System.Windows.Media.Color.FromArgb(255, px[2], px[1], px[0]);
        }
        catch
        {
            return null;
        }
    }

    // ── 点击穿透 ──────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            var x = (short)(lParam.ToInt64() & 0xFFFF);
            var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var local = PointFromScreen(new Point(x, y));
            handled = true;
            return IsPointOverCard(local) ? (IntPtr)HTCLIENT : (IntPtr)HTTRANSPARENT;
        }

        return IntPtr.Zero;
    }

    private bool IsPointOverCard(Point local)
    {
        // 经 LayoutTransform（字号缩放）后的真实视觉矩形；直接用 ActualWidth/Height 会在
        // 非 100% 字号缩放下把卡片右/下部分误判为窗口外，导致点击穿透、边缘拖拽失效。
        try
        {
            var transform = Card.TransformToAncestor(this);
            var topLeft = transform.Transform(new Point(0, 0));
            var bottomRight = transform.Transform(new Point(Card.ActualWidth, Card.ActualHeight));
            return local.X >= topLeft.X && local.Y >= topLeft.Y &&
                   local.X <= bottomRight.X && local.Y <= bottomRight.Y;
        }
        catch
        {
            return false;
        }
    }
}
