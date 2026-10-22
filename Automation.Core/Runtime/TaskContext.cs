using Automation.Core.Configuration;
using Automation.Core.DeveloperTools;
using Automation.Core.Frames;
using Automation.Core.Targeting;
using Automation.Core.Vision;

namespace Automation.Core.Runtime;

public sealed class TaskContext
{
    private static readonly IReadOnlyDictionary<string, object> EmptyTaskConfig =
        new Dictionary<string, object>(StringComparer.Ordinal);

    private readonly object _frameGate = new();
    private readonly Queue<string> _recentLogs = new();
    private const int MaxRecentLogs = 100;
    private DateTimeOffset _minimumNextFrameCapturedAt = DateTimeOffset.MinValue;

    public TaskContext(
        string sessionId,
        UiProfile uiProfile,
        TargetCapabilities capabilities,
        IFrameSource frames,
        IInputSink input,
        IActivityReporter? activity = null,
        ITaskLogSink? logSink = null,
        IReadOnlyDictionary<string, object>? taskConfig = null,
        IOcrProvider? ocr = null,
        TaskConfigSchema? taskConfigSchema = null,
        IFeatureMatcher? featureMatcher = null,
        IRuntimeFeatureTemplateStore? runtimeFeatureTemplates = null,
        IDecisionSnapshotSink? decisionSnapshots = null,
        ITaskStopRequestSink? stopRequestSink = null,
        ITaskPauseRequestSink? pauseRequestSink = null,
        IFrameSampleArchiveSink? frameSamples = null,
        string? runId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        }

        SessionId = sessionId;
        UiProfile = uiProfile;
        Capabilities = capabilities;
        Frames = frames ?? throw new ArgumentNullException(nameof(frames));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Activity = activity;
        LogSink = logSink;
        TaskConfig = taskConfig ?? EmptyTaskConfig;
        Ocr = ocr;
        Config = new TaskConfigReader(taskConfigSchema, TaskConfig);
        FeatureMatcher = featureMatcher;
        RuntimeFeatureTemplates = runtimeFeatureTemplates;
        DecisionSnapshots = decisionSnapshots;
        StopRequestSink = stopRequestSink;
        PauseRequestSink = pauseRequestSink;
        FrameSamples = frameSamples;
        RunId = string.IsNullOrWhiteSpace(runId) ? sessionId : runId;
    }

    public string SessionId { get; }
    public UiProfile UiProfile { get; }
    public TargetCapabilities Capabilities { get; }
    public IFrameSource Frames { get; }
    public IInputSink Input { get; }

    /// <summary>
    /// Reports structured progress to the session runtime. GUI binds to the latest
    /// snapshot instead of parsing free-form log text.
    /// </summary>
    public IActivityReporter? Activity { get; }

    public ITaskLogSink? LogSink { get; }
    public IReadOnlyDictionary<string, object> TaskConfig { get; }
    public TaskConfigReader Config { get; }
    public IOcrProvider? Ocr { get; }
    public IFeatureMatcher? FeatureMatcher { get; }
    public IRuntimeFeatureTemplateStore? RuntimeFeatureTemplates { get; }
    public IDecisionSnapshotSink? DecisionSnapshots { get; }
    public ITaskStopRequestSink? StopRequestSink { get; }
    public ITaskPauseRequestSink? PauseRequestSink { get; }
    public IFrameSampleArchiveSink? FrameSamples { get; }
    public string RunId { get; }

    public T GetTaskConfig<T>(string key, T fallback)
        => Config.Get(key, fallback);

    public bool RequestStop(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (StopRequestSink is null)
        {
            LogInfo("当前会话没有配置停止请求接收器，无法停止任务。", "warning");
            return false;
        }

        StopRequestSink.RequestStop(SessionId, reason.Trim());
        return true;
    }

    public bool RequestPause(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (PauseRequestSink is null)
        {
            LogInfo("当前会话没有配置暂停请求接收器，无法暂停任务。", "warning");
            return false;
        }

        return PauseRequestSink.RequestPause(SessionId, reason.Trim());
    }

    public ValueTask<FrameLease> GetLatestFrameAsync(CancellationToken cancellationToken = default)
        => Frames.GetLatestAsync(cancellationToken);

    public async ValueTask<FrameLease> GetNextFrameAsync(
        long afterSequence,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        var cursor = afterSequence;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("等待输入后的新画面超时。");
            }

            var frame = await Frames
                .WaitNextAsync(cursor, remaining, cancellationToken)
                .ConfigureAwait(false);
            var minimumCapturedAt = GetMinimumNextFrameCapturedAt();
            if (frame.CapturedAt >= minimumCapturedAt)
            {
                return frame;
            }

            cursor = Math.Max(cursor, frame.Sequence);
            frame.Dispose();
        }
    }

    public async ValueTask ClickRelativeAsync(
        RelativePoint point,
        CancellationToken cancellationToken = default)
    {
        await Input.ClickAsync(point.Clamp(), cancellationToken).ConfigureAwait(false);
        MarkInputCompleted();
    }

    public async ValueTask SwipeRelativeAsync(
        RelativePoint from,
        RelativePoint to,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        await Input
            .SwipeAsync(from.Clamp(), to.Clamp(), duration, cancellationToken)
            .ConfigureAwait(false);
        MarkInputCompleted();
    }

    public async ValueTask ScrollRelativeAsync(
        RelativePoint point,
        int wheelNotches,
        CancellationToken cancellationToken = default)
    {
        await Input.ScrollAsync(point.Clamp(), wheelNotches, cancellationToken).ConfigureAwait(false);
        MarkInputCompleted();
    }

    public async ValueTask KeyAsync(InputKey key, CancellationToken cancellationToken = default)
    {
        await Input.KeyAsync(key, cancellationToken).ConfigureAwait(false);
        MarkInputCompleted();
    }

    public async ValueTask TextAsync(string text, CancellationToken cancellationToken = default)
    {
        await Input.TextAsync(text, cancellationToken).ConfigureAwait(false);
        MarkInputCompleted();
    }

    public Task SleepAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        return Task.Delay(delay, cancellationToken);
    }

    public void LogInfo(string message, string? tag = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var logEntry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag ?? "info"}] {message}";
        lock (_recentLogs)
        {
            _recentLogs.Enqueue(logEntry);
            while (_recentLogs.Count > MaxRecentLogs)
            {
                _recentLogs.Dequeue();
            }
        }

        LogSink?.Log(SessionId, message, tag);
    }

    public void LogDebug(string message)
        => LogInfo(message, "debug");

    public IReadOnlyList<string> GetRecentLogs()
    {
        lock (_recentLogs)
        {
            return _recentLogs.ToArray();
        }
    }
    private DateTimeOffset GetMinimumNextFrameCapturedAt()
    {
        lock (_frameGate)
        {
            return _minimumNextFrameCapturedAt;
        }
    }

    private void MarkInputCompleted()
    {
        lock (_frameGate)
        {
            _minimumNextFrameCapturedAt = DateTimeOffset.Now;
        }
    }
}
