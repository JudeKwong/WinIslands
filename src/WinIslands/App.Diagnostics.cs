using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using Application = System.Windows.Application;
using Localization = WinIslands.UI.Localization;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using WinIslands.Diagnostics;
using WinIslands.Services;
using WinIslands.UI;

namespace WinIslands;

public partial class App : Application
{
    /// <summary>Renders the first island window at 2x and exits. Used for documentation previews.</summary>
    private async Task CapturePreviewAsync(string rawPath, double previewScale = 1.0, bool forceExpanded = true)
    {
        try
        {
            // Let bindings, lyrics, and open/expand animations settle before rendering.
            await Task.Delay(1500);
            var window = _windows.FirstOrDefault();
            if (window is null)
            {
                AppLogger.Error("Screenshot failed: no island window is available.");
                Shutdown(1);
                return;
            }

            // The screenshot path is diagnostic-only: keep the requested expansion even though
            // the mouse is not over the card. Normal hover behavior remains unchanged.
            window.KeepExpandedForPreview = true;
            // High-resolution promo captures are rendered offscreen at 1x layout scale.
            // Applying PreviewScale itself can make the transparent window wider than the
            // desktop, causing Windows to clamp it and clip the card on either side.
            var offscreenOnly = previewScale > 4.0;
            window.PreviewScale = offscreenOnly ? 1.0 : previewScale;
            if (forceExpanded) _vm!.IsExpanded = true;

            // Force the real window to the foreground on its own desktop, then capture the exact
            // client rectangle. RenderTargetBitmap cannot reliably capture WPF layered windows.
            window.Visibility = System.Windows.Visibility.Visible;
            window.WindowState = System.Windows.WindowState.Normal;
            window.Topmost = true;
            window.ShowActivated = true;
            window.Show();
            window.Activate();
            window.Focus();
            window.UpdateLayout();
            // Diagnostics can start faster than the clock/lyrics services on a warm machine.
            // Wait for the real content to be ready so a preview never captures a half-initialized card.
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (window.IsLoaded && !string.IsNullOrWhiteSpace(_vm.ClockText) &&
                    !string.IsNullOrWhiteSpace(_vm.DateText) && _vm.LyricIndex >= 0)
                    break;
                await Task.Delay(100);
            }

            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == System.IntPtr.Zero)
            {
                AppLogger.Error("Screenshot failed: island window has no HWND.");
                Shutdown(1);
                return;
            }

            await Task.Delay(500);

            AppLogger.Info($"Preview state: expanded={_vm!.IsExpanded}, window={window.ActualWidth:0}x{window.ActualHeight:0}, visible={window.IsVisible}, clock='{_vm!.ClockText}', date='{_vm!.DateText}', lyricIndex={_vm!.LyricIndex}, scroll={window.LyricsScroll.VerticalOffset:0.0}/{window.LyricsScroll.ScrollableHeight:0.0}/{window.LyricsScroll.ViewportHeight:0.0}");
            if (!GetWindowRect(hwnd, out var rect))
            {
                AppLogger.Error("Screenshot failed: GetWindowRect returned false.");
                Shutdown(1);
                return;
            }

            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            System.Drawing.Bitmap bitmap;
            var offscreenRender = offscreenOnly;
            if (offscreenOnly)
            {
                // Render the live card visual directly at the requested pixel density. This path
                // does not need an oversized on-screen window and therefore keeps every edge.
                AppLogger.Info($"Rendering preview offscreen at {previewScale:0.#}x.");
                bitmap = RenderCardVisual(window, previewScale);
            }
            else
            {
                try
                {
                    bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height),
                            System.Drawing.CopyPixelOperation.SourceCopy);
                    }
                }
                catch (Exception ex)
                {
                    // Locked or disconnected desktops cannot be read with CopyFromScreen. Render the
                    // card visual directly instead; this also avoids blurring content at large scales.
                    AppLogger.Warn($"Screen capture unavailable ({ex.GetType().Name}: {ex.Message}); rendering card visual offscreen.");
                    bitmap = RenderCardVisual(window, previewScale);
                    offscreenRender = true;
                }
            }

            var path = System.IO.Path.GetFullPath(rawPath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // Crop to the real card and rebuild the rounded-corner alpha so promo assets
            // never include whatever desktop happens to be behind the island.
            using var captured = bitmap;
            using var card = offscreenRender ? bitmap : CropCardFromCapture(window, bitmap, rect);
            card.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            AppLogger.Info($"Preview screenshot saved: {path} ({card.Width}x{card.Height}, {(offscreenRender ? "offscreen render" : "card cropped")})");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Screenshot failed", ex);
            Shutdown(1);
            return;
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>
    /// 本地回归诊断：连续执行 5 次展开→收起、快速切换、减少动态效果三种路径，
    /// 每次收起后校验卡片宽度稳定、未超出宿主窗口、四角圆角未被裁成直角。
    /// 只在 --roundtrip-diag 参数下运行；通过时退出码 0，失败时退出码 1。
    /// </summary>
    /// <summary>Renders the real island card visual to a transparent bitmap without a desktop capture.</summary>
    private static System.Drawing.Bitmap RenderCardVisual(IslandWindow window, double scale)
    {
        var card = window.Card ?? throw new InvalidOperationException("Island card visual is not available.");
        // Transparent offscreen rendering cannot use ClearType. Ideal formatting also keeps
        // the vector glyph shapes clean when the promo preview is rendered at 4x-6x.
        System.Windows.Media.TextOptions.SetTextFormattingMode(card, System.Windows.Media.TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextRenderingMode(card, System.Windows.Media.TextRenderingMode.ClearType);
        var w = Math.Max(1, (int)Math.Round(card.ActualWidth * scale));
        var h = Math.Max(1, (int)Math.Round(card.ActualHeight * scale));
        var dpi = 96.0 * Math.Max(1.0, scale);

        var visual = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi, dpi,
            System.Windows.Media.PixelFormats.Pbgra32);
        visual.Render(card);

        var stride = w * 4;
        var pixels = new byte[stride * h];
        visual.CopyPixels(pixels, stride, 0);

        // RenderTargetBitmap returns premultiplied Pbgra32 pixels. Keep them premultiplied
        // while converting; otherwise anti-aliased glyph edges turn into blocky artifacts.
        using var raw = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = raw.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            raw.UnlockBits(data);
        }

        var output = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(output))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);
            using var mask = new System.Drawing.Drawing2D.GraphicsPath();
            var r = (float)Math.Max(0, card.CornerRadius.TopLeft * scale);
            if (r > 0.5f)
            {
                mask.AddArc(0, 0, r * 2, r * 2, 180, 90);
                mask.AddArc(w - r * 2, 0, r * 2, r * 2, 270, 90);
                mask.AddArc(w - r * 2, h - r * 2, r * 2, r * 2, 0, 90);
                mask.AddArc(0, h - r * 2, r * 2, r * 2, 90, 90);
                mask.CloseFigure();
            }
            else
            {
                mask.AddRectangle(new System.Drawing.Rectangle(0, 0, w, h));
            }

            graphics.SetClip(mask);
            graphics.DrawImage(raw, 0, 0, w, h);
        }

        return output;
    }

    private static System.Drawing.Bitmap CropCardFromCapture(IslandWindow window, System.Drawing.Bitmap source, RECT windowRect)
    {
        var scale = window.PreviewScale;
        var card = window.Card!;
        var origin = card.PointToScreen(new System.Windows.Point(0, 0));
        var w = Math.Max(1, (int)Math.Round(card.ActualWidth * scale));
        var h = Math.Max(1, (int)Math.Round(card.ActualHeight * scale));
        var x = Math.Clamp((int)Math.Round(origin.X - windowRect.Left), 0, Math.Max(0, source.Width - w));
        var y = Math.Clamp((int)Math.Round(origin.Y - windowRect.Top), 0, Math.Max(0, source.Height - h));

        var output = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(output))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);
            using var mask = new System.Drawing.Drawing2D.GraphicsPath();
            var r = (float)Math.Max(0, card.CornerRadius.TopLeft * scale);
            if (r > 0.5f)
            {
                mask.AddArc(0, 0, r * 2, r * 2, 180, 90);
                mask.AddArc(w - r * 2, 0, r * 2, r * 2, 270, 90);
                mask.AddArc(w - r * 2, h - r * 2, r * 2, r * 2, 0, 90);
                mask.AddArc(0, h - r * 2, r * 2, r * 2, 90, 90);
                mask.CloseFigure();
            }
            else
            {
                mask.AddRectangle(new System.Drawing.Rectangle(0, 0, w, h));
            }

            graphics.SetClip(mask);
            graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, w, h),
                new System.Drawing.Rectangle(x, y, w, h), System.Drawing.GraphicsUnit.Pixel);
        }

        return output;
    }

    private async Task CaptureRoundTripAsync(string rawDir)
    {
        var failures = new List<string>();
        var passed = false;
        try
        {
            var dir = Path.GetFullPath(rawDir);
            Directory.CreateDirectory(dir);
            await Task.Delay(1800);
            var window = _windows.FirstOrDefault();
            if (window is null || _vm is null || _settings is null)
            {
                failures.Add("no island window / view model / settings available");
                return;
            }

            window.KeepExpandedForPreview = true;

            void Check(string stage, IslandWindow.CardDiagnosticResult r)
            {
                if (!double.IsFinite(r.Width) || !double.IsFinite(r.WindowWidth))
                {
                    failures.Add($"{stage}: invalid measurement ({r.Detail})");
                    return;
                }
                if (!r.CornersTransparent)
                    failures.Add($"{stage}: corners are not transparent or card is clipped ({r.Detail})");
                if (!r.ExpandedLayoutOk)
                    failures.Add($"{stage}: expanded cover/progress overlap ({r.Detail})");
                if (r.Width > r.WindowWidth + 0.5)
                    failures.Add($"{stage}: card width {r.Width:0.##} exceeds window width {r.WindowWidth:0.##}");
                if (!r.RadiusMatches)
                    failures.Add($"{stage}: radius mismatch card={r.Radius:0.###}, glass={r.GlassRadius:0.###}, clip={r.ClipRadius:0.###}, expected={r.ExpectedRadius:0.###} ({r.Detail})");
                if (double.IsFinite(r.ClipWidth) && Math.Abs(r.ClipWidth - r.Width) > 1.5)
                    failures.Add($"{stage}: clip width {r.ClipWidth:0.##} does not match card width {r.Width:0.##}");
            }

            var collapsedHeights = new List<double>();
            for (var i = 1; i <= 5; i++)
            {
                _vm.IsExpanded = true;
                await Task.Delay(1700);
                Check($"cycle{i}-expanded",
                    window.CaptureCardDiagnostic(Path.Combine(dir, $"roundtrip-{i}-expanded.png"), $"cycle{i}-expanded"));

                _vm.IsExpanded = false;
                await Task.Delay(1900);
                var collapsed = window.CaptureCardDiagnostic(
                    Path.Combine(dir, $"roundtrip-{i}-collapsed.png"), $"cycle{i}-collapsed");
                Check($"cycle{i}-collapsed", collapsed);
                collapsedHeights.Add(collapsed.Height);

                // 收尾回调/延迟布局完成后再次测量，确认尺寸不会回弹或继续膨胀。
                await Task.Delay(800);
                var late = window.CaptureCardDiagnostic(
                    Path.Combine(dir, $"roundtrip-{i}-collapsed-late.png"), $"cycle{i}-collapsed-late");
                Check($"cycle{i}-collapsed-late", late);
                if (double.IsFinite(late.Width) && Math.Abs(late.Width - collapsed.Width) > 0.5)
                    failures.Add($"cycle{i}: width changed after settle ({collapsed.Width:0.##} -> {late.Width:0.##})");
            }
            // 紧凑态宽度会随当前歌词文本长度按设计变化；跨轮次只校验高度稳定。
            // 每轮宽度是否在收敛后继续变化，由上方 collapsed-late 对比单独校验。
            var minHeight = double.MaxValue;
            var maxHeight = double.MinValue;
            foreach (var h in collapsedHeights)
            {
                if (!double.IsFinite(h)) continue;
                minHeight = Math.Min(minHeight, h);
                maxHeight = Math.Max(maxHeight, h);
            }
            if (collapsedHeights.Count >= 5 && maxHeight - minHeight > 2.0)
                failures.Add($"collapsed height drifted across cycles ({minHeight:0.##}..{maxHeight:0.##}); expected stable size");

            // 快速展开后立即收起：最常见的手势路径。
            _vm.IsExpanded = true;
            await Task.Delay(120);
            _vm.IsExpanded = false;
            await Task.Delay(2300);
            Check("fast-toggle-collapsed",
                window.CaptureCardDiagnostic(Path.Combine(dir, "roundtrip-fast-collapsed.png"), "fast-toggle-collapsed"));

            // 减少动态效果路径：没有弹簧补间，直接写最终几何。
            var originalReduceMotion = _settings.Current.ReduceMotion;
            _settings.Current.ReduceMotion = true;
            _vm.IsExpanded = true;
            await Task.Delay(500);
            _vm.IsExpanded = false;
            await Task.Delay(1400);
            Check("reduce-motion-collapsed",
                window.CaptureCardDiagnostic(Path.Combine(dir, "roundtrip-reduce-motion-collapsed.png"), "reduce-motion-collapsed"));
            _settings.Current.ReduceMotion = originalReduceMotion;

            passed = failures.Count == 0;
        }
        catch (Exception ex)
        {
            failures.Add("exception: " + ex);
        }
        finally
        {
            foreach (var failure in failures)
                AppLogger.Error("Round-trip FAIL: " + failure);
            if (passed)
                AppLogger.Info("Round-trip diagnostic PASSED: 5 cycles + fast toggle + reduce motion; pill shape stable.");
            else
                AppLogger.Error($"Round-trip diagnostic FAILED: {failures.Count} issue(s).");
            Shutdown(passed ? 0 : 1);
        }
    }
    /// <summary>
    /// Collapse-frame regression diagnostic: samples every 16 ms for 1.5 s, covering the
    /// full spring plus the 1.2 s compact-restore window. Any frame whose three radius
    /// consumers disagree with the live card height fails the run.
    /// </summary>
    private async Task CaptureCollapseFramesAsync(string rawDir)
    {
        var failures = new List<string>();
        var passed = false;
        try
        {
            var dir = Path.GetFullPath(rawDir);
            Directory.CreateDirectory(dir);
            await Task.Delay(1800);
            var window = _windows.FirstOrDefault();
            if (window is null || _vm is null)
            {
                failures.Add("no island window available");
                return;
            }

            window.KeepExpandedForPreview = true;
            _vm.IsExpanded = true;
            await Task.Delay(1900);

            var samples = new List<string>
            {
                "ms,width,height,cardRadius,glassRadius,clipRadius,expectedRadius,radiusOk,fitsWindow"
            };

            void Record(double ms, IslandWindow.CardShapeProbe probe)
            {
                samples.Add($"{ms:0.###},{probe.Width:0.###},{probe.Height:0.###},{probe.Radius:0.###}," +
                            $"{probe.GlassRadius:0.###},{probe.ClipRadius:0.###},{probe.ExpectedRadius:0.###}," +
                            $"{probe.RadiusMatches},{probe.FitsWindow}");
                if (!probe.RadiusMatches)
                    failures.Add($"at {ms:0} ms: radius mismatch ({probe.Detail})");
                if (!probe.FitsWindow)
                    failures.Add($"at {ms:0} ms: card does not fit window ({probe.Detail})");
            }

            var expanded = window.CaptureCardShapeProbe();
            AppLogger.Info($"Collapse probe [expanded]: {expanded.Detail}");
            Record(0, expanded);

            _vm.IsExpanded = false;
            var start = DateTime.UtcNow;
            var nextLogMs = 0.0;
            var frame = 0;
            while ((DateTime.UtcNow - start).TotalMilliseconds < 1500)
            {
                await Task.Delay(16);
                var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
                var probe = window.CaptureCardShapeProbe();
                frame++;
                Record(elapsed, probe);
                if (elapsed >= nextLogMs)
                {
                    AppLogger.Info($"Collapse probe [{elapsed:0} ms #{frame}]: {probe.Detail}");
                    nextLogMs += 100;
                }
            }

            File.WriteAllLines(Path.Combine(dir, "collapse-frames.csv"), samples);
            var final = window.CaptureCardDiagnostic(Path.Combine(dir, "frame-final.png"), "frames-final");
            AppLogger.Info($"Collapse-frame final: {final.Detail}");
            if (!final.RadiusMatches)
                failures.Add($"final: radius mismatch ({final.Detail})");
            if (!final.CornersTransparent)
                failures.Add($"final: corners are not transparent or card is clipped ({final.Detail})");

            passed = failures.Count == 0;
        }
        catch (Exception ex)
        {
            failures.Add("exception: " + ex);
        }
        finally
        {
            foreach (var failure in failures.Take(40))
                AppLogger.Error("Collapse-frame FAIL: " + failure);
            AppLogger.Info(passed
                ? "Collapse-frame diagnostic PASSED: every sampled frame kept a height-derived radius for 1.5 s."
                : $"Collapse-frame diagnostic FAILED: {failures.Count} issue(s).");
            Shutdown(passed ? 0 : 1);
        }
    }
}
