using Automation.Core.Vision;

namespace Automation.Core.Events;

public sealed record FullFrameOcrCompleted(
    string SessionId,
    long FrameSequence,
    DateTimeOffset OccurredAt,
    FullFrameOcrObservation Observation) : ISessionEvent;