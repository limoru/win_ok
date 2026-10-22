namespace Automation.Core.Runtime;

/// <summary>
/// 任务模块请求暂停当前会话时使用的出口。宿主负责更新会话状态，
/// 任务模块不直接操作 GUI。
/// </summary>
public interface ITaskPauseRequestSink
{
    bool RequestPause(string sessionId, string reason);
}