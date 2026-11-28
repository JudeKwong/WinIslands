using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using WinIslands.Services;

namespace WinIslands.UI;

public sealed partial class IslandViewModel : ObservableObject, IDisposable
{
    // ── Coordinator events ─────────────────────────────────────
    private void OnSnapshotChanged(object? sender, MediaSnapshot snapshot)
    {
        // README screenshot / manual preview: keep the injected demo track stable even when
        // a real SMTC or window-title session appears in the background.
        if (_demoInjected && !ReferenceEquals(sender, this)) return;
        // 用「歌手+歌名+专辑」判断换曲，而不是整条 TrackInfo 结构相等：
        // 封面 URL 等字段抖动不应触发换曲（否则会重置进度并把歌词打回开头）。
        var previous = _snapshot;
        var trackChanged = _snapshot is null ||
            LyricsService.TrackKey(_snapshot.Track) != LyricsService.TrackKey(snapshot.Track);
        var firstTrack = _snapshot is null;
        var durationChanged = previous is null || Math.Abs(previous.DurationSeconds - snapshot.DurationSeconds) >= 0.01;
        var volumeChanged = previous is null || previous.HasVolumeControl != snapshot.HasVolumeControl || !Nullable.Equals(previous.Volume, snapshot.Volume);
        _snapshot = snapshot;
        if (firstTrack && !_progressTimer.IsEnabled) _progressTimer.Start(); // 有媒体才跑进度插值，空闲停用
        if (trackChanged && !firstTrack && !string.IsNullOrEmpty(snapshot.Track.Title))
            NowPlayingRequested?.Invoke(snapshot.Track.Title, snapshot.Track.Artist);

        Title = snapshot.Track.Title;
        Artist = snapshot.Track.Artist;
        Album = snapshot.Track.Album;
        SourceLabel = snapshot.SourceLabel;
        SourceDetail = snapshot.Track.SourceAppName;
        var prevStatus = Status;
        if (trackChanged) { _statusOverrideActive = false; _pauseLock = false; } // 换曲后上一状态锁定作废
        if (_pauseLock)
        {
            // 暂停锁定（2.1.5 粘性）：用户点击暂停 / 重启恢复为暂停后，本地保持暂停，
            // 进度与歌词完全冻结，直到用户再次点击播放 / 换曲 / 媒体结束。
            // 期间完全忽略快照上报的状态——Cider SMTC 在暂停时常持续误报 Playing，
            // 旧实现先走乐观保护分支，8s 超时后会把本地状态打回 Playing，
            // 导致“暂停后歌词继续往后走”。现在锁定优先，误报不再能解冻播放。
        }
        else if (_statusOverrideActive)
        {
            // 乐观状态保护期：直到快照确认目标状态或超时，否则保持按钮状态，不被快照打回
            if (snapshot.Status == _optimisticStatus || DateTime.UtcNow > _statusOverrideUntilUtc)
            {
                _statusOverrideActive = false;
                Status = snapshot.Status;
            }
        }
        else
        {
            Status = snapshot.Status;
        }
        DurationSeconds = snapshot.DurationSeconds;
        // 暂停时保存一次位置（崩溃/退出后可恢复）
        if (Status == PlaybackStatus.Paused && prevStatus != PlaybackStatus.Paused) SavePlaybackState();
        if (durationChanged) OnPropertyChanged(nameof(DurationText));
        var hasRealPosition = snapshot.DurationSeconds > 0 || snapshot.PositionSeconds > 0;
        var reported = Math.Max(0, snapshot.PositionSeconds);
        if (snapshot.DurationSeconds > 0) reported = Math.Min(reported, snapshot.DurationSeconds); // 防御：上报值不越界
        if (trackChanged)
        {
            _restoredMode = false;
            // 启动恢复：Cider/SMTC 尚未返回真实位置时，用上次保存的位置作为初始值，
            // 避免暂停后重启先显示第 0 行、等真实位置到了再“跳”到暂停句。
            // 只要曲目匹配且有上次位置就恢复（即使第一帧带了时长但位置为 0，也以恢复位置为准，
            // 之后位置守卫会按真实上报值平滑校正，避免先显示第 0 行再跳）
            var restored = _restoredTrackKey is not null
                && LyricsService.TrackKey(snapshot.Track) == _restoredTrackKey
                && _restoredPosition > 0;
            if (restored)
            {
                _restoredMode = true; // 信任恢复位置，直到收到真实前进位置/换曲/seek
                _interpolatedPosition = _restoredPosition;
                if (_restoredStatus == PlaybackStatus.Paused)
                {
                    // 上次是暂停：锁定为暂停，重启后歌词保持不动（Cider SMTC 常误报 Playing）
                    _pauseLock = true;
                    SetStatusLocal(PlaybackStatus.Paused);
                }
                if (snapshot.DurationSeconds > 0) _interpolatedPosition = Math.Min(_interpolatedPosition, snapshot.DurationSeconds);
                _trackStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(_interpolatedPosition);
            }
            else
            {
                _useFreeClock = !hasRealPosition;
                _trackStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(reported);
                _interpolatedPosition = reported;
            }
            _restoredTrackKey = null; // 只恢复一次
            _positionStaleSinceUtc = null;
            _karaokeFrozen = false;
            SavePlaybackState();
        }
        else if (_pauseLock)
        {
            // 暂停锁定期间：位置保持冻结，不采纳快照位置（避免歌词/进度在暂停时继续走）
        }
        else if (!ShouldFreezePosition(Status) && hasRealPosition)
        {
            // 暂停期间绝不采纳播放器上报位置：即使 _pauseLock 未启用（例如从播放器端暂停），
            // 位置与歌词高亮也冻结在暂停时刻，防止 Cider/SMTC 上报轻微漂移或过期回退
            // 超时把歌词推着往后走（2.1.5）。
            var current = _interpolatedPosition;
            var seeking = _suppressSeek > 0; // 用户正在拖拽进度条
            if (ShouldAdoptReportedPosition(reported, current, seeking))
            {
                _restoredMode = false; // 已收到真实前进位置，恢复正常守卫
                _useFreeClock = false;
                _trackStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(reported);
                _interpolatedPosition = reported;
                _positionStaleSinceUtc = null;
            }
            else if (_restoredMode)
            {
                // 启动恢复信任期：持续报 0/过期位置（如 Cider SMTC）也保持恢复的位置，不触发回跳
            }
            else
            {
                // 忽略瞬间回退，保持当前插值进度继续推进，避免歌词/进度条突然跳回开头；
                // 若明显回退持续超过 ~4 秒（真正的重播或播放器端 seek），再采纳并回跳。
                if (_positionStaleSinceUtc is null) _positionStaleSinceUtc = DateTime.UtcNow;
                else if ((DateTime.UtcNow - _positionStaleSinceUtc.Value).TotalSeconds >= 4.0)
                {
                    _useFreeClock = false;
                    _trackStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(reported);
                    _interpolatedPosition = reported;
                    _positionStaleSinceUtc = null;
                }
            }
        }
        // 无真实进度且非新曲目：保持自由时钟继续推进（不重置）
        _lastPositionTime = DateTime.UtcNow;
        CanPlayPause = snapshot.CanPlayPause;
        CanNext = snapshot.CanNext;
        CanPrevious = snapshot.CanPrevious;
        CanSeek = snapshot.CanSeek && snapshot.DurationSeconds > 0;
        CanShuffle = snapshot.CanShuffle;
        CanRepeat = snapshot.CanRepeat;
        ShuffleActive = snapshot.ShuffleActive == true;
        RepeatMode = snapshot.RepeatMode;
        HasVolumeControl = snapshot.HasVolumeControl;
        _suppressVolume = true;
        Volume = snapshot.Volume ?? 0;
        _suppressVolume = false;
        var statusChanged = prevStatus != Status;
        if (statusChanged && prevStatus != PlaybackStatus.Playing && Status == PlaybackStatus.Playing) OnResumePlaying();
        if (statusChanged) _wave.SetPlaying(IsPlaying);
        if (volumeChanged) OnPropertyChanged(nameof(VolumeText));

        if (snapshot.Track.ArtworkPath.Length > 0)
            Artwork = GetArtwork(snapshot.Track.ArtworkPath);
        else
            Artwork = null;

        if (trackChanged)
            _ = LoadLyricsAsync(snapshot);

        if (statusChanged)
        {
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PlayPauseGlyph));
        }
        OnPropertyChanged(nameof(Position));
        if (trackChanged) OnPropertyChanged(nameof(HasLyrics));
        if (trackChanged || statusChanged || previous?.Source != snapshot.Source) UpdateVisibility();
    }

    /// <summary>
    /// 当前状态是否应冻结进度/歌词位置（不采纳播放器上报的位置）。
    /// 暂停时恒为 true：进度与歌词高亮必须停在暂停时刻，播放器（Cider/SMTC）上报的
    /// 轻微漂移或过期读数都不得把歌词推着往后走（2.1.5）。纯函数，便于单元测试。
    /// </summary>
    internal static bool ShouldFreezePosition(PlaybackStatus status)
        => status == PlaybackStatus.Paused;

    /// <summary>
    /// 判断是否应立即采用播放器上报的位置（秒）。
    /// 仅当位置前进 / 轻微回退（≤2s）/ 用户正在拖拽进度条时才立即采用；
    /// 播放到中途时瞬间上报 ~0 视为过期读数（如 Cider/SMTC 偶发返回 0），
    /// 返回 false，由调用方保持当前插值进度继续推进，避免歌词/进度条突然跳回开头。
    /// </summary>
    internal static bool ShouldAdoptReportedPosition(double reported, double current, bool seeking)
    {
        var sane = seeking || reported >= current - 2.0;
        var staleZero = !seeking && reported < 1.0 && current > 10.0;
        return sane && !staleZero;
    }
    private void OnMediaEnded(object? sender, EventArgs e)
    {
        _snapshot = null;
        _progressTimer.Stop(); // 无媒体时空闲停用，避免 100ms 空转
        _restoredMode = false;
        _statusOverrideActive = false;
        _pauseLock = false;
        Title = string.Empty;
        Artist = string.Empty;
        Album = string.Empty;
        Artwork = null;
        Status = PlaybackStatus.Closed;
        DurationSeconds = 0;
        Position = TimeSpan.Zero;
        Progress = 0;
        _interpolatedPosition = 0;
        _positionStaleSinceUtc = null;
        _wave.SetPlaying(false);
        LyricLines = Array.Empty<LyricLineViewModel>();
        _lyrics = LyricsResult.Empty;
        LyricIndex = -1;
        CurrentLyricText = string.Empty;
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PlayPauseGlyph));
        OnPropertyChanged(nameof(HasLyrics));
        UpdateVisibility();
    }

    private async Task LoadLyricsAsync(MediaSnapshot snapshot)
    {
        var key = LyricsService.TrackKey(snapshot.Track);
        if (key == _lyricsKey) return;
        _lyricsKey = key;

        // 切歌立即清空旧歌词，避免新歌词加载完成前旧歌词继续显示/闪回
        LyricLines = Array.Empty<LyricLineViewModel>();
        LyricIndex = -1;
        CurrentLyricWords = Array.Empty<TtmlWord>();
        CurrentLyricText = string.Empty;
        OnPropertyChanged(nameof(HasLyrics));

        var result = await _lyricsService.GetLyricsAsync(snapshot);
        if (_lyricsKey != key) return; // track changed while loading
        _lyrics = result;

        if (result.Ttml is { IsEmpty: false })
        {
            // Apple Music TTML：直接采用解析器输出的行与逐词时间轴（含空行 / 对唱 / 背景和声）
            LyricLines = result.Ttml.Lines.Select(line => new LyricLineViewModel(line)).ToList();
        }
        else if (_settings.Current.BilingualLyrics)
        {
            // 双语 LRC：相邻时间戳的翻译行合并到主句下方；结束时间取下一句开始
            var pairs = LrcParser.PairLines(result.Document.Lines, TimeSpan.FromMilliseconds(250), enable: true);
            LyricLines = BuildLrcLines(pairs.Select(p => (p.Main, p.Translation)));
        }
        else
        {
            var lines = result.Document.Lines;
            LyricLines = BuildLrcLines(lines.Select(l => (l, (string?)null)));
        }

        RefreshLyricTranslationVisibility();

        if (LyricLines.Count > 0)
        {
            // 直接按当前（已恢复的）位置定位当前句，避免启动瞬间先显示第 0 行再跳；
            // #4：叠加用户校准的时间偏移
            var idx = IndexAtPosition(
                LyricsAdjustedPosition(TimeSpan.FromSeconds(KaraokeMath.AtLeastZero(_interpolatedPosition))).TotalSeconds);
            LyricIndex = idx;
            if (idx >= 0 && idx < LyricLines.Count)
            {
                CurrentLyricWords = LyricLines[idx].Words;
                CurrentLyricText = LyricLines[idx].Text;
            }
        }
        else
        {
            LyricIndex = -1;
            CurrentLyricWords = Array.Empty<TtmlWord>();
            CurrentLyricText = string.Empty;
        }

        LyricsStatus = result.Source switch
        {
            LyricsSourceKind.LocalFile => Localization.Get("Lyrics_Local"),
            LyricsSourceKind.Cider => Localization.Get("Lyrics_FromCider"),
            LyricsSourceKind.Online => Localization.Get("Lyrics_Online"),
            LyricsSourceKind.AmllTtml => Localization.Get("Lyrics_Amll"),
            _ => Localization.Get("LyricsUnavailable"),
        };
        OnPropertyChanged(nameof(HasLyrics));
        OnPropertyChanged(nameof(LyricOffsetText)); // 本曲可能带已保存的偏移
    }

    /// <summary>LRC 行 → ViewModel：用下一句开始时间补齐 EndSec，供均分逐字时间轴。</summary>
    private static IReadOnlyList<LyricLineViewModel> BuildLrcLines(
        IEnumerable<(LyricLine Line, string? Translation)> entries)
    {
        var source = entries.ToList();
        var result = new List<LyricLineViewModel>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            var start = Math.Max(0.0, source[i].Line.Time.TotalSeconds);
            var end = i + 1 < source.Count
                ? source[i + 1].Line.Time.TotalSeconds
                : start + 4.0;
            if (!double.IsFinite(end) || end <= start) end = start + 4.0;
            result.Add(new LyricLineViewModel(source[i].Line, source[i].Translation, null, end));
        }
        return result;
    }

    /// <summary>按播放秒定位当前歌词行（找不到返回 -1）。同一时刻优先主歌词，避免背景和声抢行。</summary>
    private int IndexAtPosition(double seconds)
    {
        if (LyricLines.Count == 0 || !double.IsFinite(seconds)) return -1;
        var idx = -1;
        for (var i = 0; i < LyricLines.Count; i++)
        {
            if (LyricLines[i].StartSec > seconds + 0.0005) break;
            idx = i;
        }
        if (idx < 0) return -1;
        // TTML 中主歌词和背景和声可能共享同一开始时间：优先返回主歌词行
        if (LyricLines[idx].IsBackground)
        {
            var start = LyricLines[idx].StartSec;
            for (var i = idx - 1; i >= 0; i--)
            {
                if (Math.Abs(LyricLines[i].StartSec - start) > 0.05) break;
                if (!LyricLines[i].IsBackground) return i;
            }
        }
        return idx;
    }
    // #4 ── 歌词时间微调 ────────────────────────────────────────
    /// <summary>当前曲目的歌词时间偏移（秒），未校准为 0。</summary>
    public double CurrentLyricOffset
    {
        get => _lyricsKey.Length > 0 && _lyricTimeOffsets.TryGetValue(_lyricsKey, out var v) ? v : 0;
    }

    /// <summary>歌词对齐偏移的显示文本（按钮上显示）。</summary>
    public string LyricOffsetText
    {
        get
        {
            var off = CurrentLyricOffset;
            if (Math.Abs(off) < 0.001) return Localization.Get("Lyric_Aligned");
            return off > 0 ? $"+{off:0.0}s" : $"{off:0.0}s";
        }
    }

    /// <summary>把播放位置叠加歌词偏移后用于歌词定位/卡拉OK。</summary>
    private TimeSpan LyricsAdjustedPosition(TimeSpan pos)
        => pos + TimeSpan.FromSeconds(CurrentLyricOffset);

    /// <summary>微调当前曲目的歌词时间：delta 秒（±0.5 步进），立即生效并持久化到设置。</summary>
    public void AdjustLyricTime(double delta)
    {
        if (_lyricsKey.Length == 0) return;
        try
        {
            var off = Math.Clamp(CurrentLyricOffset + delta, -30, 30);
            _lyricTimeOffsets[_lyricsKey] = off;
            _settings.Update(s => s.LyricTimeOffsets = new Dictionary<string, double>(_lyricTimeOffsets));
            OnPropertyChanged(nameof(LyricOffsetText));
            if (HasLyrics && LyricLines.Count > 0)
            {
                var idx = IndexAtPosition(LyricsAdjustedPosition(Position).TotalSeconds);
                if (idx != LyricIndex) LyricIndex = idx;
                UpdateKaraokeHighlight();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Lyric time adjust failed: {ex.Message}");
        }
    }

    // ── Progress interpolation ─────────────────────────────────
    private void AdvanceProgress()
    {
        if (_snapshot is null || _suppressSeek > 0) return;

        if (Status == PlaybackStatus.Playing)
        {
            var now = DateTime.UtcNow;
            if (_useFreeClock)
            {
                // 播放器不报 SMTC 进度（如 Cider）：用本地时钟从曲目开始推进卡拉OK
                _interpolatedPosition = (now - _trackStartTime).TotalSeconds;
            }
            else
            {
                _interpolatedPosition += (now - _lastPositionTime).TotalSeconds;
            }
            _lastPositionTime = now;
        }

        // 时长不可用（Cider）时用兜底时长，保证进度/卡拉OK仍推进
        var duration = DurationSeconds > 0 ? DurationSeconds : 300.0;
        Position = TimeSpan.FromSeconds(KaraokeMath.AtLeastZero(_interpolatedPosition));
        Progress = KaraokeMath.ClampFraction(_interpolatedPosition / duration);

        if (HasLyrics)
        {
            var idx = IndexAtPosition(LyricsAdjustedPosition(Position).TotalSeconds); // #4 叠加歌词偏移
            if (idx != LyricIndex) LyricIndex = idx;
            UpdateKaraokeHighlight();
        }
    }

    /// <summary>逐字卡拉OK：把当前句的时长按字符均分，推进已点亮字符数。</summary>
    private void UpdateKaraokeHighlight()
    {
        if (LyricIndex < 0 || LyricIndex >= LyricLines.Count) return;
        var cur = LyricLines[LyricIndex];
        var nextStart = LyricIndex + 1 < LyricLines.Count
            ? LyricLines[LyricIndex + 1].StartSec
            : Math.Max(cur.EndSec, cur.StartSec + 0.1);
        var posSec = Position.TotalSeconds + CurrentLyricOffset; // #4 叠加歌词偏移
        var frac = KaraokeMath.LineFraction(posSec, cur.StartSec, nextStart);

        if (Status == PlaybackStatus.Playing)
        {
            // 播放中：实时推进（连续比例 0..1，供控件 60fps 缓动）
            _karaokeFrozen = false;
            KaraokePositionSeconds = posSec; // 播放中持续更新逐字位置（控件按墙钟 60fps 推进）
            SetHighlightFraction(frac);
        }
        else if (!_karaokeFrozen)
        {
            // 暂停：用当前（已正确恢复的）位置设置一次高亮，然后冻结，
            // 之后任何位置校正都不再改动高亮 → 稳定在暂停时刻的样子。
            KaraokePositionSeconds = posSec; // 播放中持续更新逐字位置（控件按墙钟 60fps 推进）
            SetHighlightFraction(frac);
            _karaokeFrozen = true;
        }
        // 已冻结：保持不动（既不更新比例也不更新逐字位置，避免暂停时跳动）

    }

    private void SetHighlightFraction(double frac)
    {
        if (LyricLines.Count > LyricIndex)
        {
            var lvm = LyricLines[LyricIndex];
            if (KaraokeMath.AbsGreaterThan(lvm.HighlightFraction - frac, 0.0005)) lvm.HighlightFraction = frac;
        }
        if (KaraokeMath.AbsGreaterThan(CompactHighlightFraction - frac, 0.0005)) CompactHighlightFraction = frac;
    }

    /// <summary>保存当前播放位置（退出/暂停/切歌时调用，供下次启动恢复）。</summary>
    public void SavePlaybackState()
    {
        try
        {
            // Demo previews must not overwrite the user's real playback resume state.
            if (_snapshot is null || _demoInjected) return;
            new PlaybackStateStore
            {
                TrackKey = LyricsService.TrackKey(_snapshot.Track),
                PositionSeconds = KaraokeMath.AtLeastZero(_interpolatedPosition),
                Status = Status.ToString(),
            }.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SavePlaybackState failed: {ex.Message}");
        }
    }
}
