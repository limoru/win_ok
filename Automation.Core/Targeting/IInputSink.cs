namespace Automation.Core.Targeting;

public interface IInputSink
{
    ValueTask ClickAsync(RelativePoint point, CancellationToken cancellationToken = default);

    ValueTask HoverClickAsync(RelativePoint point, CancellationToken cancellationToken = default)
        => ClickAsync(point, cancellationToken);

    ValueTask SwipeAsync(
        RelativePoint from,
        RelativePoint to,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    ValueTask KeyAsync(InputKey key, CancellationToken cancellationToken = default);

    ValueTask TextAsync(string text, CancellationToken cancellationToken = default);
}