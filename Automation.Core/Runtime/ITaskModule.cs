namespace Automation.Core.Runtime;

public interface ITaskModule : IAsyncDisposable
{
    string ModuleId { get; }
    string DisplayName { get; }

    ValueTask<bool> RunOnceAsync(TaskContext context, CancellationToken cancellationToken);

    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
}