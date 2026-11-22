using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinIslands.Services;

/// <summary>检测到的新复制文本。仅用于“已复制”轻提示，不保留历史。</summary>
public sealed record ClipboardText(DateTime Time, string Text);

/// <summary>
/// 最小剪贴板监听器：按 Windows 剪贴板序列号检测文本复制，只发出一次性事件。
/// 不读取历史、不持久化、不保留复制内容。
/// </summary>
public sealed class ClipboardCopyMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly object _gate = new();
    private string _last = string.Empty;
    private uint _lastClipboardSequence = uint.MaxValue;
    private bool _polling;
    private string _lastPollError = string.Empty;
    private DateTime _lastPollErrorLogUtc = DateTime.MinValue;
    private static readonly TimeSpan PollErrorLogInterval = TimeSpan.FromMinutes(5);

    public ClipboardCopyMonitor()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>检测到新复制的文本（每次复制只触发一次）。</summary>
    public event Action<ClipboardText>? EntryAdded;

    /// <summary>开启/关闭复制检测。开启时先建立基线，避免把启动前已有内容当成新复制。</summary>
    public void SetPolling(bool polling)
    {
        lock (_gate) _polling = polling;
        if (polling)
        {
            BaselineClipboard();
            if (!_timer.IsEnabled) _timer.Start();
        }
        else if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// 启动基线：当前剪贴板内容只记作“已知”，不弹提示。之后真实复制仍会正常触发。
    /// </summary>
    private void BaselineClipboard()
    {
        try
        {
            var text = System.Windows.Clipboard.ContainsText()
                ? (System.Windows.Clipboard.GetText() ?? string.Empty)
                : string.Empty;
            var baseline = ComputeBaseline(_last, text);
            lock (_gate)
            {
                if (baseline is not null) _last = baseline;
                _lastClipboardSequence = GetClipboardSequenceNumber();
            }
        }
        catch
        {
            // 剪贴板被其它进程占用时忽略；首次真实复制仍会触发。
        }
    }

    internal static string? ComputeBaseline(string currentLast, string clipboardText)
    {
        if (currentLast.Length > 0) return null;
        if (string.IsNullOrWhiteSpace(clipboardText) || clipboardText.Length > 20000) return null;
        return clipboardText;
    }

    public void CopyToClipboard(string text)
    {
        try
        {
            var value = text ?? string.Empty;
            System.Windows.Clipboard.SetText(value);
            lock (_gate)
            {
                _last = value;
                _lastClipboardSequence = GetClipboardSequenceNumber();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Clipboard set failed: {ex.Message}");
        }
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (!_polling) return;
        }

        var currentSequence = GetClipboardSequenceNumber();
        uint lastSequence;
        lock (_gate) lastSequence = _lastClipboardSequence;
        if (!ShouldReadClipboard(lastSequence, currentSequence)) return;
        if (currentSequence != 0)
        {
            lock (_gate) _lastClipboardSequence = currentSequence;
        }

        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                ResetPollError();
                return;
            }

            var text = System.Windows.Clipboard.GetText();
            ResetPollError();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 20000) return;

            lock (_gate)
            {
                if (text == _last) return;
                _last = text;
            }

            EntryAdded?.Invoke(new ClipboardText(DateTime.Now, text));
        }
        catch (Exception ex)
        {
            ReportPollError(ex);
        }
    }

    internal static bool ShouldReadClipboard(uint lastSequence, uint currentSequence)
        => currentSequence == 0 || currentSequence != lastSequence;

    private void ResetPollError()
    {
        _lastPollError = string.Empty;
        _lastPollErrorLogUtc = DateTime.MinValue;
    }

    private void ReportPollError(Exception ex)
    {
        var detail = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : $"{ex.GetType().Name}: {ex.Message}";
        var now = DateTime.UtcNow;
        if (!ShouldLogPollError(_lastPollError, _lastPollErrorLogUtc, now, detail, PollErrorLogInterval)) return;
        _lastPollError = detail;
        _lastPollErrorLogUtc = now;
        AppLogger.Debug($"Clipboard poll unavailable: {detail}");
    }

    internal static bool ShouldLogPollError(string lastError, DateTime lastLoggedUtc, DateTime nowUtc,
        string currentError, TimeSpan interval)
    {
        if (!string.Equals(lastError, currentError, StringComparison.Ordinal)) return true;
        if (lastLoggedUtc == DateTime.MinValue) return true;
        return nowUtc - lastLoggedUtc >= interval;
    }

    public void Dispose() => _timer.Stop();

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
