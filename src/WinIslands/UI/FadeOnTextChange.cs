using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace WinIslands.UI;

/// <summary>
/// 文本内容变化时的 iOS 式平滑过渡（2.2.0）：
/// 媒体信息（歌名/歌手/专辑）切换时不再是硬切，而是「快淡出 → 淡入」——透明度先柔和跌到 0.25
/// 再以 EaseOut 回到 1.0（约 260ms），视觉上像 iOS 的 Now Playing 刷新新曲目，避免文字“突跳”。
/// 可选 SlideUp=true 时叠加 6px 的上移归位（仅用于未启用跑马灯的文本，避免与 Marquee 的
/// RenderTransform 冲突）。ReduceMotion 开启时自动退化为直接切换。
///
/// 用法：local:FadeOnTextChange.Enabled="True" [local:FadeOnTextChange.SlideUp="True"]
/// </summary>
public static class FadeOnTextChange
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(FadeOnTextChange), new PropertyMetadata(false, OnEnabledChanged));
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    /// <summary>是否在淡入时叠加 6px 上移归位（与跑马灯互斥，仅用于无 Marquee 的文本）。</summary>
    public static readonly DependencyProperty SlideUpProperty = DependencyProperty.RegisterAttached(
        "SlideUp", typeof(bool), typeof(FadeOnTextChange), new PropertyMetadata(false));
    public static void SetSlideUp(DependencyObject o, bool v) => o.SetValue(SlideUpProperty, v);
    public static bool GetSlideUp(DependencyObject o) => (bool)o.GetValue(SlideUpProperty);

    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    // 首次赋值（初始绑定）不淡入：启动/进入页面时的首个文本直接显示，避免空窗闪烁。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, object> Initialized = new();

    /// <summary>全局低功耗覆盖（2.4.0）：由 IslandWindow 在设置变化时同步，统一限制本组件的动画帧率。</summary>
    public static bool LowPowerModeOverride;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        if ((bool)e.NewValue) TextDescriptor.AddValueChanged(tb, OnTextChanged);
        else TextDescriptor.RemoveValueChanged(tb, OnTextChanged);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock tb) return;
        try
        {
            if (GetEnabled(tb) != true) return;
            if (!tb.IsLoaded || !tb.IsVisible) return;
            if (tb.Opacity <= 0.03) return;                       // 本来就不可见（展开淡入中/隐藏）：跳过
            if (GetReduceMotion(tb)) return;                      // 减少动态效果：直接切换
            if (!Initialized.TryGetValue(tb, out _))
            {
                Initialized.Add(tb, new object());                // 初次绑定不淡入
                return;
            }

            // 跑马灯占用 RenderTransform 时仅做透明度过渡；否则附加 6px 上移归位
            if (GetSlideUp(tb) && tb.RenderTransform is not System.Windows.Media.TranslateTransform)
                tb.RenderTransform = new System.Windows.Media.TranslateTransform(0, 6);

            var from = tb.Opacity;
            var duration = TimeSpan.FromMilliseconds(260);
            var fade = new DoubleAnimationUsingKeyFrames
            {
                Duration = duration,
                FillBehavior = FillBehavior.Stop,
            };
            // 快跌到 0.25（EaseIn），再柔和弹回 1.0（EaseOut）——iOS 内容刷新手感
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Max(0.0, Math.Min(0.25, from)), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(85)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            AnimationFrameRate.Apply(fade, LowPowerModeOverride); // 2.4.0
            tb.BeginAnimation(UIElement.OpacityProperty, fade);

            if (GetSlideUp(tb) && tb.RenderTransform is System.Windows.Media.TranslateTransform tr)
            {
                var slide = new DoubleAnimationUsingKeyFrames
                {
                    Duration = duration,
                    FillBehavior = FillBehavior.Stop,
                };
                slide.KeyFrames.Add(new EasingDoubleKeyFrame(6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(85)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                });
                slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
                AnimationFrameRate.Apply(slide, LowPowerModeOverride); // 2.4.0
                tr.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
            }
        }
        catch
        {
            // 文本过渡异常绝不影响主流程（丢失一次淡入不影响使用）
        }
    }

    private static bool GetReduceMotion(TextBlock tb)
    {
        var w = Window.GetWindow(tb);
        return w is IslandWindow iw && iw.IsReduceMotionEnabled;
    }
}
