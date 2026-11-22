namespace WinIslands.UI;

/// <summary>灵动岛卡片的两种稳定形态。</summary>
public enum IslandMotionState
{
    Compact,
    Expanded,
}

/// <summary>
/// 一次展开/收起请求的凭证。只有「当前代数」的凭证允许提交最终几何，
/// 旧动画迟到的完成回调会被直接丢弃。
/// </summary>
public readonly record struct IslandMotionTicket(long Generation, IslandMotionState Target)
{
    public static readonly IslandMotionTicket None = new(0, IslandMotionState.Compact);
    public bool IsValid => Generation > 0;
}

/// <summary>
/// 展开/收起动画的唯一状态权威（3.1.7）。
///
/// 背景：卡片尺寸、圆角、裁剪此前由 Storyboard、弹簧、SizeChanged、延迟 Reconcile
/// 多处写入。快速连续展开/收起时，旧动画的完成回调可能在新动画之后到达，把几何写成
/// 旧目标，表现为「收回了但还是展开尺寸」或「四角变直角」。
///
/// 该状态机把这类竞争收敛为代数比较：每次 Begin 递增代数；只有最新代数可以 Settle。
/// 不依赖 WPF，可直接单元测试。
/// </summary>
public sealed class IslandMotionStateMachine
{
    private long _generation;
    private IslandMotionState _target = IslandMotionState.Compact;
    private IslandMotionState _settled = IslandMotionState.Compact;
    private bool _inFlight;

    public long Generation => _generation;
    public IslandMotionState Target => _target;
    public IslandMotionState Settled => _settled;
    public bool InFlight => _inFlight;

    public static IslandMotionState StateOf(bool expanded) =>
        expanded ? IslandMotionState.Expanded : IslandMotionState.Compact;

    /// <summary>开始一次新的目标形态。返回的凭证是本次请求唯一的提交许可。</summary>
    public IslandMotionTicket Begin(IslandMotionState target)
    {
        _target = target;
        _generation++;
        _inFlight = true;
        return new IslandMotionTicket(_generation, target);
    }

    public IslandMotionTicket Begin(bool expanded) => Begin(StateOf(expanded));

    /// <summary>该凭证是否仍是当前请求（旧动画的迟到回调应返回 false）。</summary>
    public bool IsCurrent(in IslandMotionTicket ticket) =>
        ticket.IsValid && ticket.Generation == _generation;

    /// <summary>
    /// 尝试用凭证提交最终形态。只有当前凭证能成功；
    /// 幂等：重复 Settle 同一凭证返回 true 但不会改变代数。
    /// </summary>
    public bool TrySettle(in IslandMotionTicket ticket)
    {
        if (!IsCurrent(ticket)) return false;
        _inFlight = false;
        _settled = ticket.Target;
        return true;
    }

    /// <summary>取消进行中的请求（窗口关闭 / 关闭动画时使用），后续旧回调全部失效。</summary>
    public void Cancel()
    {
        _generation++;
        _inFlight = false;
        _target = _settled;
    }

    /// <summary>强制重置（用于直接写尺寸、无动画的路径）。</summary>
    public void Reset(IslandMotionState state)
    {
        _generation++;
        _settled = state;
        _target = state;
        _inFlight = false;
    }

    public void Reset(bool expanded) => Reset(StateOf(expanded));
}
