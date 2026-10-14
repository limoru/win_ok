namespace Automation.Core.Events;

public interface ISessionEvent
{
    string SessionId { get; }
    long FrameSequence { get; }
    DateTimeOffset OccurredAt { get; }
}