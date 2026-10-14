namespace Automation.Core.Events;

public interface ISessionEventBus
{
    void Publish<TEvent>(TEvent @event)
        where TEvent : ISessionEvent;

    IAsyncEnumerable<TEvent> Subscribe<TEvent>(CancellationToken cancellationToken = default)
        where TEvent : ISessionEvent;
}