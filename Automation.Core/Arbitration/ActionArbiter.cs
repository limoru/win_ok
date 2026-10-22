using Automation.Core.Targeting;

namespace Automation.Core.Arbitration;

public enum PageActionDecisionKind
{
    Execute,
    Recover,
    SuppressDuplicate
}

public readonly record struct PageActionDecision(
    PageActionDecisionKind Kind,
    string Reason,
    int RecoveryCount = 0)
{
    public static PageActionDecision Execute(string reason)
        => new(PageActionDecisionKind.Execute, reason);

    public static PageActionDecision Recover(string reason, int recoveryCount)
        => new(PageActionDecisionKind.Recover, reason, recoveryCount);

    public static PageActionDecision Suppress(string reason)
        => new(PageActionDecisionKind.SuppressDuplicate, reason);
}

public sealed class ActionArbiter
{
    private ConfirmationSession? _confirmation;
    private readonly Dictionary<string, PageActionSession> _pageActions =
        new(StringComparer.Ordinal);
    private PageObservation? _previousObservation;
    private long _lastActionFrameSequence = -1;

    public PageActionDecision TryBeginPageAction(
        string key,
        PageObservation observation,
        int? retryAfterFrames = null,
        int maxAttempts = 3,
        string? sourceRouteId = null,
        int recoveryAfterFrames = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(observation);

        if (retryAfterFrames is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterFrames));
        }

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        if (recoveryAfterFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryAfterFrames));
        }

        if (_confirmation?.IsActive == true)
        {
            return PageActionDecision.Suppress("中心确认流程正在接管当前帧，暂不执行页面动作。");
        }

        if (_pageActions.TryGetValue(key, out var pageAction))
        {
            if (retryAfterFrames is { } retryFrames && pageAction.CanRetryAfter(
                    observation.FrameSequence,
                    retryFrames,
                    maxAttempts))
            {
                _previousObservation = observation;
                return PageActionDecision.Execute(
                    $"当前页面动作保持不变，已等待 {retryFrames} 帧，允许第 {pageAction.AttemptCount + 1}/{maxAttempts} 次尝试。");
            }

            if (pageAction.HasReachedAttemptLimit(maxAttempts))
            {
                if (retryAfterFrames is not null
                    && pageAction.CanRecoverAfter(
                        observation.FrameSequence,
                        recoveryAfterFrames))
                {
                    pageAction.MarkRecoveryDecision(observation.FrameSequence);
                    _previousObservation = observation;
                    return PageActionDecision.Recover(
                        $"页面动作达到 {pageAction.AttemptCount}/{maxAttempts} 次快速重试上限，冷却 {recoveryAfterFrames} 帧后执行第 {pageAction.RecoveryCount} 次自动恢复。",
                        pageAction.RecoveryCount);
                }

                return PageActionDecision.Suppress(
                    $"当前页面动作已达到 {pageAction.AttemptCount}/{maxAttempts} 次快速重试上限，等待自动恢复冷却。");
            }

            return PageActionDecision.Suppress(retryAfterFrames is { } configuredRetryFrames
                ? $"当前页面动作已经发送，等待页面阶段变化；重试至少需要 {configuredRetryFrames} 帧。"
                : "当前页面动作已经发送；该语义路由未声明自动重试，等待页面阶段变化。");
        }

        _pageActions[key] = new PageActionSession(
            observation.SessionId,
            key,
            observation,
            sourceRouteId);
        _previousObservation = observation;
        return PageActionDecision.Execute("允许执行新的页面动作。");
    }

    public void MarkPageActionSent(string key, PageObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(observation);

        if (!_pageActions.TryGetValue(key, out var pageAction))
        {
            return;
        }

        pageAction.MarkCommandSent(observation.FrameSequence);
        _previousObservation = observation;
        foreach (var otherKey in _pageActions.Keys
                     .Where(otherKey => !string.Equals(otherKey, key, StringComparison.Ordinal))
                     .ToArray())
        {
            _pageActions.Remove(otherKey);
        }
    }

    public void CancelPageAction(string key, PageObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(observation);

        if (!_pageActions.TryGetValue(key, out var pageAction)
            || !pageAction.CanCancelAfterUnhandledAttempt)
        {
            return;
        }

        _pageActions.Remove(key);
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
            if (confirmationDecision.Kind is ActionDecisionKind.Completed or ActionDecisionKind.Failed)
            {
                _pageActions.Clear();
            }
            ResetCompletedSession(confirmationDecision, observation);
            return ToActionDecision(confirmationDecision);
        }

        RemoveActionsForRoutesNoLongerObserved(observation, currentActionKey);

        if (currentActionKey is not null
            && _pageActions.TryGetValue(currentActionKey, out var pageAction))
        {
            switch (pageAction.Observe(observation, currentActionKey))
            {
                case PageActionObservationKind.SourcePageStillPresent:
                    return ActionDecision.SuppressDuplicate(
                        "当前路由动作已经执行，路由未离开前禁止重复执行。");
                case PageActionObservationKind.ConfirmationObserved:
                    return ActionDecision.None();
                case PageActionObservationKind.Completed:
                    _pageActions.Remove(currentActionKey);
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

    private void RemoveActionsForRoutesNoLongerObserved(
        PageObservation observation,
        string? currentActionKey)
    {
        if (observation.RouteIds.Count == 0)
        {
            return;
        }

        var observedRouteIds = observation.RouteIds.ToHashSet(StringComparer.Ordinal);
        foreach (var entry in _pageActions.ToArray())
        {
            if (string.Equals(entry.Key, currentActionKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!observedRouteIds.Contains(entry.Value.SourceRouteId))
            {
                _pageActions.Remove(entry.Key);
            }
        }
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
