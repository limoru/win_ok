namespace Automation.Core.Arbitration;

public enum PageActionObservationKind
{
    SourcePageStillPresent,
    ConfirmationObserved,
    RetryAvailable,
    Completed
}

public sealed class PageActionSession
{
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);
    private const int MaxAttempts = 2;

    private readonly string _sessionId;
    private readonly DateTimeOffset _startedAt;
    private readonly TimeSpan _retryAfter;
    private DateTimeOffset _lastAttemptAt;
    private bool _confirmationObserved;
    private bool _retryAvailable;

    public PageActionSession(
        string sessionId,
        string actionKey,
        PageObservation observation,
        TimeSpan? retryAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKey);
        ArgumentNullException.ThrowIfNull(observation);

        _sessionId = sessionId;
        ActionKey = actionKey;
        SourceFingerprint = observation.Fingerprint;
        _startedAt = ResolveTimestamp(observation);
        _lastAttemptAt = _startedAt;
        _retryAfter = retryAfter ?? DefaultRetryAfter;
    }

    public string ActionKey { get; }
    public PageFingerprint SourceFingerprint { get; }
    public int AttemptCount { get; private set; }

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
            _confirmationObserved = true;
            _retryAvailable = false;
            return PageActionObservationKind.ConfirmationObserved;
        }

        if (!string.Equals(currentActionKey, ActionKey, StringComparison.Ordinal))
        {
            return PageActionObservationKind.Completed;
        }

        if (_retryAvailable)
        {
            return PageActionObservationKind.RetryAvailable;
        }

        if (!_confirmationObserved
            && AttemptCount < MaxAttempts
            && ResolveTimestamp(observation) - _lastAttemptAt >= _retryAfter)
        {
            _retryAvailable = true;
            return PageActionObservationKind.RetryAvailable;
        }

        return PageActionObservationKind.SourcePageStillPresent;
    }

    public bool TryConsumeRetry(PageObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!_retryAvailable || observation.HasCenterConfirmation)
        {
            return false;
        }

        _retryAvailable = false;
        return true;
    }

    public void MarkCommandSent(PageObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        AttemptCount++;
        _lastAttemptAt = ResolveTimestamp(observation);
        _retryAvailable = false;
    }

    public bool CanCancelAfterUnhandledAttempt => AttemptCount == 0;

    private static DateTimeOffset ResolveTimestamp(PageObservation observation)
        => observation.CapturedAt == default
            ? DateTimeOffset.UtcNow
            : observation.CapturedAt;
}