using System.Collections.ObjectModel;
using WinIslands.UI;

namespace WinIslands.Services;

/// <summary>
/// 通知历史服务：内存中维护最近 N 条通知记录，支持标记已读、一键全部已读、删除。
/// 可被单元测试覆盖（纯逻辑，不依赖 Windows API）。
/// </summary>
public sealed class NotificationHistoryService : IDisposable
{
    private readonly ObservableCollection<EventHistoryItem> _entries = new();
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>当前历史记录列表（只读视图）。</summary>
    public IReadOnlyList<EventHistoryItem> Entries
    {
        get
        {
            lock (_gate) { return _entries.ToList(); }
        }
    }

    /// <summary>最大保留条数（超出时自动裁剪尾部旧记录）。</summary>
    public int MaxEntries { get; set; } = 20;

    /// <summary>添加一条通知到历史记录（插入头部）。</summary>
    public void Add(string title, string body, string icon, string? source = null)
    {
        lock (_gate)
        {
            _entries.Insert(0, new EventHistoryItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Body = body,
                Icon = icon,
                TimeUtc = DateTime.UtcNow,
                Source = source ?? string.Empty,
                Read = false,
            });

            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(_entries.Count - 1);
        }
    }

    /// <summary>标记标题+正文完全匹配的记录为已读。</summary>
    public void MarkReadMatching(string title, string body)
    {
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (e.Title == title && e.Body == body)
                    e.Read = true;
            }
        }
    }

    /// <summary>全部标记为已读。</summary>
    public void MarkAllRead()
    {
        lock (_gate)
        {
            foreach (var e in _entries)
                e.Read = true;
        }
    }

    /// <summary>移除指定记录。</summary>
    public void Remove(EventHistoryItem item)
    {
        lock (_gate)
        {
            // 通过 Id 比较，因为集合中的引用可能不同
            var idx = _entries.ToList().FindIndex(e => e.Id == item.Id);
            if (idx >= 0) _entries.RemoveAt(idx);
        }
    }

    /// <summary>清空全部历史。</summary>
    public void Clear()
    {
        lock (_gate) { _entries.Clear(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _entries.Clear(); }
    }
}
