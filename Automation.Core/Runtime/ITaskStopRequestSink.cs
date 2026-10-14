namespace Automation.Core.Runtime;

/// <summary>
/// 任务模块请求停止当前会话时使用的出口。宿主负责把请求转成自身的停止流程，
/// 任务模块不直接操作 GUI 或目标窗口。
/// </summary>
public interface ITaskStopRequestSink
{
    void RequestStop(string sessionId, string reason);
}