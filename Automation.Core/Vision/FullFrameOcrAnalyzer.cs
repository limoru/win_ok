using Automation.Core.Events;
using Automation.Core.Frames;

namespace Automation.Core.Vision;

public sealed class FullFrameOcrAnalyzer
{
    private const int MaximumCachedFrames = 8;
    private readonly object _gate = new();
    private readonly Dictionary<FrameKey, FullFrameOcrObservation> _cache = [];
    private readonly Queue<FrameKey> _cacheOrder = [];

    public FullFrameOcrObservation Analyze(
        string sessionId,
        FrameLease lease,
        IOcrProvider ocr,
        ISessionEventBus? eventBus = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(ocr);

        if (lease is not IBgraFrameLease bgraLease)
        {
            throw new InvalidOperationException(
                $"Full-frame OCR requires a BGRA frame, but received '{lease.GetType().Name}'.");
        }

        var key = new FrameKey(sessionId, lease.Sequence, lease.Width, lease.Height);
        FullFrameOcrObservation observation;
        var publish = false;

        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out observation!))
            {
                var recognized = ocr.Recognize(bgraLease.Frame);
                observation = new FullFrameOcrObservation(
                    sessionId,
                    lease.Sequence,
                    lease.CapturedAt,
                    lease.Width,
                    lease.Height,
                    recognized);
                _cache[key] = observation;
                _cacheOrder.Enqueue(key);
                TrimCache();
                publish = true;
            }
        }

        if (publish)
        {
            eventBus?.Publish(new FullFrameOcrCompleted(
                observation.SessionId,
                observation.FrameSequence,
                observation.CapturedAt,
                observation));
        }

        return observation;
    }

    private void TrimCache()
    {
        while (_cacheOrder.Count > MaximumCachedFrames)
        {
            var expired = _cacheOrder.Dequeue();
            _cache.Remove(expired);
        }
    }

    private readonly record struct FrameKey(
        string SessionId,
        long FrameSequence,
        int Width,
        int Height);
}