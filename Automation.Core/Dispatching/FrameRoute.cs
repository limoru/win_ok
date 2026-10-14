namespace Automation.Core.Dispatching;

public sealed record FrameRoute<TFrame>(
    string Id,
    int Priority,
    Func<TFrame, bool> Matches,
    Func<TFrame, CancellationToken, ValueTask<bool>> HandleAsync,
    bool UseActionBoundary = true,
    bool AllowFallbackWhenUnhandled = false);