namespace Automation.Core.Dispatching;

public sealed class FrameRouter<TFrame>
{
    private readonly FrameRoute<TFrame>[] _routes;

    public FrameRouter(IEnumerable<FrameRoute<TFrame>> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        _routes = routes
            .OrderBy(route => route.Priority)
            .ThenBy(route => route.Id, StringComparer.Ordinal)
            .ToArray();

        if (_routes.Any(route => string.IsNullOrWhiteSpace(route.Id)))
        {
            throw new ArgumentException("Every frame route requires a non-empty id.", nameof(routes));
        }

        var duplicateId = _routes
            .GroupBy(route => route.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new ArgumentException($"Duplicate frame route id '{duplicateId.Key}'.", nameof(routes));
        }
    }

    public IReadOnlyList<FrameRoute<TFrame>> Routes => _routes;

    public FrameRoute<TFrame>? Resolve(TFrame frame)
    {
        foreach (var route in _routes)
        {
            if (route.Matches(frame))
            {
                return route;
            }
        }

        return null;
    }

    public FrameRoute<TFrame>? ResolvePriorityOverlay(TFrame frame)
    {
        foreach (var route in _routes)
        {
            if (route.IsPriorityOverlay && route.Matches(frame))
            {
                return route;
            }
        }

        return null;
    }

    public FrameRoute<TFrame>? FindById(string routeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        return _routes.FirstOrDefault(route =>
            string.Equals(route.Id, routeId, StringComparison.Ordinal));
    }

    public IReadOnlyList<FrameRoute<TFrame>> ResolveCandidates(TFrame frame)
        => _routes.Where(route => route.Matches(frame)).ToArray();

    public async ValueTask<FrameDispatchResult> DispatchAsync(
        TFrame frame,
        CancellationToken cancellationToken = default)
    {
        var candidates = ResolveCandidates(frame);
        if (candidates.Count == 0)
        {
            return FrameDispatchResult.NotMatched;
        }

        foreach (var route in candidates)
        {
            var handled = await route.HandleAsync(frame, cancellationToken).ConfigureAwait(false);
            if (handled || !route.AllowFallbackWhenUnhandled)
            {
                return new FrameDispatchResult(true, handled);
            }
        }

        return new FrameDispatchResult(true, false);
    }
}
public readonly record struct FrameDispatchResult(bool Matched, bool Handled)
{
    public static FrameDispatchResult NotMatched => new(false, false);
}
