namespace Kes.Tasks;

internal enum TargetMemberArchivePhase
{
    Idle,
    OpeningStatistics,
    CapturingMemberList,
    ClosingStatistics
}

internal sealed class TargetMemberArchiveFlow(TimeSpan stageTimeout)
{
    private readonly object _gate = new();
    private readonly TimeSpan _stageTimeout = stageTimeout >= TimeSpan.Zero
        ? stageTimeout
        : throw new ArgumentOutOfRangeException(nameof(stageTimeout));
    private TargetMemberArchivePhase _phase;
    private DateTimeOffset _stageStartedAt;

    public TargetMemberArchivePhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _phase != TargetMemberArchivePhase.Idle;
            }
        }
    }

    public void BeginOpeningStatistics(DateTimeOffset now)
        => Begin(TargetMemberArchivePhase.OpeningStatistics, now);

    public void BeginCapturingMemberList(DateTimeOffset now)
        => Begin(TargetMemberArchivePhase.CapturingMemberList, now);

    public void BeginClosingStatistics(DateTimeOffset now)
        => Begin(TargetMemberArchivePhase.ClosingStatistics, now);

    public bool HasTimedOut(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _phase != TargetMemberArchivePhase.Idle
                   && now - _stageStartedAt >= _stageTimeout;
        }
    }

    public TimeSpan Elapsed(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _phase == TargetMemberArchivePhase.Idle
                ? TimeSpan.Zero
                : now - _stageStartedAt;
        }
    }

    public void Complete() => SetIdle();

    public void Fail() => SetIdle();

    private void Begin(TargetMemberArchivePhase phase, DateTimeOffset now)
    {
        lock (_gate)
        {
            _phase = phase;
            _stageStartedAt = now;
        }
    }

    private void SetIdle()
    {
        lock (_gate)
        {
            _phase = TargetMemberArchivePhase.Idle;
        }
    }
}