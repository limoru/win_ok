namespace Automation.Core.Arbitration;

public enum PageActionObservationKind
{
    SourcePageStillPresent,
    ConfirmationObserved,
    Completed
}

public sealed class PageActionSession
{
    private readonly string _sessionId;

    public PageActionSession(
        string sessionId,
        string actionKey,
        PageObservation observation,
        string? sourceRouteId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKey);
        ArgumentNullException.ThrowIfNull(observation);

        _sessionId = sessionId;
        ActionKey = actionKey;
        SourceRouteId = string.IsNullOrWhiteSpace(sourceRouteId)
            ? observation.PrimaryRouteId ?? actionKey
            : sourceRouteId;
        SourceFingerprint = observation.Fingerprint;
    }

    public string ActionKey { get; }
    public string SourceRouteId { get; }
    public PageFingerprint SourceFingerprint { get; }
    public int AttemptCount { get; private set; }
    public long LastCommandFrameSequence { get; private set; } = -1;
    public int RecoveryCount { get; private set; }
    public long LastRecoveryFrameSequence { get; private set; } = -1;

    public PageActionObservationKind Observe(
        PageObservation observation,
        string? currentActionKey)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!string.Equals(observation.SessionId, _sessionId, StringComparison.Ordinal))
        {
            return PageActionObservationKind.Completed;
        }

        if (observation.HasCenterConfirmation)
        {
            return PageActionObservationKind.ConfirmationObserved;
        }

        if (!string.Equals(currentActionKey, ActionKey, StringComparison.Ordinal))
        {
            return PageActionObservationKind.Completed;
        }

        return PageActionObservationKind.SourcePageStillPresent;
    }

    public void MarkCommandSent(long frameSequence)
    {
        AttemptCount++;
        LastCommandFrameSequence = Math.Max(LastCommandFrameSequence, frameSequence);
    }

    public bool CanRetryAfter(long frameSequence, int retryAfterFrames, int maxAttempts)
    {
        if (retryAfterFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterFrames));
        }

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        return AttemptCount > 0
               && AttemptCount < maxAttempts
               && LastCommandFrameSequence >= 0
               && frameSequence >= LastCommandFrameSequence
               && frameSequence - LastCommandFrameSequence >= retryAfterFrames;
    }

    public bool HasReachedAttemptLimit(int maxAttempts)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        return AttemptCount >= maxAttempts;
    }

    public bool CanRecoverAfter(long frameSequence, int recoveryAfterFrames)
    {
        if (recoveryAfterFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryAfterFrames));
        }

        var anchorFrameSequence = Math.Max(
            LastCommandFrameSequence,
            LastRecoveryFrameSequence);
        return LastCommandFrameSequence >= 0
               && frameSequence >= anchorFrameSequence
               && frameSequence - anchorFrameSequence >= recoveryAfterFrames;
    }

    public void MarkRecoveryDecision(long frameSequence)
    {
        RecoveryCount++;
        LastRecoveryFrameSequence = Math.Max(LastRecoveryFrameSequence, frameSequence);
    }

    public bool CanCancelAfterUnhandledAttempt => AttemptCount == 0;
}
