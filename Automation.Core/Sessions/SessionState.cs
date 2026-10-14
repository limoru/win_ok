namespace Automation.Core.Sessions;

public enum SessionState
{
    Ready,
    Stopped,
    Starting,
    Running,
    Paused,
    Faulted,
    NeedsAttention
}