namespace Kes.Tasks;

public enum BattleEventPhase
{
    Idle,
    EventSelected,
    StartAttackRequested
}

public enum BattleEventPageDecision
{
    ProcessNormally,
    WaitForTransition,
    RetryEventSelection
}

/// <summary>
/// Tracks the cross-frame transition from an event battle option to the battle preview.
/// The preview can briefly expose the previous event page to the frame bus.
/// </summary>
public sealed class BattleEventFlow
{
    public static readonly TimeSpan DefaultTransitionTimeout = TimeSpan.FromSeconds(8);

    private readonly TimeSpan _transitionTimeout;
    private DateTimeOffset _phaseStartedAt;

    public BattleEventFlow(TimeSpan? transitionTimeout = null)
    {
        _transitionTimeout = transitionTimeout ?? DefaultTransitionTimeout;
        if (_transitionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transitionTimeout),
                "战斗事件切换超时必须大于零。");
        }
    }

    public BattleEventPhase Phase { get; private set; }

    public bool IsTransitioning => Phase is BattleEventPhase.EventSelected
        or BattleEventPhase.StartAttackRequested;

    public BattleEventPageDecision ObserveEventPage(DateTimeOffset observedAt)
    {
        if (!IsTransitioning)
        {
            return BattleEventPageDecision.ProcessNormally;
        }

        observedAt = ResolveTimestamp(observedAt);
        if (observedAt - _phaseStartedAt < _transitionTimeout)
        {
            return BattleEventPageDecision.WaitForTransition;
        }

        Reset();
        return BattleEventPageDecision.RetryEventSelection;
    }

    public void MarkEventSelected(DateTimeOffset selectedAt)
        => SetPhase(BattleEventPhase.EventSelected, selectedAt);

    public bool TryBeginStartAttack(DateTimeOffset requestedAt)
    {
        if (Phase is BattleEventPhase.StartAttackRequested)
        {
            return false;
        }

        SetPhase(BattleEventPhase.StartAttackRequested, requestedAt);
        return true;
    }

    public void Complete()
        => Reset();

    private void SetPhase(BattleEventPhase phase, DateTimeOffset startedAt)
    {
        Phase = phase;
        _phaseStartedAt = ResolveTimestamp(startedAt);
    }

    private void Reset()
    {
        Phase = BattleEventPhase.Idle;
        _phaseStartedAt = default;
    }

    private static DateTimeOffset ResolveTimestamp(DateTimeOffset timestamp)
        => timestamp == default ? DateTimeOffset.UtcNow : timestamp;
}
