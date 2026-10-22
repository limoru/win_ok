namespace Automation.Core.Dispatching;

public enum FrameDispatchErrorLogDecisionKind
{
    Observe,
    Alert,
    Suppress
}

public readonly record struct FrameDispatchErrorLogDecision(
    FrameDispatchErrorLogDecisionKind Kind,
    int ConsecutiveCount,
    bool IsImmediate);

/// <summary>
/// Coalesces repeated dispatch errors until the same error streak reaches the
/// configured threshold. Critical consumer failures remain immediate.
/// </summary>
public sealed class FrameDispatchErrorLogGate
{
    private readonly object _gate = new();
    private (FrameDispatchErrorKind Kind, string ConsumerId)? _streakKey;
    private int _consecutiveCount;
    private bool _alertRaised;

    public FrameDispatchErrorLogDecision Record(FrameDispatchError error, int threshold)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (threshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        if (IsImmediate(error.Kind))
        {
            return new FrameDispatchErrorLogDecision(
                FrameDispatchErrorLogDecisionKind.Alert,
                1,
                IsImmediate: true);
        }

        lock (_gate)
        {
            var key = (error.Kind, error.ConsumerId ?? string.Empty);
            if (_streakKey != key)
            {
                _streakKey = key;
                _consecutiveCount = 1;
                _alertRaised = false;
            }
            else
            {
                _consecutiveCount++;
            }

            if (_alertRaised)
            {
                return new FrameDispatchErrorLogDecision(
                    FrameDispatchErrorLogDecisionKind.Suppress,
                    _consecutiveCount,
                    IsImmediate: false);
            }

            if (_consecutiveCount < threshold)
            {
                return new FrameDispatchErrorLogDecision(
                    FrameDispatchErrorLogDecisionKind.Observe,
                    _consecutiveCount,
                    IsImmediate: false);
            }

            _alertRaised = true;
            return new FrameDispatchErrorLogDecision(
                FrameDispatchErrorLogDecisionKind.Alert,
                _consecutiveCount,
                IsImmediate: false);
        }
    }

    private static bool IsImmediate(FrameDispatchErrorKind kind)
        => kind is FrameDispatchErrorKind.ConsumerFailed
            or FrameDispatchErrorKind.DeliveryRejected;

    public void Reset()
    {
        lock (_gate)
        {
            _streakKey = null;
            _consecutiveCount = 0;
            _alertRaised = false;
        }
    }
}
