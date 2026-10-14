namespace Automation.Core.Runtime;

public interface ITaskModule
{
    string ModuleId { get; }
    string DisplayName { get; }

    ValueTask<bool> RunOnceAsync(TaskContext context, CancellationToken cancellationToken);
}