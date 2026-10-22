namespace Automation.Core.Targeting;

public interface IInputSink
{
    ValueTask ClickAsync(RelativePoint point, CancellationToken cancellationToken = default);

    ValueTask SwipeAsync(
        RelativePoint from,
        RelativePoint to,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a mouse-wheel style scroll at the requested point. Negative deltas scroll down.
    /// Touch-only targets may leave the default swipe fallback in place.
    /// </summary>
    ValueTask ScrollAsync(
        RelativePoint point,
        int wheelNotches,
        CancellationToken cancellationToken = default)
    {
        var travel = Math.Clamp(wheelNotches * 0.08, -0.35, 0.35);
        var destination = new RelativePoint(
            point.X,
            Math.Clamp(point.Y + travel, 0d, 1d));
        return SwipeAsync(
            point.Clamp(),
            destination,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
    }

    ValueTask KeyAsync(InputKey key, CancellationToken cancellationToken = default);

    ValueTask TextAsync(string text, CancellationToken cancellationToken = default);
}