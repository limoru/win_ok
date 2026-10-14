using Automation.Core.Configuration;
using Automation.Core.Runtime;
using Automation.Core.Targeting;

namespace Automation.Core.Sessions;

public sealed class AutomationSession : IAsyncDisposable, IActivityReporter
{
    public AutomationSession(string sessionId, TargetBinding binding, UiProfile uiProfile)
    {
        SessionId = sessionId;
        Binding = binding;
        UiProfile = uiProfile;
    }

    public string SessionId { get; }
    public TargetBinding Binding { get; }
    public UiProfile UiProfile { get; set; }
    public ITargetSession? Target { get; private set; }
    public SessionState State { get; private set; } = SessionState.Ready;
    public SessionActivity Activity { get; private set; } = SessionActivity.Idle("等待会话启动");

    public void AttachTarget(ITargetSession target)
    {
        Target = target;
        State = SessionState.Running;
    }

    public void SetState(SessionState state)
    {
        State = state;
    }

    public void Report(SessionActivity activity)
    {
        Activity = activity;
    }

    public async ValueTask DisposeAsync()
    {
        if (Target is not null)
        {
            await Target.DisposeAsync().ConfigureAwait(false);
            Target = null;
        }

        State = SessionState.Stopped;
        Activity = SessionActivity.Idle("会话已停止");
    }
}