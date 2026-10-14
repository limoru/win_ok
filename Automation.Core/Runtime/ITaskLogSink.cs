namespace Automation.Core.Runtime;

public interface ITaskLogSink
{
    void Log(string sessionId, string message, string? tag = null);
}