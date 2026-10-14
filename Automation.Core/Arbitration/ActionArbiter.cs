using Automation.Core.Targeting;

namespace Automation.Core.Arbitration;

public enum PageActionDecisionKind
{
    Execute,
    SuppressDuplicate
}

public readonly record struct PageActionDecision(
    PageActionDecisionKind Kind,
    string Reason,
    bool IsRetry = false)
{
    public static PageActionDecision Execute(string reason, bool isRetry = false)
        => new(PageActionDecisionKind.Execute, reason, isRetry);

    public static PageActionDecision Suppress(string reason)
        => new(PageActionDecisionKind.SuppressDuplicate, reason);
}

public sealed class ActionArbiter
{
    private ConfirmationSession? _confirmation;
    private PageActionSession? _pageAction;
    private PageObservation? _previousObservation;
    private long _lastActionFrameSequence = -1;

    public PageActionDecision TryBeginPageAction(string key, PageObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(observation);

        if (_confirmation?.IsActive == true)
        {
            return PageActionDecision.Suppress("中心确认流程正在接管当前帧，暂不执行页面动作。");
        }

        if (_pageAction is not null)
        {
            if (string.Equals(_pageAction.ActionKey, key, StringComparison.Ordinal))
            {
                return _pageAction.TryConsumeRetry(observation)
                    ? PageActionDecision.Execute("允许在超时后重试一次同一页面动作。", isRetry: true)
                    : PageActionDecision.Suppress("当前路由动作已经发送，等待路由变化或确认弹窗。");
            }

            _pageAction = null;
        }

        _pageAction = new PageActionSession(observation.SessionId, key, observation);
        _previousObservation = observation;
        return PageActionDecision.Execute("允许执行新的页面动作。");
    }

    public void MarkPageActionSent(string key, PageObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(observation);

        if (_pageAction is null
            || !string.Equals(_pageAction.ActionKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _pageAction.MarkCommandSent(observation);
    }

    public void CancelPageAction(string key, PageObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(observation);

        if (_pageAction is null
            || !string.Equals(_pageAction.ActionKey, key, StringComparison.Ordinal)
            || !_pageAction.CanCancelAfterUnhandledAttempt)
        {
            return;
        }

        _pageAction = null;
    }

    public ActionDecision SubmitSkip(
        SkipCurrentContentIntent intent,
        PageObservation observation,
        RelativePoint target)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(observation);

        if (intent.FrameSequence <= _lastActionFrameSequence)
        {
            return ActionDecision.SuppressDuplicate("忽略过期或同帧的重复跳过意图。");
        }

        if (_confirmation?.IsActive == true)
        {
            return _confirmation.Intent.CooldownKey == intent.CooldownKey
                ? ActionDecision.SuppressDuplicate("相同冷却键的确认流程仍在进行。")
                : ActionDecision.SuppressDuplicate("当前会话已有其他确认流程，暂不执行新动作。");
        }

        _confirmation = ConfirmationSession.ForSkip(observation.SessionId, intent, observation);
        var command = new InputCommand(
            CommandId.New(),
            intent.Id,
            intent.FrameSequence,
            InputCommandKind.SkipCurrentContent,
            target.Clamp(),
            intent.Reason);
        return new ActionDecision(ActionDecisionKind.ExecuteSkip, "允许执行跳过命令。", command);
    }

    public ActionDecision ObservePage(PageObservation observation, string? currentActionKey = null)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (_confirmation is not null)
        {
            var confirmationDecision = _confirmation.ObservePage(observation);
            ResetCompletedSession(confirmationDecision, observation);
            return ToActionDecision(confirmationDecision);
        }

        if (_pageAction is not null)
        {
            switch (_pageAction.Observe(observation, currentActionKey))
            {
                case PageActionObservationKind.SourcePageStillPresent:
                    return ActionDecision.SuppressDuplicate(
                        "当前路由动作已经执行，路由未离开前禁止重复执行。");
                case PageActionObservationKind.ConfirmationObserved:
                    return ActionDecision.None();
                case PageActionObservationKind.RetryAvailable:
                    return ActionDecision.None();
                case PageActionObservationKind.Completed:
                    _pageAction = null;
                    _previousObservation = observation;
                    return ActionDecision.None();
                default:
                    throw new InvalidOperationException("Unhandled page action observation.");
            }
        }

        if (!observation.HasCenterConfirmation)
        {
            _previousObservation = observation;
        }

        return ActionDecision.None();
    }

    public ActionDecision ObserveConfirmation(PageObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (!observation.HasCenterConfirmation || observation.CenterConfirmationPoint is null)
        {
            return ActionDecision.None();
        }

        if (observation.FrameSequence <= _lastActionFrameSequence)
        {
            return ActionDecision.SuppressDuplicate("忽略过期或同帧的重复确认命令。");
        }

        if (_confirmation is null)
        {
            var source = _previousObservation ?? observation;
            var intent = new ConfirmCenterDialogIntent(
                IntentId.New(),
                CooldownKeys.CenterConfirmation,
                observation.FrameSequence,
                source.Fingerprint,
                "中心确认弹窗");
            _confirmation = ConfirmationSession.ForConfirmation(
                observation.SessionId,
                intent,
                source);
        }

        var decision = _confirmation.ObserveConfirmation(observation);
        if (decision.Kind != ActionDecisionKind.ExecuteConfirm
            || decision.Target is null
            || decision.Intent is null)
        {
            ResetCompletedSession(decision, observation);
            return ToActionDecision(decision);
        }

        var command = new InputCommand(
            CommandId.New(),
            decision.Intent.Id,
            observation.FrameSequence,
            InputCommandKind.ConfirmCenterDialog,
            decision.Target.Value.Clamp(),
            decision.Intent.Reason);
        return new ActionDecision(
            ActionDecisionKind.ExecuteConfirm,
            decision.Reason,
            command);
    }

    public void MarkCommandSent(InputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _lastActionFrameSequence = Math.Max(_lastActionFrameSequence, command.EvidenceFrameSequence);

        if (_confirmation is null)
        {
            return;
        }

        if (command.Kind == InputCommandKind.SkipCurrentContent)
        {
            _confirmation.MarkSkipCommandSent(command.EvidenceFrameSequence);
        }
        else if (command.Kind == InputCommandKind.ConfirmCenterDialog)
        {
            _confirmation.MarkConfirmCommandSent(command.EvidenceFrameSequence);
        }
    }

    private static ActionDecision ToActionDecision(ConfirmationDecision decision)
        => new(decision.Kind, decision.Reason);

    private void ResetCompletedSession(
        ConfirmationDecision decision,
        PageObservation observation)
    {
        if (decision.Kind is not (ActionDecisionKind.Completed or ActionDecisionKind.Failed))
        {
            return;
        }

        _confirmation = null;
        _previousObservation = observation;
    }
}