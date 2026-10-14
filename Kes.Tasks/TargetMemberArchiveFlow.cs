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
    private readonly TimeSpan _stageTimeout = stageTimeout >= TimeSpan.Zero
        ? stageTimeout
        : throw new ArgumentOutOfRangeException(nameof(stageTimeout));
    private DateTimeOffset _stageStartedAt;

    public TargetMemberArchivePhase Phase { get; private set; }

    public bool IsActive => Phase != TargetMemberArchivePhase.Idle;

    public void BeginOpeningStatistics(DateTimeOffset now)
    {
        Phase = TargetMemberArchivePhase.OpeningStatistics;
        _stageStartedAt = now;
    }

    public void BeginCapturingMemberList(DateTimeOffset now)
    {
        Phase = TargetMemberArchivePhase.CapturingMemberList;
        _stageStartedAt = now;
    }

    public void BeginClosingStatistics(DateTimeOffset now)
    {
        Phase = TargetMemberArchivePhase.ClosingStatistics;
        _stageStartedAt = now;
    }

    public bool HasTimedOut(DateTimeOffset now)
        => IsActive && now - _stageStartedAt >= _stageTimeout;

    public TimeSpan Elapsed(DateTimeOffset now)
        => IsActive ? now - _stageStartedAt : TimeSpan.Zero;

    public void Complete()
        => Phase = TargetMemberArchivePhase.Idle;

    public void Fail()
        => Phase = TargetMemberArchivePhase.Idle;
}