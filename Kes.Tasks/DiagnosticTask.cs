using Automation.Core.Frames;
using Automation.Core.Runtime;

namespace Kes.Tasks;

public sealed class DiagnosticTask : ITaskModule
{
    private static readonly TimeSpan FrameWaitTimeout = TimeSpan.FromSeconds(2);
    private long _lastSequence;
    private bool _waitingReported;
    private bool _timeoutReported;

    public string ModuleId => "Diagnostic";
    public string DisplayName => "运行诊断";

    public async ValueTask<bool> RunOnceAsync(
        TaskContext context,
        CancellationToken cancellationToken)
    {
        if (!_waitingReported)
        {
            _waitingReported = true;
            context.Activity?.Report(new SessionActivity(
                SessionActivityKind.Waiting,
                ModuleId,
                DisplayName,
                "waiting-frame",
                "等待新帧",
                "正在确认抓帧序号、尺寸和像素格式。",
                null,
                null,
                DateTimeOffset.Now));
        }

        FrameLease frame;
        try
        {
            frame = _lastSequence == 0
                ? await context.GetLatestFrameAsync(cancellationToken).ConfigureAwait(false)
                : await context
                    .GetNextFrameAsync(_lastSequence, FrameWaitTimeout, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!_timeoutReported)
            {
                _timeoutReported = true;
                context.Activity?.Report(new SessionActivity(
                    SessionActivityKind.Waiting,
                    ModuleId,
                    DisplayName,
                    "frame-timeout",
                    "等待新帧超时",
                    "目标可能已最小化、停止绘制或暂时不可捕获。",
                    null,
                    null,
                    DateTimeOffset.Now));
                context.LogInfo("等待新帧超时，诊断任务继续运行。", "warning");
            }

            return true;
        }

        using (frame)
        {
            if (_timeoutReported)
            {
                _timeoutReported = false;
                context.LogInfo($"抓帧已恢复：帧 #{frame.Sequence}。", "status");
            }

            var previousSequence = _lastSequence;
            _lastSequence = frame.Sequence;
            if (previousSequence != 0 && frame.Sequence - previousSequence < 10)
            {
                return true;
            }

            var detail = $"帧 #{frame.Sequence} · {frame.Width}x{frame.Height} · {frame.PixelFormat}";
            context.Activity?.Report(new SessionActivity(
                SessionActivityKind.Running,
                ModuleId,
                DisplayName,
                "frame-ready",
                "抓帧链路正常",
                detail,
                null,
                null,
                DateTimeOffset.Now));
            context.LogInfo($"诊断通过：{detail}", "status");
        }

        return true;
    }
}