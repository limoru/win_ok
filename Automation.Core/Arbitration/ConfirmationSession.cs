using Automation.Core.Targeting;

namespace Automation.Core.Arbitration;

public enum ConfirmationState
{
    AwaitingDialog,
    ConfirmClicked,
    Completed,
    Failed
}

public sealed class ConfirmationSession
{
    private const int ConfirmRetryFrameInterval = 6;
    private const int RequiredDistinctPageFrames = 2;

    private readonly string _sessionId;
    private readonly ActionIntent _intent;
    private readonly PageFingerprint _sourcePageFingerprint;
    private PageFingerprint? _confirmationDialogFingerprint;
    private long _lastObservedFrameSequence;
    private long _lastConfirmCommandFrameSequence = -1;
    private int _distinctPageFrameCount;
    private bool _skipCommandSent;

    private ConfirmationSession(
        string sessionId,
        ActionIntent intent,
        PageObservation sourceObservation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(sourceObservation);

        _sessionId = sessionId;
        _intent = intent;
        _sourcePageFingerprint = intent.SourcePageFingerprint;
        _lastObservedFrameSequence = sourceObservation.FrameSequence;
        State = ConfirmationState.AwaitingDialog;
    }

    public ConfirmationState State { get; private set; }
    public ActionIntent Intent => _intent;
    public PageFingerprint SourcePageFingerprint => _sourcePageFingerprint;
    public bool IsActive => State is ConfirmationState.AwaitingDialog or ConfirmationState.ConfirmClicked;
    public bool HasSourcePageReturned { get; private set; }

    public static ConfirmationSession ForSkip(
        string sessionId,
        ActionIntent intent,
        PageObservation sourceObservation)
        => new(sessionId, intent, sourceObservation);

    public static ConfirmationSession ForConfirmation(
        string sessionId,
        ActionIntent intent,
        PageObservation sourceObservation)
        => new(sessionId, intent, sourceObservation);

    public ConfirmationDecision ObservePage(PageObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SessionId != _sessionId)
        {
            return ConfirmationDecision.Wait("观察结果属于其他会话。");
        }

        if (observation.FrameSequence <= _lastObservedFrameSequence)
        {
            return ConfirmationDecision.Wait("忽略过期帧。");
        }

        _lastObservedFrameSequence = observation.FrameSequence;
        if (State is ConfirmationState.Completed or ConfirmationState.Failed)
        {
            return State == ConfirmationState.Completed
                ? ConfirmationDecision.Completed("确认流程已经完成。")
                : ConfirmationDecision.Failed("确认流程已经失败。");
        }

        if (observation.Fingerprint == _sourcePageFingerprint)
        {
            _distinctPageFrameCount = 0;
            HasSourcePageReturned = _skipCommandSent || State == ConfirmationState.ConfirmClicked;
            return ConfirmationDecision.SuppressDuplicate("来源页面仍然存在，禁止重复执行同一意图。", _intent);
        }

        if (State == ConfirmationState.AwaitingDialog && observation.HasCenterConfirmation)
        {
            return ConfirmationDecision.Wait("等待共享确认处理器处理当前弹窗。", _intent);
        }

        if (State == ConfirmationState.ConfirmClicked
            && observation.HasCenterConfirmation
            && observation.Fingerprint == _confirmationDialogFingerprint)
        {
            return ConfirmationDecision.Wait("原确认弹窗仍然存在，等待页面响应。", _intent);
        }

        if (!_skipCommandSent && _intent is SkipCurrentContentIntent)
        {
            return ConfirmationDecision.Wait("跳过指令尚未发送。", _intent);
        }

        if (State == ConfirmationState.AwaitingDialog)
        {
            if (observation.HasCenterConfirmation)
            {
                _distinctPageFrameCount = 0;
                return ConfirmationDecision.Wait("等待处理中心确认弹窗。", _intent);
            }

            _distinctPageFrameCount++;
            if (_distinctPageFrameCount >= RequiredDistinctPageFrames)
            {
                State = ConfirmationState.Completed;
                return ConfirmationDecision.Completed("来源页面已经离开，且没有出现确认弹窗。", _intent);
            }

            return ConfirmationDecision.Wait("等待更多页面退出证据。", _intent);
        }

        _distinctPageFrameCount++;
        if (_distinctPageFrameCount >= RequiredDistinctPageFrames)
        {
            State = ConfirmationState.Completed;
            return ConfirmationDecision.Completed("来源页面已经离开，确认流程完成。", _intent);
        }

        return ConfirmationDecision.Wait("等待更多页面退出证据。", _intent);
    }

    public ConfirmationDecision ObserveConfirmation(PageObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SessionId != _sessionId)
        {
            return ConfirmationDecision.Wait("确认弹窗属于其他会话。");
        }

        if (observation.FrameSequence < _lastObservedFrameSequence)
        {
            return ConfirmationDecision.Wait("忽略过期确认弹窗。", _intent);
        }

        _lastObservedFrameSequence = observation.FrameSequence;
        if (!observation.HasCenterConfirmation || observation.CenterConfirmationPoint is null)
        {
            return ConfirmationDecision.Wait("当前帧没有可用的中心确认按钮。", _intent);
        }

        if (State == ConfirmationState.AwaitingDialog)
        {
            _confirmationDialogFingerprint = observation.Fingerprint;
            return ConfirmationDecision.ExecuteConfirm(
                "检测到中心确认弹窗。",
                _intent,
                observation.CenterConfirmationPoint.Value);
        }

        if (State == ConfirmationState.ConfirmClicked
            && observation.FrameSequence - _lastConfirmCommandFrameSequence >= ConfirmRetryFrameInterval)
        {
            return ConfirmationDecision.ExecuteConfirm(
                "确认弹窗仍然存在，允许重试确认命令。",
                _intent,
                observation.CenterConfirmationPoint.Value);
        }

        return ConfirmationDecision.Wait("确认命令已经发送，等待页面响应。", _intent);
    }

    public ConfirmationDecision MarkSkipCommandSent(long frameSequence)
    {
        if (State != ConfirmationState.AwaitingDialog || _skipCommandSent)
        {
            return ConfirmationDecision.Wait("跳过命令已经记录。", _intent);
        }

        _skipCommandSent = true;
        _lastObservedFrameSequence = Math.Max(_lastObservedFrameSequence, frameSequence);
        return ConfirmationDecision.Wait("跳过命令已经发送，等待中心确认弹窗。", _intent);
    }

    public ConfirmationDecision MarkConfirmCommandSent(long frameSequence)
    {
        if (!_skipCommandSent && _intent is not ConfirmCenterDialogIntent)
        {
            return ConfirmationDecision.Wait("尚未记录跳过命令。", _intent);
        }

        if (State is ConfirmationState.Completed or ConfirmationState.Failed)
        {
            return ConfirmationDecision.Wait("确认流程已经结束。", _intent);
        }

        State = ConfirmationState.ConfirmClicked;
        _lastConfirmCommandFrameSequence = frameSequence;
        _lastObservedFrameSequence = Math.Max(_lastObservedFrameSequence, frameSequence);
        _distinctPageFrameCount = 0;
        return ConfirmationDecision.Wait("确认命令已经发送，等待来源页面离开。", _intent);
    }
}

public sealed record ConfirmationDecision(
    ActionDecisionKind Kind,
    string Reason,
    ActionIntent? Intent = null,
    RelativePoint? Target = null)
{
    public static ConfirmationDecision Wait(string reason, ActionIntent? intent = null)
        => new(ActionDecisionKind.Wait, reason, intent);

    public static ConfirmationDecision SuppressDuplicate(string reason, ActionIntent intent)
        => new(ActionDecisionKind.SuppressDuplicate, reason, intent);

    public static ConfirmationDecision ExecuteConfirm(
        string reason,
        ActionIntent intent,
        RelativePoint target)
        => new(ActionDecisionKind.ExecuteConfirm, reason, intent, target);

    public static ConfirmationDecision Completed(string reason, ActionIntent? intent = null)
        => new(ActionDecisionKind.Completed, reason, intent);

    public static ConfirmationDecision Failed(string reason, ActionIntent? intent = null)
        => new(ActionDecisionKind.Failed, reason, intent);
}