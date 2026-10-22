using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Automation.Core.Arbitration;
using Automation.Core.DeveloperTools;
using Automation.Core.Dispatching;
using Automation.Core.Events;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Kes.Tasks.Configuration;

namespace Kes.Tasks;

public sealed partial class ChaosModeTask : ITaskModule
{
    private const string GameLanguageKey = "游戏语言";
    private const string TraditionalChinese = "繁体中文";
    private const string CardRewardSkipActionKey = "card-reward-skip";
    private const string CardAssignmentSkipActionKey = "card-assignment-skip";
    private const string ConvertInactiveActionKey = "convert-inactive";
    private const string FlashEffectCardsActionKey = "flash-effect-cards";
    private const string EquipmentInstallTransitionActionKey = "equipment-install-transition";
    private const string EquipmentInstallConfirmWaitActionKey = "equipment-install-confirm-wait";
    private const int PageActionMaxAttempts = 3;

    private static readonly TimeSpan FrameWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly RelativeRegion CenterConfirmationProbeRegion =
        new(0.009, 0.168, 0.977, 0.875);
    private static readonly RelativePoint FooterConfirmButtonHotspot =
        new(0.921, 0.931);
    private const string CenterConfirmationProbeId = "center-confirmation";

    private static async ValueTask HandleRecognitionFailureAsync(
        ChaosModePage page,
        string actionKey,
        string condition,
        RelativePoint intendedFixedPoint,
        CancellationToken cancellationToken)
    {
        var configuredRounds = page.Context.GetTaskConfig(
            ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey,
            2);
        var fallbackRounds = Math.Clamp(configuredRounds, 1, 10);
        var missCount = page.State.IncrementRecognitionMiss(actionKey);
        if (missCount < fallbackRounds)
        {
            page.Context.LogInfo(
                $"{condition}（{missCount}/{fallbackRounds}轮），等待下一轮重新识别。",
                "warning");
            return;
        }

        if (missCount > fallbackRounds)
        {
            return;
        }

        page.Context.LogInfo(
            $"连续{fallbackRounds}轮{condition}；固定位置点击已禁止，保存诊断现场。",
            "warning");
        var contextLogs = BuildRecognitionFailureContext(
            page,
            actionKey,
            condition,
            intendedFixedPoint,
            fallbackRounds);
        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.FixedClickBlockedReasonId,
                candidateActionLabel: $"已阻止固定点击：{condition}",
                candidateX: intendedFixedPoint.X,
                candidateY: intendedFixedPoint.Y,
                contextLogs: contextLogs,
                candidateActionSource: "固定位置兜底已禁止",
                force: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildRecognitionFailureContext(
        ChaosModePage page,
        string actionKey,
        string condition,
        RelativePoint? intendedFixedPoint,
        int? fallbackRounds)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"会话: {page.Context.SessionId}");
        builder.AppendLine($"帧序号: {page.FrameSequence}");
        builder.AppendLine($"动作: {actionKey}");
        builder.AppendLine($"条件: {condition}");
        if (fallbackRounds is not null)
        {
            builder.AppendLine($"连续未识别轮数: {fallbackRounds.Value}");
        }

        if (intendedFixedPoint is not null)
        {
            builder.AppendLine(
                $"原固定坐标: ({intendedFixedPoint.Value.X:0.0000}, {intendedFixedPoint.Value.Y:0.0000})");
        }
        builder.AppendLine("OCR:");
        foreach (var line in page.Recognized.Lines)
        {
            builder.AppendLine(
                $"- {line.Text} | score={line.Score:0.0000} | x={line.Region.X}, y={line.Region.Y}, w={line.Region.Width}, h={line.Region.Height}");
        }

        builder.AppendLine("最近日志:");
        foreach (var log in page.Context.GetRecentLogs())
        {
            builder.AppendLine(log);
        }

        return builder.ToString();
    }

    // 处理顺序整体对齐 Python 版 utils_chaos.PAGE_HANDLERS，括号里的编号是
    // Python PAGE_HANDLERS 中的下标。事件页必须先于通用文字按钮处理，避免
    // OCR 把事件描述里的“确认”拆成独立文本框后误点中央区域。
    private readonly ChaosRunState _state = new();
    private readonly FullFrameOcrAnalyzer _frameOcrAnalyzer = new();
    private readonly RegionOcrAnalyzer _regionOcrAnalyzer = new();
    private readonly SessionEventBus _eventBus = new();
    private readonly ActionArbiter _actionArbiter = new();
    private readonly ChaosCommandBus _commandBus = new();
    private readonly FrameRouter<ChaosModePage> _frameRouter = new(CreateFrameRoutes());
    private readonly TargetMemberArchiveFlow _targetMemberArchiveFlow;
    private readonly bool _useFrameDispatchBus;
    private readonly object _frameDispatchBusGate = new();
    private readonly SemaphoreSlim _frameActionGate = new(1, 1);
    private FrameDispatchBus<ChaosModePage>? _frameDispatchBus;
    private long _lastSequence;
    private long _lastSerializedFrameSequence = -1;
    private long _lastSampleFrameSequence;
    private int _disposed;
    private int _confirmationProbeArmed;
    private bool _lastProbeHitCenterConfirmation;
    private int _confirmationProbeMisses;

    private void AdvanceLastSequence(long sequence)
        => AdvanceSequence(ref _lastSequence, sequence);

    private void ArmCenterConfirmationProbe()
    {
        if (Interlocked.CompareExchange(ref _confirmationProbeArmed, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _confirmationProbeMisses, 0);
        }
    }

    internal static void AdvanceSequence(ref long target, long sequence)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (sequence <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, sequence, current) != current);
    }
    private bool _waitingReported;
    private bool _runtimeFeatureTemplatesInitialized;
    private DateTimeOffset _lastPerformanceLogAt = DateTimeOffset.MinValue;
    private string _lastUnhandledPageSignature = string.Empty;
    private DateTimeOffset _lastUnhandledPageLogAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan RestConfirmationTimeout = TimeSpan.FromSeconds(5);

    public ChaosModeTask(
        TimeSpan? targetMemberArchiveTimeout = null,
        bool useFrameDispatchBus = false)
    {
        _useFrameDispatchBus = useFrameDispatchBus;
        _targetMemberArchiveFlow = new TargetMemberArchiveFlow(
            targetMemberArchiveTimeout ?? TimeSpan.FromSeconds(8));
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        FrameDispatchBus<ChaosModePage>? bus;
        lock (_frameDispatchBusGate)
        {
            bus = _frameDispatchBus;
            _frameDispatchBus = null;
        }

        if (bus is not null)
        {
            await bus.DisposeAsync().ConfigureAwait(false);
        }

        _frameActionGate.Dispose();
    }

    public string ModuleId => "ChaosMode";
    public string DisplayName => "自动卡厄思模式";

    public void DispatchEscapeCommand(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("逃脱指令必须包含原因。", nameof(reason));
        }

        _commandBus.Dispatch(new EscapeCommand(reason.Trim(), DateTimeOffset.UtcNow));
    }
    /// <summary>当前单局运行状态（对应 Python node_status），供后续处理器读写与测试。</summary>
    public ChaosRunState RunState => _state;

    public async ValueTask<bool> RunOnceAsync(
        TaskContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ocr = context.Ocr
            ?? throw new InvalidOperationException("自动卡厄思模式需要 OCR 服务，但当前会话没有加载 OCR。 ");

        EnsureRuntimeFeatureTemplates(context);

        if (_useFrameDispatchBus)
        {
            return await RunFrameBusOnceAsync(context, ocr, cancellationToken).ConfigureAwait(false);
        }

        var runStarted = Stopwatch.GetTimestamp();
        FrameLease frameLease;
        try
        {
            frameLease = Volatile.Read(ref _lastSequence) == 0
                ? await context.GetLatestFrameAsync(cancellationToken).ConfigureAwait(false)
                : await context
                    .GetNextFrameAsync(Volatile.Read(ref _lastSequence), FrameWaitTimeout, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!_waitingReported)
            {
                _waitingReported = true;
                context.Activity?.Report(new SessionActivity(
                    SessionActivityKind.Waiting,
                    ModuleId,
                    DisplayName,
                    "waiting-frame",
                    "等待游戏画面",
                    "抓帧暂时没有新画面，卡厄思处理器继续等待。",
                    null,
                    null,
                    DateTimeOffset.Now));
                context.LogInfo("等待新画面超时，卡厄思处理器将继续重试。", "warning");
            }

            return true;
        }

        var frameWaitMs = Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds;
        _waitingReported = false;
        using (frameLease)
        {
            if (frameLease is not IBgraFrameLease bgraFrameLease)
            {
                throw new InvalidOperationException(
                    $"自动卡厄思模式仅支持 BGRA 帧，当前帧类型为 {frameLease.GetType().Name}。 ");
            }

            var ocrStarted = Stopwatch.GetTimestamp();
            var analysis = _frameOcrAnalyzer.Analyze(
                context.SessionId,
                frameLease,
                ocr,
                _eventBus);
            var recognized = analysis.Recognition;
            var ocrMs = Stopwatch.GetElapsedTime(ocrStarted).TotalMilliseconds;
            AdvanceLastSequence(frameLease.Sequence);
            var page = new ChaosModePage(
                context,
                bgraFrameLease.Frame,
                frameLease.Sequence,
                recognized,
                _state,
                _actionArbiter,
                _commandBus,
                frameLease,
                ArmCenterConfirmationProbe);

            var centerConfirmation = FindCenterConfirmation(page);
            var frameObservation = PageObservation.FromOcr(
                context.SessionId,
                page.FrameSequence,
                page.Frame.Width,
                page.Frame.Height,
                page.Recognized,
                centerConfirmation?.Point,
                frameLease.CapturedAt);
            page.Observation = frameObservation;
            ObserveEquipmentInstallLifecycle(page);
            IReadOnlyList<FrameRoute<ChaosModePage>> observedRoutes =
                _targetMemberArchiveFlow.IsActive
                    ? []
                    : _frameRouter.ResolveCandidates(page);
            page.Observation = page.Observation with
            {
                RouteIds = observedRoutes.Select(route => route.Id).ToArray()
            };
            var observedRoute = observedRoutes.FirstOrDefault();
            var observedActionKey = observedRoute is null
                ? null
                : ResolveActionBoundaryKey(observedRoute.Id, page);
            var observationDecision = _actionArbiter.ObservePage(
                page.Observation,
                observedActionKey);
            if (observationDecision.Kind == ActionDecisionKind.SuppressDuplicate)
            {
                context.LogDebug(
                    $"继续处理帧 #{page.FrameSequence}；页面观察未释放动作权，由消费者决定等待或重试。");
            }

            var handlersStarted = Stopwatch.GetTimestamp();
            var handled = false;
            if (_targetMemberArchiveFlow.IsActive)
            {
                handled = await TryHandlePriorityOverlayAsync(page, cancellationToken).ConfigureAwait(false);
                if (!handled)
                {
                    handled = await HandleTargetMemberArchiveAsync(page, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                handled = await HandlePageAsync(page, cancellationToken).ConfigureAwait(false);
            }

            if (handled == false)
            {
                AdvanceLastSequence(page.FrameSequence);
                _lastUnhandledPageSignature = string.Empty;
                _lastUnhandledPageLogAt = DateTimeOffset.MinValue;
            }

            var handlersMs = Stopwatch.GetElapsedTime(handlersStarted).TotalMilliseconds;
            var totalMs = Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds;
            if (!handled)
            {
                ReportUnhandledPage(context, recognized);
            }

            ReportSlowFrame(
                context,
                frameWaitMs,
                ocrMs,
                handlersMs,
                0,
                0,
                0,
                0,
                totalMs);
            return handled;
        }
    }

    private void EnsureRuntimeFeatureTemplates(TaskContext context)
    {
        if (_runtimeFeatureTemplatesInitialized)
        {
            return;
        }

        _runtimeFeatureTemplatesInitialized = true;
        var store = context.RuntimeFeatureTemplates;
        if (store is null || context.FeatureMatcher is not IRuntimeFeatureMatcher matcher)
        {
            return;
        }

        var targetName = context.GetTaskConfig("刷存档主战员", string.Empty).Trim();
        if (targetName.Length == 0)
        {
            return;
        }

        var loaded = 0;
        var avatarVariants = 0;
        foreach (var runtimeFeature in TargetMemberAvatarCatalog.Features)
        {
            var templates = store.Load(runtimeFeature.Name);
            if (runtimeFeature.Name == TargetMemberAvatarCatalog.BaseFeatureName)
            {
                avatarVariants = templates.Count;
            }

            foreach (var template in templates)
            {
                matcher.RegisterRuntimeTemplate(runtimeFeature.Name, template);
                loaded++;
            }
        }

        if (loaded == 0)
        {
            return;
        }

        if (avatarVariants > 0)
        {
            _state.SaveTargetMember = true;
            context.LogInfo(
                $"已从任务方案加载主战员「{targetName}」的头像变体 {avatarVariants} 个（模板 {loaded} 项），本轮跳过主界面头像确认。",
                "activity");
            return;
        }

        context.LogInfo(
            $"已从任务方案加载主战员「{targetName}」的头像候选（{loaded} 项），缺少基础头像，仍需进入主界面确认。",
            "activity");
    }

    private void ReportUnhandledPage(TaskContext context, OcrFrameResult recognized)
    {
        var signature = string.Join(
            " | ",
            recognized.Lines
                .Select(line => line.Text.Trim())
                .Where(text => text.Length > 0)
                .Take(30));
        if (signature.Length > 800)
        {
            signature = signature[..800];
        }

        if (!string.Equals(
                signature,
                _lastUnhandledPageSignature,
                StringComparison.Ordinal))
        {
            _lastUnhandledPageSignature = signature;
        }

        var now = DateTimeOffset.Now;
        if (_lastUnhandledPageLogAt != DateTimeOffset.MinValue
            && now - _lastUnhandledPageLogAt < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastUnhandledPageLogAt = now;
        context.LogInfo(
            string.IsNullOrWhiteSpace(signature)
                ? "未识别页面，没有匹配处理器，且 OCR 为空。"
                : $"未识别页面，没有匹配处理器。OCR={signature}",
            "warning");
    }
    private void ReportSlowFrame(
        TaskContext context,
        double frameWaitMs,
        double ocrMs,
        double handlersMs,
        double routeMs,
        double sampleCreateMs,
        double archiveMs,
        double publishMs,
        double totalMs)
    {
        if (frameWaitMs < 250
            && ocrMs < 300
            && handlersMs < 300
            && routeMs < 300
            && sampleCreateMs < 100
            && archiveMs < 100
            && totalMs < 800)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (now - _lastPerformanceLogAt < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastPerformanceLogAt = now;
        context.LogInfo($"[performance] wait={frameWaitMs:0}ms ocr={ocrMs:0}ms route={routeMs:0}ms sample={sampleCreateMs:0}ms archive={archiveMs:0}ms publish={publishMs:0}ms total={totalMs:0}ms", "performance");
    }

    private static async ValueTask CaptureDecisionSnapshotAsync(
        ChaosModePage page,
        string reasonId,
        string? candidateActionLabel = null,
        double? candidateX = null,
        double? candidateY = null,
        CancellationToken cancellationToken = default,
        string candidateActionSource = "任务当前候选操作",
        string? contextLogs = null,
        bool force = false)
    {
        var sink = page.Context.DecisionSnapshots;
        if (sink is null || (!sink.IsEnabled && !force))
        {
            return;
        }

        var definition = DecisionSnapshotCatalog.Resolve(reasonId);
        IReadOnlyList<DecisionCandidateAction> candidateActions =
            candidateActionLabel is null
                ? []
                :
                [
                    new DecisionCandidateAction(
                        candidateActionLabel,
                        candidateX,
                        candidateY,
                        candidateActionSource)
                ];
        var request = definition.CreateRequest(
            page.Context.SessionId,
            page.FrameSequence,
            DateTimeOffset.Now,
            candidateActions,
            contextLogs ?? string.Empty);

        try
        {
            var directory = await sink.CaptureAsync(
                    request,
                    page.Frame,
                    page.Recognized,
                    force: force,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (directory is not null)
            {
                page.Context.LogInfo(
                    $"开发标注样本已保存：{definition.PageLabel} → {directory}",
                    "developer");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            page.Context.LogInfo(
                $"开发标注样本保存失败：{definition.PageLabel} · {exception.Message}",
                "warning");
        }
    }

    // Python handle_close_page (utils.py:2010)：出现"点击屏幕"提示时点掉。
    private static async ValueTask<bool> HandleClosePageAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindTextAnywhere(page, GameText(page, "点击屏幕"));
        if (line is null)
        {
            return false;
        }

        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.CenterSkipReasonId,
                "点击提示文字",
                (line.Region.X + (line.Region.Width / 2d)) / page.Frame.Width,
                (line.Region.Y + (line.Region.Height / 2d)) / page.Frame.Height,
                cancellationToken)
            .ConfigureAwait(false);

        await ClickTextAsync(page, line, "点击屏幕事件", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<bool> HandleValidationCardPromptAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var prompt = page.Recognized.Lines.FirstOrDefault(line =>
            line.Text.Contains("不使用战利品验证卡", StringComparison.Ordinal)
            || line.Text.Contains("不使用戰利品驗證卡", StringComparison.Ordinal));
        if (prompt is null)
        {
            return false;
        }

        var confirm = FindText(
            page,
            new RelativeRegion(0.200, 0.500, 0.800, 0.780),
            GameText(page, "确认"));
        if (confirm is null)
        {
            return false;
        }
        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.ValidationCardPromptReasonId,
                "点击弹窗确认",
                (confirm.Region.X + (confirm.Region.Width / 2d)) / page.Frame.Width,
                (confirm.Region.Y + (confirm.Region.Height / 2d)) / page.Frame.Height,
                cancellationToken)
            .ConfigureAwait(false);

        if (!page.State.ValidationCardPromptDontShowClicked)
        {
            var checkbox = FindText(
                page,
                new RelativeRegion(0.250, 0.450, 0.750, 0.620),
                GameText(page, "下次登录前不再显示"));
            if (checkbox is not null)
            {
                var checkboxX = (checkbox.Region.X - Math.Max(8, checkbox.Region.Height * 0.5))
                                / page.Frame.Width;
                var checkboxY = (checkbox.Region.Y + (checkbox.Region.Height / 2d))
                                / page.Frame.Height;
                await ClickPointAsync(
                        page,
                        new RelativePoint(checkboxX, checkboxY),
                        "战利品验证卡弹窗：勾选下次不再显示",
                        cancellationToken)
                    .ConfigureAwait(false);
                page.State.ValidationCardPromptDontShowClicked = true;
            }
        }

        await ClickTextAsync(
                page,
                confirm,
                "战利品验证卡弹窗：确认继续",
                cancellationToken)
            .ConfigureAwait(false);
        page.State.ValidationCardPromptConfirmedAt = DateTimeOffset.UtcNow;
        return true;
    }
    private static async ValueTask<bool> HandleSharedConfirmationAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var confirmation = FindCenterConfirmation(page);
        if (confirmation is null)
        {
            return false;
        }

        var observation = page.Observation;
        var decision = page.ActionArbiter.ObserveConfirmation(observation);
        if (decision.Kind == ActionDecisionKind.ExecuteConfirm
            && decision.Command is not null)
        {
            await ExecuteInputCommandAsync(page, decision.Command, cancellationToken)
                .ConfigureAwait(false);
            if (page.State.AwaitingRestConfirmation)
            {
                page.State.FlashOrRest = false;
                page.State.AwaitingRestConfirmation = false;
                page.State.RestConfirmationStartedAt = default;
            }

            return true;
        }

        if (decision.Kind is ActionDecisionKind.SuppressDuplicate or ActionDecisionKind.Wait)
        {
            page.Context.LogInfo(decision.Reason, "activity");
            return true;
        }

        return false;
    }
    private static CenterConfirmation? FindCenterConfirmation(ChaosModePage page)
    {
        var region = CenterConfirmationProbeRegion;
        var line = FindText(page, region, GameText(page, "确认"));
        if (line is null)
        {
            return null;
        }

        var cancel = FindText(page, region, GameText(page, "取消"));
        if (cancel is null)
        {
            page.Context.LogDebug("检测到孤立的“确认”文字但没有同一行的“取消”按钮，不按中心确认弹窗处理。");
            return null;
        }

        var confirmY = (line.Region.Y + (line.Region.Height / 2d)) / page.Frame.Height;
        var cancelY = (cancel.Region.Y + (cancel.Region.Height / 2d)) / page.Frame.Height;
        if (Math.Abs(confirmY - cancelY) > 0.08)
        {
            page.Context.LogDebug("“确认”和“取消”不在同一按钮行，不按中心确认弹窗处理。");
            return null;
        }

        if (!IsButtonActive(page.Frame, line))
        {
            page.Context.LogInfo("页面中央确认按钮未激活，跳过点击。", "activity");
            return null;
        }

        var point = new RelativePoint(
            (line.Region.X + (line.Region.Width / 2d)) / page.Frame.Width,
            (line.Region.Y + (line.Region.Height / 2d)) / page.Frame.Height).Clamp();
        return new CenterConfirmation(point);
    }

    // Python handle_confirm (utils.py:3319)：通用"确认"按钮，灰色禁用时不点。
    private static async ValueTask<bool> HandleConfirmAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindFooterConfirmBox(page);
        if (line is null)
        {
            return false;
        }

        if (!IsButtonActive(page.Frame, line))
        {
            page.Context.LogInfo("确认按钮未激活，跳过点击。", "activity");
            return false;
        }

        await ClickFooterConfirmAsync(page, line, "检测到确认按钮", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static OcrTextLine? FindActiveFooterConfirmBox(ChaosModePage page)
    {
        var line = FindFooterConfirmBox(page);
        return line is not null && IsButtonActive(page.Frame, line) ? line : null;
    }

    private static async ValueTask ClickFooterConfirmAsync(
        ChaosModePage page,
        OcrTextLine line,
        string message,
        CancellationToken cancellationToken)
    {
        var ocrCenter = new RelativePoint(
            (line.Region.X + (line.Region.Width / 2d)) / page.Frame.Width,
            (line.Region.Y + (line.Region.Height / 2d)) / page.Frame.Height).Clamp();
        var clickPoint = ocrCenter.X >= 0.85 && ocrCenter.Y >= 0.90
            ? FooterConfirmButtonHotspot
            : ocrCenter;
        if (clickPoint != ocrCenter)
        {
            page.Context.LogDebug(
                $"确认按钮 OCR 中心=({ocrCenter.X:0.000}, {ocrCenter.Y:0.000})，使用底部确认控件稳定热点 ({clickPoint.X:0.000}, {clickPoint.Y:0.000})。");
        }

        await ClickPointAsync(page, clickPoint, message, cancellationToken)
            .ConfigureAwait(false);
    }

    // Python handle_leave (utils.py:4286)：右下角"离开"按钮，灰色禁用时不点。
    private static async ValueTask<bool> HandleLeaveAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.945, 0.918);
        if (line is null || !MatchesAny(page, line, "离开"))
        {
            return false;
        }

        if (!IsButtonActive(page.Frame, line))
        {
            page.Context.LogInfo("离开按钮未激活，跳过点击。", "activity");
            return false;
        }

        await ClickTextAsync(page, line, "检测到离开按钮", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_next_step (utils.py:4299)：区域(0.833,0.885,0.954,0.957)内
    // 文本与"下一步"编辑距离 <=2 即点击。
    private static async ValueTask<bool> HandleNextStepAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var region = new RelativeRegion(0.833, 0.885, 0.954, 0.957);
        var line = page.Recognized.Lines.FirstOrDefault(
            candidate => region.Contains(candidate, page.Frame.Width, page.Frame.Height)
                         && EditDistanceWithin(candidate.Text, "下一步", 2));
        if (line is null)
        {
            return false;
        }

        await ClickTextAsync(page, line, "检测到下一步按钮", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_settlement (utils.py:2057)：右下角"结算"按钮，点击固定相对坐标。
    private static async ValueTask<bool> HandleSettlementAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.941, 0.917);
        if (line is null || !MatchesAny(page, line, "结算"))
        {
            return false;
        }
        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.BattleSettlementReasonId,
                "点击结算",
                0.941,
                0.917,
                cancellationToken)
            .ConfigureAwait(false);

        await ClickPointAsync(
                page,
                new RelativePoint(0.941, 0.917),
                "检测到结算按钮",
                cancellationToken)
            .ConfigureAwait(false);
        page.State.BattleEvent.Complete();
        // 对应 Python：结算页若已标记 reach_final_boss，说明刚通关底，层数 +1 并重置层状态。
        if (page.State.ReachFinalBoss)
        {
            page.State.PassFinalBossCount++;
            page.Context.LogInfo(
                $"检测到boss结算页面且 reach_final_boss=True，通关层数+1 (当前: {page.State.PassFinalBossCount})",
                "activity");
            page.State.ResetLayer();
        }

        return true;
    }

    // Python handle_chaos_reward_settlement (utils_chaos.py:1009)：卡厄思奖励结算页。
    // 默认不领取奖励时，改由 _finish_only_first_layer 判断是否退出重刷/结束第一层。
    private static async ValueTask<bool> HandleChaosRewardSettlementAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var title = FindBoxAtPoint(page, 0.552, 0.067);
        if (title is null || !ContainsAny(page, title, "结算"))
        {
            return false;
        }

        page.Context.LogInfo("检测到卡厄思奖励结算页面", "activity");

        if (!page.Context.GetTaskConfig("领取奖励(只使用验证卡)", false))
        {
            page.Context.LogInfo("领取奖励(只使用验证卡)配置为False", "activity");
            return page.State.IsEscaped
                ? false
                : await TryFinishOnlyFirstLayerAsync(page, cancellationToken).ConfigureAwait(false);
        }

        var reward = FindBoxAtPoint(page, 0.851, 0.389);
        if (reward is null || !ContainsAny(page, reward, "获得"))
        {
            page.Context.LogInfo("未检测到获得按钮", "activity");
            return page.State.IsEscaped
                ? false
                : await TryFinishOnlyFirstLayerAsync(page, cancellationToken).ConfigureAwait(false);
        }

        page.Context.LogInfo("检测到获得按钮，点击获得", "activity");
        await ClickTextAsync(page, reward, "领取卡厄思奖励", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_chaos_reward_claim (utils_chaos.py:971)：卡厄思奖励领取页。
    private static async ValueTask<bool> HandleChaosRewardClaimAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        if (IsFlashEffectPage(page))
        {
            return false;
        }

        var claim = FindBoxAtPoint(page, 0.568, 0.711);
        if (claim is null || !ContainsAny(page, claim, "获得"))
        {
            return false;
        }

        page.Context.LogInfo("检测到卡厄思模式奖励领取页面", "activity");
        var verify = FindBoxAtPoint(page, 0.959, 0.281);
        var verifyMatch = verify is null
            ? Match.Empty
            : Regex.Match(verify.Text, @"(\d+)/(\d+)", RegexOptions.CultureInvariant);
        if (verifyMatch.Success
            && int.TryParse(verifyMatch.Groups[1].Value, out var currentCards)
            && int.TryParse(verifyMatch.Groups[2].Value, out var maxCards))
        {
            page.Context.LogInfo($"战利品验证卡: {currentCards}/{maxCards}", "activity");
            if (currentCards > 0)
            {
                page.Context.LogInfo($"当前战利品验证卡{currentCards}>0，点击获得", "activity");
                await ClickTextAsync(page, claim, "领取卡厄思战利品", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            page.Context.LogInfo("当前战利品验证卡为0，点击取消", "activity");
        }
        else
        {
            page.Context.LogInfo("未检测到战利品验证卡信息，点击取消", "activity");
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.352, 0.708),
                "取消领取卡厄思战利品",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python _finish_only_first_layer (utils.py:5408)：只打第一层的统一退出判断。
    private static async ValueTask<bool> TryFinishOnlyFirstLayerAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        if (page.State.PassFinalBossCount < 1)
        {
            return false;
        }

        if (page.Context.GetTaskConfig("首层刷特定闪光", false)
            && !page.State.GetSpecificFlash)
        {
            page.State.SuccessRounds++;
            page.Context.LogInfo("未刷到指定闪光且已通关第一层，退出重刷", "activity");
            await ClickPointAsync(
                    page,
                    new RelativePoint(0.959, 0.051),
                    "退出第一层重新刷取特定闪光",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (page.Context.GetTaskConfig("只打第一层", false))
        {
            page.State.SuccessRounds++;
            page.Context.LogInfo(
                $"只打第一层任务已完成，success_rounds + 1 (当前: {page.State.SuccessRounds})，退出结算页面",
                "activity");
            await ClickPointAsync(
                    page,
                    new RelativePoint(0.959, 0.051),
                    "退出结算页面",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        return false;
    }
    // Python handle_continue (utils.py:3293)：通用"继续"按钮。
    private static async ValueTask<bool> HandleContinueAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindText(
            page,
            new RelativeRegion(0.459, 0.858, 0.992, 0.988),
            GameText(page, "继续"));
        if (line is null)
        {
            return false;
        }

        if (page.State.ValidationCardPromptConfirmedAt is { } confirmedAt
            && DateTimeOffset.UtcNow - confirmedAt < TimeSpan.FromSeconds(12))
        {
            page.Context.LogInfo("战利品验证卡已确认，等待页面切换，暂不点击继续。", "activity");
            return false;
        }

        await ClickTextAsync(page, line, "检测到继续按钮", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_enter (utils.py:3467)：通用"进入"按钮。
    private static async ValueTask<bool> HandleEnterAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindText(
            page,
            new RelativeRegion(0.017, 0.771, 0.996, 0.992),
            GameText(page, "进入"));
        if (line is null)
        {
            return false;
        }

        await ClickTextAsync(page, line, "检测到进入按钮", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_skip (utils.py:2072)：右下角"跳过"按钮。
    private static async ValueTask<bool> HandleSkipAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindTextPreferSmallest(
            page,
            new RelativeRegion(0.620, 0.883, 0.990, 0.983),
            GameText(page, "跳过"));
        if (line is null)
        {
            return false;
        }

        await ClickSkipTextAsync(page, line, "检测到跳过按钮", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_convert (utils.py:3347)：通用"转换"按钮；未激活时记录现场，禁止固定坐标兜底。
    private static async ValueTask<bool> HandleConvertAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.945, 0.918);
        if (line is null || !MatchesAny(page, line, "转换"))
        {
            return false;
        }

        if (IsButtonActive(page.Frame, line))
        {
            page.State.ResetRecognitionMiss(ConvertInactiveActionKey);
            await ClickTextAsync(page, line, "检测到转换按钮", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        await HandleRecognitionFailureAsync(
                page,
                ConvertInactiveActionKey,
                "转换按钮仍不可用",
                new RelativePoint(0.776, 0.926),
                cancellationToken)
            .ConfigureAwait(false);
        return false;
    }

    // Python handle_close_button (utils.py:5489)：通用"关闭"按钮。
    private static async ValueTask<bool> HandleCloseButtonAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.512, 0.929);
        if (line is null || !MatchesAny(page, line, "关闭"))
        {
            return false;
        }

        await ClickTextAsync(page, line, "检测到关闭按钮", cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Python handle_flash (utils.py:3411)：通用"闪光"按钮。
    // 注意：Python 原版用子串包含（'闪光' in name），会把"重新闪光""赋予闪光"
    // 一起吃掉，导致 handle_reflash/handle_grant_flash 不可达。这里改为精确匹配，
    // 让这三个按钮各走各的分支（"闪光"本身不受影响）。
    private static ValueTask<bool> HandleFlashAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
        => TryActiveButtonAsync(
            page, 0.945, 0.918, "闪光", "闪光", substringMatch: false, cancellationToken,
            snapshotReasonId: DecisionSnapshotCatalog.FlashReasonId);

    // Python handle_reflash (utils.py:3425)：通用"重新闪光"按钮，点击后等待 2 秒。
    private static ValueTask<bool> HandleReflashAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
        => TryActiveButtonAsync(
            page,
            0.945,
            0.918,
            "重新闪光",
            "重新闪光",
            substringMatch: true,
            cancellationToken,
            snapshotReasonId: DecisionSnapshotCatalog.ReflashReasonId);

    // Python handle_grant_flash (utils.py:3439)：通用"赋予闪光"按钮。
    private static ValueTask<bool> HandleGrantFlashAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
        => TryActiveButtonAsync(
            page, 0.945, 0.918, "赋予闪光", "赋予闪光", substringMatch: false, cancellationToken,
            snapshotReasonId: DecisionSnapshotCatalog.GrantFlashReasonId);

    // Python handle_copy (utils.py:3453)：通用"复制"按钮。
    private static ValueTask<bool> HandleCopyAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
        => TryActiveButtonAsync(
            page, 0.945, 0.918, "复制", "复制", substringMatch: false, cancellationToken);

    // Python handle_craft (utils.py:4314)：通用"合成"按钮。
    private static ValueTask<bool> HandleCraftAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
        => TryActiveButtonAsync(
            page, 0.938, 0.903, "合成", "合成", substringMatch: false, cancellationToken);

    // Python handle_select (utils.py:4328)：通用"选择"按钮。
    private static ValueTask<bool> HandleSelectAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
        => TryActiveButtonAsync(
            page, 0.945, 0.918, "选择", "选择", substringMatch: false, cancellationToken);

    // Python handle_equipment_recast (utils.py:3494)：装备重铸页面，点固定位置跳过。
    private static async ValueTask<bool> HandleEquipmentRecastAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.501, 0.128);
        if (line is null || !ContainsAny(page, line, "装备重铸"))
        {
            return false;
        }

        await ClickSkipPointAsync(
                page,
                new RelativePoint(0.749, 0.932),
                "检测到装备重铸页面，点击跳过",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_minimizemap (utils.py:5729)：检测到小地图按钮时点击关闭。
    private static async ValueTask<bool> HandleMinimizemapAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var feature = FindFeatureOne(page, "minimizemap");
        if (feature is null)
        {
            return false;
        }

        var point = new RelativePoint(
            feature.CenterX(page.Frame.Width),
            feature.CenterY(page.Frame.Height));
        await ClickPointAsync(
                page,
                point,
                "检测到地图页面，点击关闭小地图",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_battle_crash (utils.py:1999)：战斗信息错乱或通讯不稳时点击屏幕中央。
    private static async ValueTask<bool> HandleBattleCrashAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var matched = page.Recognized.Lines.Any(line =>
            line.Text.Contains("出现错乱", StringComparison.Ordinal)
            || line.Text.Contains("点击重试", StringComparison.Ordinal)
            || Regex.IsMatch(
                line.Text,
                "通讯不稳定.*重新尝试",
                RegexOptions.CultureInvariant));
        if (!matched)
        {
            return false;
        }
        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.CenterSkipReasonId,
                "点击画面中心恢复",
                0.5,
                0.5,
                cancellationToken)
            .ConfigureAwait(false);

        await ClickPointAsync(
                page,
                new RelativePoint(0.5, 0.5),
                "战斗信息出现错乱，点击恢复",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
    // Python handle_weakness_info (utils.py:5720)：怪物弱点信息页，点固定位置关闭。
    private static async ValueTask<bool> HandleWeaknessInfoAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.387, 0.107);
        if (line is null || !ContainsAny(page, line, "弱点"))
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.502, 0.092),
                "检测到怪物信息页面，点击关闭",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_obtain_reward (utils.py:4275)：获得奖励页面，点击领取。
    private static async ValueTask<bool> HandleObtainRewardAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.924, 0.922);
        if (line is null || !MatchesAny(page, line, "获得"))
        {
            return false;
        }

        await ClickTextAsync(page, line, "检测到获得奖励页面，点击领取", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Boss equipment rewards are claimed before equipment choice and member allocation.
    private static async ValueTask<bool> HandleBossEquipmentRewardAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var claimBox = FindText(
            page,
            new RelativeRegion(0.850, 0.865, 0.995, 0.975),
            GameText(page, "\u83B7\u5F97"));
        if (claimBox is null)
        {
            return false;
        }

        page.Context.LogInfo(
            "\u68C0\u6D4B\u5230 Boss \u88C5\u5907\u5956\u52B1\uFF0C\u5148\u9886\u53D6\u88C5\u5907\uFF0C\u968F\u540E\u8FDB\u5165\u4E8C\u9009\u4E00\u548C\u4E3B\u6218\u5458\u5206\u914D\u3002",
            "activity");
        await ClickTextAsync(
                page,
                claimBox,
                "\u9886\u53D6 Boss \u88C5\u5907\u5956\u52B1",
                cancellationToken)
            .ConfigureAwait(false);
        page.State.BossEquipmentRewardAwaitingAllocation = true;
        return true;
    }
    // Python handle_go_to_chaos_core (utils_chaos.py)：前往卡厄思核心按钮。
    private static ValueTask<bool> HandleGoToChaosCoreAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
        => TryActiveButtonAsync(
            page, 0.945, 0.918, "前往卡厄思核心", "前往卡厄思核心", substringMatch: false, cancellationToken);

    // Python handle_zero_system_home (utils_chaos.py:446)：零式系统首页，点击"法典"。
    private static async ValueTask<bool> HandleZeroSystemHomeAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var title = FindBoxAtPoint(page, 0.120, 0.046);
        var codex = FindBoxAtPoint(page, 0.812, 0.469);
        if (title is null
            || !ContainsAny(page, title, "零式系统")
            || codex is null
            || !MatchesAny(page, codex, "法典"))
        {
            return false;
        }

        await ClickTextAsync(
                page,
                codex,
                "检测到零式系统首页，点击法典",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_codex_search (utils_chaos.py:458)：法典搜索页，点击搜索新坐标。
    private static async ValueTask<bool> HandleCodexSearchAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var title = FindBoxAtPoint(page, 0.5, 0.438);
        if (title is null || !MatchesAny(page, title, "法典"))
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.5, 0.760),
                "检测到法典搜索页面，点击搜索新坐标",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_expedition_unlock (utils_chaos.py:889)：解锁探险记录页面。
    private static async ValueTask<bool> HandleExpeditionUnlockAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.5, 0.151);
        if (line is null || !ContainsAny(page, line, "解锁的探险记录"))
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.5, 0.95),
                "检测到解锁探险记录页面，点击页面",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_memory_elimination (utils_chaos.py:469)：记忆消除页面。
    private static async ValueTask<bool> HandleMemoryEliminationAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.589, 0.703);
        if (line is null || !ContainsAny(page, line, "记忆消除"))
        {
            return false;
        }

        await ClickTextAsync(
                page,
                line,
                "检测到记忆消除页面，点击记忆消除",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_chaos_craft (utils_chaos.py:480)：卡厄思合成页面。
    private static async ValueTask<bool> HandleChaosCraftAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.774, 0.925);
        if (line is not null && ContainsAny(page, line, "卡厄思合成"))
        {
            await ClickTextAsync(
                    page,
                    line,
                    "检测到卡厄思合成页面",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        line = FindBoxAtPoint(page, 0.563, 0.922);
        if (line is not null && ContainsAny(page, line, "免费合成"))
        {
            await ClickTextAsync(
                    page,
                    line,
                    "检测到卡厄思合成页面",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        return false;
    }

    // Python handle_conquer_difficulty (utils_chaos.py:497)：征服新难度页面。
    private static async ValueTask<bool> HandleConquerDifficultyAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.502, 0.572);
        if (line is null || !ContainsAny(page, line, "征服新难度"))
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.502, 0.943),
                "检测到征服新难度页面，点击关闭",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_treating (utils_chaos.py:938)：治疗进行中页面。
    private static async ValueTask<bool> HandleTreatingAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindTextAnywhere(page, GameText(page, "选择哪种方法进行治疗"));
        if (line is null)
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.765, 0.500),
                "检测到治疗进行中",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_treat_approve (utils_chaos.py:947)：治疗完成页面，点击批准。
    private static async ValueTask<bool> HandleTreatApproveAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindTextAnywhere(page, GameText(page, "点击批准"));
        if (line is null)
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.768, 0.810),
                "检测到治疗完成，点击批准",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_held_cards_page (utils.py:5711)：持有卡牌页面，点击关闭。
    private static async ValueTask<bool> HandleHeldCardsPageAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.500, 0.056);
        if (line is null || !MatchesAny(page, line, "持有卡牌"))
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.966, 0.053),
                "检测到持有卡牌页面，点击关闭",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_stage_end_data_details (utils_chaos.py:868)：关卡结束数据详情页。
    private static async ValueTask<bool> HandleStageEndDataDetailsAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var region = new RelativeRegion(0.111, 0.103, 0.338, 0.243);
        var text = RegionText(page, region);
        if (!GameText(page, "存储数据").Any(expected => text.Contains(expected, StringComparison.Ordinal)))
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.883, 0.156),
                "检测到关卡结束数据详情页面，点击关闭",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
    // 逃脱页面必须由命令总线明确下发逃脱指令；没有指令时只退出页面，不结束本局。
    private static async ValueTask<bool> HandleEscapeAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.952, 0.928);
        if (line is null
            || (!ContainsAny(page, line, "逃脱") && !ContainsAny(page, line, "脱逃")))
        {
            return false;
        }

        if (!page.Commands.TryConsumeEscape(out var command))
        {
            var cancel = FindTextAnywhere(page, GameText(page, "取消"));
            if (cancel is null)
            {
                return false;
            }

            await ClickTextAsync(
                    page,
                    cancel,
                    "未收到逃脱指令，取消逃脱并返回",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        await ClickTextAsync(
                page,
                line,
                $"收到逃脱指令（{command!.Reason}），点击逃脱",
                cancellationToken)
            .ConfigureAwait(false);
        page.State.IsEscaped = true;
        return true;
    }
    // Python handle_refine_equipment_credit (utils.py:2020)：提炼装备信用点页面。
    private static async ValueTask<bool> HandleRefineEquipmentCreditAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.598, 0.635);
        if (line is null || !ContainsAny(page, line, "以信用点接收"))
        {
            return false;
        }

        await ClickTextAsync(
                page,
                line,
                "检测到提炼装备信用点页面，点击以信用点接收",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_convert_card (utils.py:3271)：转换卡牌页面，依次点两处跳过。
    private static async ValueTask<bool> HandleConvertCardAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.226, 0.046);
        if (line is null || !ContainsAny(page, line, "转换的卡牌"))
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.776, 0.926),
                "检测到卡牌转换选择，点击跳过（第一步）",
                cancellationToken)
            .ConfigureAwait(false);
        await ClickPointAsync(
                page,
                new RelativePoint(0.661, 0.632),
                "检测到卡牌转换选择，点击跳过（第二步）",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python handle_negotiation (utils.py:3283)：谈判失败页，点下一步跳过。
    // 注：Python 写的是 `title.name in "失败"`（方向写反的笔误），此处按本意用"包含失败"实现。
    private static async ValueTask<bool> HandleNegotiationAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var title = FindBoxAtPoint(page, 0.498, 0.683);
        if (title is null || !MatchesAny(page, title, "失败"))
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.665, 0.899),
                "检测到掷骰子失败，跳过掷骰子",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
    // Python handle_expedition_result (utils.py:5327)：探险结果页面，结算本局并重置单局状态。
    // 注意 Python 末尾是 return False —— 它不算"已处理"，外层会继续跑后续处理器。
    private static async ValueTask<bool> HandleExpeditionResultAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        if (!IsExpeditionResultPage(page))
        {
            return false;
        }
        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.ExpeditionResultReasonId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        page.Context.LogInfo("检测到探险结果页面", "activity");

        var complete = FindBoxAtPoint(page, 0.928, 0.122);
        var failed = FindBoxAtPoint(page, 0.296, 0.719);
        var state = page.State;
        var onlyFirstLayer = page.Context.GetTaskConfig("只打第一层", false);

        state.TotalRounds++;

        if (complete is not null && ContainsAny(page, complete, "完成"))
        {
            state.SuccessRounds++;
            page.Context.LogInfo("出击模式探险结果: 成功", "activity");
        }
        else if (complete is not null && ContainsAny(page, complete, "失败"))
        {
            if (!onlyFirstLayer)
            {
                page.Context.LogInfo("出击模式探险结果: 失败", "activity");
            }
            else if (state.PassFinalBossCount == 0)
            {
                page.Context.LogInfo("出击模式探险结果: 失败", "activity");
            }
        }
        else if (complete is null && failed is null)
        {
            if (onlyFirstLayer && state.PassFinalBossCount >= 1)
            {
                page.Context.LogInfo("卡厄思模式探险结果: 成功", "activity");
            }
            else if (!onlyFirstLayer && !state.IsEscaped)
            {
                state.SuccessRounds++;
                page.Context.LogInfo("卡厄思模式探险结果: 成功", "activity");
            }
            else
            {
                page.Context.LogInfo("卡厄思模式探险结果: 失败", "activity");
            }
        }
        else
        {
            page.Context.LogInfo("卡厄思模式探险结果: 失败", "activity");
        }

        page.Context.LogInfo(
            $"探险完成，成功次数/总次数={state.SuccessRounds}/{state.TotalRounds}",
            "activity");

        // TODO: Python 此处调用 engraving_log.end_run(...) 记录本局结果，
        // 刻印日志子系统尚未迁移，暂缺。

        state.ResetMission();
        page.ActionArbiter.MarkPageActionSent("expedition-result", page.Observation);
        return false;
    }

    private static bool IsExpeditionResultPage(ChaosModePage page)
    {
        var title = FindBoxAtPoint(page, 0.625, 0.122);
        return title is not null && ContainsAny(page, title, "探险结果");
    }

    // Python handle_mental_breakdown (utils_chaos.py:902)：精神崩溃页面，按配置决定是否去治疗。
    private static async ValueTask<bool> HandleMentalBreakdownAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var line = FindBoxAtPoint(page, 0.496, 0.186);
        if (line is null || !ContainsAny(page, line, "精神崩溃发生"))
        {
            return false;
        }

        // 配置"治疗崩溃"默认 True；为 False 时不处理，交给后续处理器。
        if (!page.Context.GetTaskConfig("治疗崩溃", true))
        {
            return false;
        }

        await ClickPointAsync(
                page,
                new RelativePoint(0.706, 0.915),
                "检测到精神崩溃发生，去创伤中心治疗",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
    private static async ValueTask ClickTextAsync(
        ChaosModePage page,
        OcrTextLine line,
        string message,
        CancellationToken cancellationToken)
    {
        var point = new RelativePoint(
            (line.Region.X + (line.Region.Width / 2d)) / page.Frame.Width,
            (line.Region.Y + (line.Region.Height / 2d)) / page.Frame.Height).Clamp();
        await ClickPointAsync(page, point, message, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask ExecuteInputCommandAsync(
        ChaosModePage page,
        InputCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Kind == InputCommandKind.ConfirmCenterDialog)
        {
            await CaptureDecisionSnapshotAsync(
                    page,
                    DecisionSnapshotCatalog.CenterConfirmReasonId,
                    "点击中心确认",
                    command.Target.X,
                    command.Target.Y,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await ClickPointAsync(
                page,
                command.Target,
                command.Purpose,
                cancellationToken)
            .ConfigureAwait(false);
        page.ActionArbiter.MarkCommandSent(command);
    }

    private static async ValueTask ClickSkipTextAsync(
        ChaosModePage page,
        OcrTextLine line,
        string message,
        CancellationToken cancellationToken)
    {
        var point = new RelativePoint(
            (line.Region.X + (line.Region.Width / 2d)) / page.Frame.Width,
            (line.Region.Y + (line.Region.Height / 2d)) / page.Frame.Height).Clamp();
        await ClickSkipPointAsync(page, point, message, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask ClickSkipPointAsync(
        ChaosModePage page,
        RelativePoint point,
        string message,
        CancellationToken cancellationToken)
    {
        var observation = page.Observation;
        var intent = new SkipCurrentContentIntent(
            IntentId.New(),
            CooldownKeys.SkipCurrentContent,
            page.FrameSequence,
            observation.Fingerprint,
            message);
        var decision = page.ActionArbiter.SubmitSkip(intent, observation, point);
        if (decision.Kind != ActionDecisionKind.ExecuteSkip || decision.Command is null)
        {
            page.Context.LogInfo(decision.Reason, "activity");
            return;
        }

        page.ExpectCenterConfirmationAfterAction(message);
        await ClickPointAsync(page, decision.Command.Target, message, cancellationToken)
            .ConfigureAwait(false);
        page.ActionArbiter.MarkCommandSent(decision.Command);
    }

    private static async ValueTask ClickPointAsync(
        ChaosModePage page,
        RelativePoint point,
        string message,
        CancellationToken cancellationToken)
    {
        var clamped = point.Clamp();
        page.Context.LogInfo($"{message}，点击 ({clamped.X:0.000}, {clamped.Y:0.000})。", "activity");
        await page.Context.ClickRelativeAsync(clamped, cancellationToken).ConfigureAwait(false);
        page.MarkInputDispatched();
    }

    private static async ValueTask PressKeyAsync(
        ChaosModePage page,
        InputKey key,
        string message,
        CancellationToken cancellationToken)
    {
        page.Context.LogInfo($"{message}，按键 {key}。", "activity");
        await page.Context.KeyAsync(key, cancellationToken).ConfigureAwait(false);
        page.MarkInputDispatched();
    }

    private static OcrTextLine? FindText(
        ChaosModePage page,
        RelativeRegion region,
        IReadOnlyList<string> expectedTexts)
        => page.Recognized.Lines.FirstOrDefault(
            line => region.Contains(line, page.Frame.Width, page.Frame.Height)
                    && expectedTexts.Any(expected => CleanMatch(line.Text, expected)));

    private static OcrTextLine? FindTextPreferSmallest(
        ChaosModePage page,
        RelativeRegion region,
        IReadOnlyList<string> expectedTexts)
        => page.Classification.FindOcrBoxes(region)
            .Where(line => expectedTexts.Any(expected => CleanMatch(line.Text, expected)))
            .OrderBy(line => (long)line.Region.Width * line.Region.Height)
            .FirstOrDefault();

    // Python find_text：全画面子串搜索（不限定区域、不做清理）。
    private static OcrTextLine? FindTextAnywhere(
        ChaosModePage page,
        IReadOnlyList<string> expectedTexts)
        => page.Recognized.Lines.FirstOrDefault(
            line => expectedTexts.Any(expected => line.Text.Contains(expected, StringComparison.Ordinal)));

    // Python find_box_at_point：返回包含该相对坐标点、且面积最小的文本行。
    private static OcrTextLine? FindBoxAtPoint(
        ChaosModePage page,
        double relativeX,
        double relativeY)
        => page.Classification.FindBoxAtPoint(relativeX, relativeY);

    private static IReadOnlyList<FeatureBox> FindFeatures(
        ChaosModePage page,
        string featureName,
        RelativeRegion? region = null,
        double? threshold = null)
        => page.Classification.FindFeatures(featureName, region, threshold);

    private static FeatureBox? FindFeatureOne(
        ChaosModePage page,
        string featureName,
        RelativeRegion? region = null,
        double? threshold = null)
        => FindFeatures(page, featureName, region, threshold)
            .OrderByDescending(feature => feature.Confidence)
            .FirstOrDefault();

    private static bool MatchesAny(ChaosModePage page, OcrTextLine line, string simplified)
        => GameText(page, simplified).Any(expected => CleanMatch(line.Text, expected));

    // Python 部分处理器用 `子串 in box.name` 判断（不做清理），对应这个方法。
    private static bool ContainsAny(ChaosModePage page, OcrTextLine line, string simplified)
        => GameText(page, simplified).Any(expected => line.Text.Contains(expected, StringComparison.Ordinal));

    // Python 里一批"右下角实体按钮"处理器结构完全一致：
    // 按点取框 -> 匹配文案 -> 判断是否激活 -> 点击。这里统一实现，避免重复。
    private static async ValueTask<bool> TryActiveButtonAsync(
        ChaosModePage page,
        double probeX,
        double probeY,
        string term,
        string label,
        bool substringMatch,
        CancellationToken cancellationToken,
        string? snapshotReasonId = null)
    {
        var line = FindBoxAtPoint(page, probeX, probeY);
        if (line is null)
        {
            return false;
        }

        var matched = substringMatch ? ContainsAny(page, line, term) : MatchesAny(page, line, term);
        if (!matched)
        {
            return false;
        }

        if (!IsButtonActive(page.Frame, line))
        {
            page.Context.LogInfo($"{label}按钮未激活，跳过点击。", "activity");
            return false;
        }

        if (snapshotReasonId is not null)
        {
            await CaptureDecisionSnapshotAsync(
                    page,
                    snapshotReasonId,
                    label,
                    (line.Region.X + (line.Region.Width / 2d)) / page.Frame.Width,
                    (line.Region.Y + (line.Region.Height / 2d)) / page.Frame.Height,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await ClickTextAsync(page, line, label, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Python _get_region_text：把区域内所有 OCR 文本去空白后拼接返回。
    private static string RegionText(ChaosModePage page, RelativeRegion region)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var line in page.Recognized.Lines)
        {
            if (region.Contains(line, page.Frame.Width, page.Frame.Height))
            {
                builder.Append(line.Text.Trim());
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> GameText(ChaosModePage page, string simplified)
    {
        var traditional = string.Equals(
            page.Context.GetTaskConfig(GameLanguageKey, "简体中文"),
            TraditionalChinese,
            StringComparison.Ordinal);
        // C# OCR 直接返回客户端字形，因此同时容纳简繁字形；
        // 繁中客户端另有词汇差异（见 Python assets/game_text_map/zh_tw.py），额外补上。
        return simplified switch
        {
            "点击屏幕" when traditional => ["点击屏幕", "點擊屏幕", "点击画面", "點擊畫面"],
            "点击屏幕" => ["点击屏幕", "點擊屏幕"],
            "纯白的记忆" when traditional => ["纯白的记忆", "純白的記憶", "纯白记忆", "純白記憶"],
            "纯白的记忆" => ["纯白的记忆", "純白的記憶"],
            "选择铭刻的记忆" when traditional => ["选择铭刻的记忆", "選擇銘刻的記憶", "选择铭刻记忆", "選擇銘刻記憶"],
            "选择铭刻的记忆" => ["选择铭刻的记忆", "選擇銘刻的記憶"],
            "铭刻的记忆" when traditional => ["铭刻的记忆", "銘刻的記憶", "铭刻记忆", "銘刻記憶"],
            "铭刻的记忆" => ["铭刻的记忆", "銘刻的記憶"],
            "领受计划的指示" when traditional => ["领受计划的指示", "領受計劃的指示", "领受计划的指示", "領受計畫的指示"],
            "领受计划的指示" => ["领受计划的指示", "領受計劃的指示"],
            "精密加工记忆" when traditional => ["精密加工记忆", "精密加工記憶"],
            "精密加工记忆" => ["精密加工记忆", "精密加工記憶"],
            "加工记忆" when traditional => ["加工记忆", "加工記憶"],
            "加工记忆" => ["加工记忆", "加工記憶"],
            "重新搜索" when traditional => ["重新搜索", "重新搜尋", "重新探索"],
            "重新搜索" => ["重新搜索", "重新搜尋"],
            "前往记忆之末" when traditional => ["前往记忆之末", "前往記憶之末"],
            "前往记忆之末" => ["前往记忆之末", "前往記憶之末"],
            "主战员" => ["主战员", "主戰員"],
            "继续" when traditional => ["继续", "繼續", "继续进行", "繼續進行"],
            "继续" => ["继续", "繼續"],
            "闪光" when traditional => ["闪光", "閃光", "灵光一闪", "靈光一閃"],
            "闪光" => ["闪光", "閃光"],
            "重新闪光" when traditional => ["重新闪光", "重新閃光", "再次灵光", "再次靈光"],
            "重新闪光" => ["重新闪光", "重新閃光"],
            "装备重铸" when traditional => ["装备重铸", "裝備重鑄", "重铸装备", "重鑄裝備"],
            "装备重铸" => ["装备重铸", "裝備重鑄"],
            "零式系统" when traditional => ["零式系统", "零式系統", "零号系统", "零號系統"],
            "零式系统" => ["零式系统", "零式系統"],
            "记忆消除" when traditional => ["记忆消除", "記憶消除", "消除记忆", "消除記憶"],
            "记忆消除" => ["记忆消除", "記憶消除"],
            "解锁的探险记录" when traditional =>
                ["解锁的探险记录", "解鎖的探險記錄", "解锁的探索纪录", "解鎖的探索紀錄"],
            "解锁的探险记录" => ["解锁的探险记录", "解鎖的探險記錄"],
            "选择哪种方法进行治疗" when traditional =>
                ["选择哪种方法进行治疗", "選擇哪種方法進行治療", "选择什么方法来治疗", "選擇什麼方法來治療"],
            "选择哪种方法进行治疗" => ["选择哪种方法进行治疗", "選擇哪種方法進行治療"],
            "点击批准" when traditional => ["点击批准", "點擊批准", "点击以批准", "點擊以批准"],
            "点击批准" => ["点击批准", "點擊批准"],
            "存储数据" when traditional => ["存储数据", "存儲數據", "存档资料", "存檔資料"],
            "存储数据" => ["存储数据", "存儲數據"],
            "确认" => ["确认", "確認"],
            "进入" => ["进入", "進入"],
            "结算" => ["结算", "結算"],
            "跳过" => ["跳过", "跳過"],
            "离开" => ["离开", "離開"],
            "转换" => ["转换", "轉換"],
            "关闭" => ["关闭", "關閉"],
            "赋予闪光" => ["赋予闪光", "賦予閃光"],
            "复制" => ["复制", "複製"],
            "合成" => ["合成"],
            "选择" => ["选择", "選擇"],
            "弱点" => ["弱点", "弱點"],
            "获得" => ["获得", "獲得"],
            "前往卡厄思核心" => ["前往卡厄思核心"],
            "法典" => ["法典"],
            "卡厄思合成" => ["卡厄思合成"],
            "免费合成" => ["免费合成", "免費合成"],
            "征服新难度" => ["征服新难度", "征服新難度"],
            "持有卡牌" when traditional => ["持有卡牌", "持有中的卡牌"],
            "持有卡牌" => ["持有卡牌"],
            "逃脱" when traditional => ["逃脱", "逃脫", "撤退"],
            "逃脱" => ["逃脱", "逃脫"],
            "脱逃" => ["脱逃", "脫逃"],
            "以信用点接收" when traditional =>
                ["以信用点接收", "以信用點接收", "以信用点数接收", "以信用點數接收"],
            "以信用点接收" => ["以信用点接收", "以信用點接收"],
            "转换的卡牌" when traditional =>
                ["转换的卡牌", "轉換的卡牌", "变更的卡牌", "變更的卡牌"],
            "转换的卡牌" => ["转换的卡牌", "轉換的卡牌"],
            "失败" => ["失败", "失敗"],
            "探险结果" when traditional => ["探险结果", "探險結果", "探索结果", "探索結果"],
            "探险结果" => ["探险结果", "探險結果"],
            "完成" => ["完成"],
            "精神崩溃发生" when traditional =>
                ["精神崩溃发生", "精神崩潰發生", "触发精神异常", "觸發精神異常"],
            "精神崩溃发生" => ["精神崩溃发生", "精神崩潰發生"],
            "移除卡牌" => ["移除卡牌"],
            "德朗商店" => ["德朗商店"],
            "免费" => ["免费", "免費"],
            "冥想" => ["冥想"],
            "购买卡牌" => ["购买卡牌", "購買卡牌"],
            "请选择要接受卡牌的主战员" => ["请选择要接受卡牌的主战员", "請選擇要接受卡牌的主戰員"],
            "请选择要复制的卡牌" when traditional =>
                ["请选择要复制的卡牌", "請選擇要複製的卡牌", "请选择要复制的卡片", "請選擇要複製的卡片"],
            "请选择要复制的卡牌" => ["请选择要复制的卡牌", "請選擇要複製的卡牌"],
            "选择要复制卡牌的主战员" when traditional =>
                ["选择要复制卡牌的主战员", "選擇要複製卡牌的主戰員", "选择要复制卡牌的战斗员", "選擇要複製卡牌的戰鬥員"],
            "选择要复制卡牌的主战员" => ["选择要复制卡牌的主战员", "選擇要複製卡牌的主戰員"],
            "请选择你的命运" => ["请选择你的命运", "請選擇你的命運"],
            "无法获得" => ["无法获得", "無法獲得"],
            "请决定闪光效果" when traditional => ["请决定闪光效果", "請決定閃光效果", "請決定閃光效果。"],
            "请决定闪光效果" => ["请决定闪光效果", "請決定閃光效果"],
            "请选择获得" => ["请选择获得", "請選擇欲獲得", "请选择欲获得", "請選擇獲得"],
            "刷新" => ["刷新"],
            "购买" => ["购买", "購買"],
            "取消" => ["取消"],
            "基础" => ["基础", "基礎"],
            "故事" => ["故事"],
            "营救" => ["营救", "營救"],
            "方舟城市" => ["方舟城市"],
            "获得法典" => ["获得法典", "獲得法典", "取得法典"],
            _ => [simplified]
        };
    }

    private static bool CleanMatch(string? actual, string target)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        return string.Equals(CleanText(actual), CleanText(target), StringComparison.Ordinal);
    }

    private static string CleanText(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if ((character >= '\u4e00' && character <= '\u9fff')
                || char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    // Python _edit_distance：判断实际文本与目标的编辑距离是否 <= maxDistance。
    private static bool EditDistanceWithin(string? actual, string target, int maxDistance)
    {
        actual ??= string.Empty;
        if (Math.Abs(actual.Length - target.Length) > maxDistance)
        {
            return false;
        }

        var previous = new int[target.Length + 1];
        for (var column = 0; column <= target.Length; column++)
        {
            previous[column] = column;
        }

        var current = new int[target.Length + 1];
        for (var row = 1; row <= actual.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= target.Length; column++)
            {
                var cost = actual[row - 1] == target[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[target.Length] <= maxDistance;
    }

    private static bool IsButtonActive(BgraFrame frame, OcrTextLine line)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || line.Region.Width <= 0 || line.Region.Height <= 0)
        {
            return false;
        }

        var leftWidth = (int)(line.Region.Width * 0.67);
        var leftHeight = (int)(line.Region.Height * 0.83);
        var leftX = line.Region.X - (int)(leftWidth * 1.1);
        var leftY = line.Region.Y + (int)(line.Region.Height * 0.1);
        leftX = Math.Clamp(leftX, 0, frame.Width);
        leftY = Math.Clamp(leftY, 0, frame.Height);
        leftWidth = Math.Min(leftWidth, frame.Width - leftX);
        leftHeight = Math.Min(leftHeight, frame.Height - leftY);
        if (leftWidth <= 0 || leftHeight <= 0)
        {
            return false;
        }

        long blue = 0;
        long green = 0;
        long red = 0;
        var count = leftWidth * leftHeight;
        for (var row = 0; row < leftHeight; row++)
        {
            var pixelOffset = ((leftY + row) * frame.Stride) + (leftX * 4);
            for (var column = 0; column < leftWidth; column++)
            {
                blue += frame.Pixels[pixelOffset];
                green += frame.Pixels[pixelOffset + 1];
                red += frame.Pixels[pixelOffset + 2];
                pixelOffset += 4;
            }
        }

        var averageBlue = (double)blue / count;
        var averageGreen = (double)green / count;
        var averageRed = (double)red / count;
        var inDisabledRange =
            averageBlue is >= 120 and <= 200
            && averageGreen is >= 120 and <= 200
            && averageRed is >= 120 and <= 200;
        var maximumDifference = Math.Max(
            Math.Abs(averageBlue - averageGreen),
            Math.Max(
                Math.Abs(averageGreen - averageRed),
                Math.Abs(averageRed - averageBlue)));
        return !(inDisabledRange && maximumDifference < 5);
    }

    private bool HandlePendingRestConfirmation(ChaosModePage page)
    {
        if (!page.State.AwaitingRestConfirmation)
        {
            return false;
        }

        if (page.Observation.HasCenterConfirmation)
        {
            return false;
        }

        if (DateTimeOffset.Now - page.State.RestConfirmationStartedAt < RestConfirmationTimeout)
        {
            page.Context.LogInfo("已发送休息操作，等待确认弹窗，暂不执行跳过或其他页面动作。", "activity");
            return true;
        }

        page.State.AwaitingRestConfirmation = false;
        page.State.RestConfirmationStartedAt = default;
        page.Context.LogInfo("等待休息确认超时，恢复当前页面识别。", "warning");
        return false;
    }

    private static string ResolveActionBoundaryKey(string routeId, ChaosModePage page)
    {
        if (string.Equals(routeId, "snow-season", StringComparison.Ordinal))
        {
            return $"{ResolveSnowSeasonActionStage(page)}-{page.State.SnowSeasonInputVersion}";
        }

        if (string.Equals(routeId, "select-card", StringComparison.Ordinal))
        {
            return $"select-card-{page.State.CardSelectionInputVersion}";
        }

        if (!string.Equals(routeId, "equipment", StringComparison.Ordinal))
        {
            return routeId;
        }

        var isInstallPage = IsEquipmentInstallPage(page);
        var stagePrefix = "equipment";
        var stageKey = page.State.EquipmentInstallAwaitingConfirm
            ? $"{stagePrefix}-confirm"
            : isInstallPage
                ? $"{stagePrefix}-install"
                : page.State.EquipmentChoiceAwaitingInstall
                    ? $"{stagePrefix}-awaiting-install"
                    : page.State.EquipmentChoiceConfirmAwaitingAllocation
                        ? $"{stagePrefix}-choice-confirm-pending"
                        : $"{stagePrefix}-choice";
        LogEquipmentStageEvidence(page, stageKey, isInstallPage);
        return stageKey;
    }

    private static void LogEquipmentStageEvidence(
        ChaosModePage page,
        string stageKey,
        bool isInstallPage)
    {
        var evidence = page.Recognized.Lines
            .Where(line => line.Text.Contains("装备", StringComparison.Ordinal)
                           || line.Text.Contains("主战员", StringComparison.Ordinal)
                           || line.Text.Contains("长按已佩戴", StringComparison.Ordinal)
                           || line.Text.Contains("長按已佩戴", StringComparison.Ordinal)
                           || line.Text.Contains("确认", StringComparison.Ordinal)
                           || line.Text.Contains("提炼", StringComparison.Ordinal))
            .Select(line =>
                $"{line.Text}@{(line.Region.X + (line.Region.Width / 2d)) / page.Frame.Width:0.000},{(line.Region.Y + (line.Region.Height / 2d)) / page.Frame.Height:0.000}");
        page.Context.LogDebug(
            $"[equipment-stage] 帧 #{page.FrameSequence} key={stageKey} awaitingInstall={page.State.EquipmentChoiceAwaitingInstall} awaitingConfirm={page.State.EquipmentInstallAwaitingConfirm} installPage={isInstallPage} 证据={string.Join(" | ", evidence)}");
    }

    private async ValueTask<bool> HandlePageAsync(
        ChaosModePage page,
        CancellationToken cancellationToken,
        string? preferredRouteId = null)
    {
        if (HandlePendingRestConfirmation(page))
        {
            return true;
        }

        if (await TryHandlePriorityOverlayAsync(page, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await HandleResumeTargetMemberArchiveAsync(page, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (await HandleSaveTargetMemberAsync(page, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (!IsRouteSelectionPage(page) && page.State.EnterNewNode)
        {
            page.State.EnterNewNode = false;
            page.State.NodeCount++;
            page.Context.LogInfo($"离开路线选择页面，当前节点计数: {page.State.NodeCount}", "activity");
        }

        var routes = _frameRouter.ResolveCandidates(page);
        if (!string.IsNullOrWhiteSpace(preferredRouteId))
        {
            var preferred = routes.FirstOrDefault(route =>
                string.Equals(route.Id, preferredRouteId, StringComparison.Ordinal));
            if (preferred is null)
            {
                return false;
            }

            routes = new[] { preferred }
                .Concat(routes.Where(route =>
                    !string.Equals(route.Id, preferred.Id, StringComparison.Ordinal)))
                .ToArray();
        }
        if (routes.All(route => !string.Equals(route.Id, "select-card", StringComparison.Ordinal)))
        {
            page.State.ResetCardSelectionSearch();
        }


        if (routes.Count == 0)
        {
            return false;
        }

        foreach (var route in routes)
        {
            if (await TryExecuteRouteAsync(page, route, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

        }

        // 所有命中路由都未执行动作，但这些帧已被当前页面路由认领。
        return true;
    }

    private async ValueTask<bool> TryHandlePriorityOverlayAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var route = _frameRouter.ResolvePriorityOverlay(page);
        return route is not null
               && await TryExecuteRouteAsync(page, route, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryExecuteRouteAsync(
        ChaosModePage page,
        FrameRoute<ChaosModePage> route,
        CancellationToken cancellationToken)
    {
        if (!route.UseActionBoundary)
        {
            var handledWithoutBoundary = await route
                .HandleAsync(page, cancellationToken)
                .ConfigureAwait(false);
            return handledWithoutBoundary || !route.AllowFallbackWhenUnhandled;
        }

        var actionKey = ResolveActionBoundaryKey(route.Id, page);
        var retryAfterFrames = route.ActionRetryAfterFrames?.Invoke(page);
        var recoveryAfterFrames = GetActionRecoveryWaitFrames(page);
        var actionDecision = page.ActionArbiter.TryBeginPageAction(
            actionKey,
            page.Observation,
            retryAfterFrames,
            PageActionMaxAttempts,
            route.Id,
            recoveryAfterFrames);
        if (actionDecision.Kind == PageActionDecisionKind.SuppressDuplicate)
        {
            page.Context.LogInfo(actionDecision.Reason, "activity");
            return true;
        }

        if (actionDecision.Kind == PageActionDecisionKind.Recover)
        {
            PreparePageActionRecovery(page, actionKey, actionDecision);
        }

        var inputCountBefore = page.DispatchedInputCount;
        var handled = await route.HandleAsync(page, cancellationToken).ConfigureAwait(false);
        if (page.DispatchedInputCount > inputCountBefore)
        {
            page.ActionArbiter.MarkPageActionSent(actionKey, page.Observation);
        }

        if (handled)
        {
            return true;
        }

        page.ActionArbiter.CancelPageAction(actionKey, page.Observation);
        return !route.AllowFallbackWhenUnhandled;
    }

    private static void PreparePageActionRecovery(
        ChaosModePage page,
        string actionKey,
        PageActionDecision decision)
    {
        page.State.ResetRecognitionMiss(actionKey);
        if (string.Equals(actionKey, "event-task", StringComparison.Ordinal))
        {
            ClearPendingEventOption(page.State);
        }

        var message = $"{decision.Reason} 已清理暂存状态，并重新识别当前帧。";
        if (decision.RecoveryCount <= 1)
        {
            page.Context.LogInfo(message, "warning");
        }
        else
        {
            page.Context.LogDebug(message);
        }
    }

    private sealed class ChaosModePage(
        TaskContext context,
        BgraFrame frame,
        long frameSequence,
        OcrFrameResult recognized,
        ChaosRunState state,
        ActionArbiter actionArbiter,
        ChaosCommandBus commandBus,
        FrameLease frameLease,
        Action? requestCenterConfirmationProbe = null) : IDisposable
    {
        private ChaosModeFrameClassification? _classification;

        public TaskContext Context { get; } = context;
        public BgraFrame Frame { get; } = frame;
        public long FrameSequence { get; private set; } = frameSequence;
        public OcrFrameResult Recognized { get; } = recognized;
        public ChaosModeFrameClassification Classification => _classification ??= new(this);
        public ActionArbiter ActionArbiter { get; } = actionArbiter;
        public ChaosCommandBus Commands { get; } = commandBus;
        public FrameLease FrameLease { get; } = frameLease;
        public PageObservation Observation { get; set; } = null!;

        public void DisposeFrame() => FrameLease.Dispose();

        public void Dispose() => DisposeFrame();

        public ChaosRunState State { get; } = state;
        public int DispatchedInputCount { get; private set; }
        public void MarkInputDispatched() => DispatchedInputCount++;

        public void ExpectCenterConfirmationAfterAction(string reason)
        {
            if (requestCenterConfirmationProbe is null)
            {
                return;
            }

            requestCenterConfirmationProbe();
            Context.LogDebug(
                $"[roi-probe] 帧 #{FrameSequence} 动作请求下一帧探测中心确认：{reason}。");
        }
    }

    private readonly record struct CenterConfirmation(RelativePoint Point);
    private readonly record struct RelativeRegion(
        double Left,
        double Top,
        double Right,
        double Bottom)
    {
        public bool Contains(OcrTextLine line, int frameWidth, int frameHeight)
        {
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                return false;
            }

            var centerX = (line.Region.X + (line.Region.Width / 2d)) / frameWidth;
            var centerY = (line.Region.Y + (line.Region.Height / 2d)) / frameHeight;
            return centerX >= Left
                   && centerX <= Right
                   && centerY >= Top
                   && centerY <= Bottom;
        }
    }
    // Python handle_route_selection (utils.py:4060)：识别当前可见节点，
    // 优先使用小地图规划，规划不可用时回退到当前节点优先级。
    private static bool IsRouteSelectionPage(ChaosModePage page)
    {
        var positionRegion = new RelativeRegion(0.335, 0.568, 0.453, 0.751);
        var position = FindFeatureOne(page, "position", positionRegion);
        var cannotReceive = FindBoxAtPoint(page, 0.186, 0.850);
        return position is not null
               || (cannotReceive is not null && ContainsAny(page, cannotReceive, "无法接收到梦境号"));
    }

    private static async ValueTask<bool> HandleRouteSelectionActionAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        page.State.BattleEvent.Complete();
        page.State.EnterNewNode = true;
        page.State.FlashOrRest = true;
        ClearPendingEventOption(page.State);
        if (page.Context.GetTaskConfig("进入商店", false))
        {
            page.State.Shop = true;
        }

        page.Context.LogInfo("检测到路线选择页面，按优先级选择节点。", "activity");

        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.RouteSelectionReasonId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var routeRegion = new RelativeRegion(0.656, 0.053, 0.977, 0.908);
        var nodes = page.Classification.RouteNodes;

        if (nodes.Count == 0)
        {
            page.State.ReachFinalBoss = true;
            page.State.NodeType = "boss";
            var pauseConfig = page.Context.GetTaskConfig("第几层boss前自动暂停", "不暂停");
            if (!string.Equals(pauseConfig, "不暂停", StringComparison.Ordinal)
                && int.TryParse(pauseConfig, out var pauseLayer)
                && pauseLayer - 1 == page.State.PassFinalBossCount
                && page.State.PauseRequestedBossLayer != pauseLayer)
            {
                page.Context.LogInfo(
                    $"配置在第{pauseLayer}层 Boss 前暂停（当前已通过{page.State.PassFinalBossCount}层）。",
                    "activity");
                if (page.Context.RequestPause($"第{pauseLayer}层 Boss 前自动暂停"))
                {
                    page.State.PauseRequestedBossLayer = pauseLayer;
                    return true;
                }
            }

            await ClickPointAsync(
                    page,
                    new RelativePoint(0.815, 0.492),
                    "检测到最终 Boss 节点，点击进入",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var specialPriorities = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["shop"] = -1,
            ["kalei"] = -1,
            ["seal"] = -1,
            ["hard"] = 1
        };
        foreach (var (specialName, _) in specialPriorities)
        {
            foreach (var specialBox in FindFeatures(page, specialName, routeRegion))
            {
                var specialX = specialBox.CenterX(page.Frame.Width);
                var specialY = specialBox.CenterY(page.Frame.Height);
                var nearest = nodes
                    .OrderBy(node => Math.Pow(RelativeCenter(node.Box, page).X - specialX, 2)
                                      + Math.Pow(RelativeCenter(node.Box, page).Y - specialY, 2))
                    .First();
                nearest.SpecialFeatures.Add(specialName);
            }
        }

        var priority = new ChaosModeConfig(page.Context.Config)
            .GetRoutePriority(page.State.PassFinalBossCount);
        page.Context.LogInfo(
            $"第{page.State.PassFinalBossCount + 1}层路线优先级: {string.Join(" > ", priority)}");

        RouteNode chosen;
        ChaosMapRoutePlan? routePlan = null;
        if (page.Context.FeatureMatcher is not null)
        {
            var mapInfo = ChaosMapRecognizer.Recognize(
                page.Frame,
                page.Context.FeatureMatcher,
                message => page.Context.LogInfo(message),
                new FeatureSearchRegion(0.019, 0.633, 0.380, 0.972));
            routePlan = ChaosMapRoutePlanner.FindBestRouteByPriority(mapInfo, priority);
        }

        if (routePlan is not null)
        {
            page.Context.LogInfo(
                $"小地图规划路线=[{string.Join(", ", routePlan.Route)}]，"
                + $"下一节点={routePlan.NextNodeId?.ToString() ?? "无"}，"
                + $"行={routePlan.NextRow?.ToString() ?? "无"}，"
                + $"预计={routePlan.NextNodeType ?? "无"}"
                + $"+[{string.Join(", ", routePlan.NextSpecialFeatures)}]。");
        }
        chosen = SelectVisibleRouteNode(nodes, priority, page);
        if (routePlan?.NextRow is int nextRow)
        {
            var visibleNodes = nodes
                .OrderBy(node => RelativeCenter(node.Box, page).Y)
                .ToArray();
            var plannedIndex = nextRow - 1;
            if (plannedIndex >= 0 && plannedIndex < visibleNodes.Length)
            {
                var plannedNode = visibleNodes[plannedIndex];
                var expectedSpecials = routePlan.NextSpecialFeatures
                    .Select(name => name.Replace("_in_map", string.Empty, StringComparison.Ordinal))
                    .ToHashSet(StringComparer.Ordinal);
                var actualSpecials = plannedNode.SpecialFeatures.ToHashSet(StringComparer.Ordinal);
                if (plannedNode.NodeType == routePlan.NextNodeType
                    && expectedSpecials.SetEquals(actualSpecials))
                {
                    chosen = plannedNode;
                    page.Context.LogInfo("小地图规划与当前节点识别一致，按规划结果进入。", "activity");
                }
                else
                {
                    page.Context.LogInfo("小地图规划与当前节点识别不一致，改用当前节点优先级兜底。");
                }
            }
            else
            {
                page.Context.LogInfo("小地图规划节点超出当前可见节点范围，改用优先级兜底。");
            }
        }
        else
        {
            page.Context.LogInfo("小地图路线规划失败，改用当前节点优先级兜底。");
        }

        var center = RelativeCenter(chosen.Box, page);
        var clickX = center.X - 0.095;
        var clickY = center.Y - 0.0065;
        page.State.NodeType = chosen.NodeType;
        page.Context.LogInfo(
            $"点击「{chosen.NodeType}」节点，特殊标志=[{string.Join(", ", chosen.SpecialFeatures)}]。",
            "activity");
        await ClickPointAsync(
                page,
                new RelativePoint(clickX, clickY),
                $"选择{chosen.NodeType}节点",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static RouteNode SelectVisibleRouteNode(
        IReadOnlyList<RouteNode> nodes,
        IReadOnlyList<string> priority,
        ChaosModePage page)
    {
        var priorityIndex = priority
            .Select((nodeType, index) => new { nodeType, index })
            .GroupBy(item => item.nodeType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().index, StringComparer.Ordinal);
        var specialPriorities = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["shop"] = -1,
            ["kalei"] = -1,
            ["seal"] = -1,
            ["hard"] = 1
        };
        var shopRank = priorityIndex.GetValueOrDefault("商店", -1);

        return nodes
            .OrderBy(node =>
            {
                var hasShop = node.SpecialFeatures.Contains("shop", StringComparer.Ordinal);
                var shopPriority = shopRank < 0 ? (hasShop ? 0 : 1) : 1;
                var typePriority = shopRank >= 0 && hasShop
                    ? shopRank
                    : priorityIndex.GetValueOrDefault(node.NodeType, priority.Count);
                if (node.NodeType == "结算")
                {
                    typePriority = priority.Count + 1;
                }

                var specialPriority = node.SpecialFeatures
                    .Select(name => specialPriorities.GetValueOrDefault(name, 0))
                    .DefaultIfEmpty(0)
                    .Min();
                var center = RelativeCenter(node.Box, page);
                return (shopPriority, typePriority, specialPriority, center.Y, center.X);
            })
            .First();
    }

    private static (double X, double Y) RelativeCenter(FeatureBox box, ChaosModePage page)
        => (box.CenterX(page.Frame.Width), box.CenterY(page.Frame.Height));
    // Python handle_save_target_member (utils_chaos.py:339)：从主界面进入信息统计页面。
    private async ValueTask<bool> HandleSaveTargetMemberAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var targetName = page.Context.GetTaskConfig("刷存档主战员", string.Empty).Trim();
        if (targetName.Length == 0
            || page.State.SaveTargetMember
            || _targetMemberArchiveFlow.IsActive)
        {
            return false;
        }

        if (GetCurrentHpPercent(page) is null)
        {
            return false;
        }

        _targetMemberArchiveFlow.BeginOpeningStatistics(DateTimeOffset.UtcNow);
        page.Context.LogInfo($"前往获取刷存档主战员「{targetName}」头像特征", "activity");
        await ClickPointAsync(
                page,
                new RelativePoint(0.182, 0.081),
                "打开信息统计页面",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // 启动时如果游戏已经停在主战员信息页，可直接恢复头像捕获，不必强制回首页重进。
    private async ValueTask<bool> HandleResumeTargetMemberArchiveAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var state = page.State;
        if (state.SaveTargetMember
            || _targetMemberArchiveFlow.IsActive
            || !LooksLikeTargetMemberListPage(page))
        {
            return false;
        }

        if (page.Context.GetTaskConfig("刷存档主战员", string.Empty).Trim().Length == 0)
        {
            return false;
        }

        _targetMemberArchiveFlow.BeginCapturingMemberList(DateTimeOffset.UtcNow);
        page.Context.LogInfo("检测到主战员信息页，恢复主战员头像捕获。", "activity");
        return await HandleTargetMemberArchiveAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private static bool LooksLikeTargetMemberListPage(ChaosModePage page)
    {
        var topLines = FindOcrBoxes(page, new RelativeRegion(0, 0, 1, 0.25));
        var hasMainMember = topLines.Any(line =>
            line.Text.Contains("主战员", StringComparison.Ordinal)
            || line.Text.Contains("主戰員", StringComparison.Ordinal));
        var hasCompanion = topLines.Any(line =>
            line.Text.Contains("辅战员", StringComparison.Ordinal)
            || line.Text.Contains("輔戰員", StringComparison.Ordinal)
            || line.Text.Contains("辅助员", StringComparison.Ordinal)
            || line.Text.Contains("輔助員", StringComparison.Ordinal));
        var hasDestiny = topLines.Any(line =>
            line.Text.Contains("命运", StringComparison.Ordinal)
            || line.Text.Contains("命運", StringComparison.Ordinal));
        var hasSetBonus = topLines.Any(line =>
            line.Text.Contains("套装效果", StringComparison.Ordinal)
            || line.Text.Contains("套裝效果", StringComparison.Ordinal));
        return hasMainMember && (hasCompanion || hasDestiny || hasSetBonus);
    }

    // Python handle_archive_target_member (utils_chaos.py:234)：保存目标主战员运行时头像特征。
    private async ValueTask<bool> HandleTargetMemberArchiveAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        if (!_targetMemberArchiveFlow.IsActive)
        {
            return false;
        }

        var targetName = page.Context.GetTaskConfig("刷存档主战员", string.Empty).Trim();
        if (targetName.Length == 0)
        {
            _targetMemberArchiveFlow.Fail();
            page.Context.LogInfo("刷存档主战员为空，已退出主战员头像捕获。", "warning");
            return true;
        }

        if (_targetMemberArchiveFlow.Phase == TargetMemberArchivePhase.OpeningStatistics)
        {
            var memberInfo = FindFeatureOne(
                    page,
                    "memberinfo",
                    new RelativeRegion(0.005, 0.018, 0.080, 0.343))
                ?? FindFeatureOne(
                    page,
                    "memberinfo2",
                    new RelativeRegion(0.005, 0.018, 0.080, 0.343));
            if (memberInfo is null)
            {
                var now = DateTimeOffset.UtcNow;
                if (!_targetMemberArchiveFlow.HasTimedOut(now))
                {
                    return true;
                }

                page.Context.LogInfo(
                    $"等待信息统计页面超时（{_targetMemberArchiveFlow.Elapsed(now).TotalSeconds:0.0}秒），已取消本次头像捕获。",
                    "warning");
                _targetMemberArchiveFlow.Fail();
                return true;
            }

            page.Context.LogInfo("检测到信息统计页面，打开主战员信息", "activity");
            await ClickPointAsync(
                    page,
                    new RelativePoint(memberInfo.CenterX(page.Frame.Width), memberInfo.CenterY(page.Frame.Height)),
                    "打开主战员信息",
                    cancellationToken)
                .ConfigureAwait(false);
            await ClickPointAsync(
                    page,
                    new RelativePoint(0.201, 0.056),
                    "切换到主战员列表",
                    cancellationToken)
                .ConfigureAwait(false);
            _targetMemberArchiveFlow.BeginCapturingMemberList(DateTimeOffset.UtcNow);
            return true;
        }

        if (_targetMemberArchiveFlow.Phase == TargetMemberArchivePhase.ClosingStatistics)
        {
            if (!LooksLikeTargetMemberListPage(page))
            {
                _targetMemberArchiveFlow.Complete();
                page.Context.LogInfo("信息统计页面已关闭，继续执行后续页面。", "activity");
                return await HandlePageAsync(page, cancellationToken).ConfigureAwait(false);
            }

            var closeNow = DateTimeOffset.UtcNow;
            if (!_targetMemberArchiveFlow.HasTimedOut(closeNow))
            {
                return true;
            }

            page.Context.LogInfo("关闭信息统计页面超时，继续尝试关闭。", "warning");
            _targetMemberArchiveFlow.BeginClosingStatistics(closeNow);
            await CloseTargetMemberArchiveAsync(page, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_targetMemberArchiveFlow.Phase != TargetMemberArchivePhase.CapturingMemberList)
        {
            _targetMemberArchiveFlow.Fail();
            return true;
        }

        var memberRegions = new[]
        {
            new FeatureSearchRegion(0.233, 0.151, 0.301, 0.269),
            new FeatureSearchRegion(0.502, 0.151, 0.570, 0.269),
            new FeatureSearchRegion(0.780, 0.151, 0.848, 0.269)
        };
        var cachedTarget = FindFeatureOne(
            page,
            "target_member",
            new RelativeRegion(0.233, 0.151, 0.848, 0.269),
            0.72);
        if (cachedTarget is not null)
        {
            var targetX = cachedTarget.CenterX(page.Frame.Width);
            var targetY = cachedTarget.CenterY(page.Frame.Height);
            var cachedIndex = Enumerable.Range(0, memberRegions.Length)
                .OrderBy(index =>
                {
                    var region = memberRegions[index];
                    var centerX = (region.Left + region.Right) / 2d;
                    var centerY = (region.Top + region.Bottom) / 2d;
                    return Math.Pow(centerX - targetX, 2) + Math.Pow(centerY - targetY, 2);
                })
                .First();
            page.State.SaveTargetMember = true;
            page.Context.LogInfo(
                $"缓存头像匹配到第{cachedIndex + 1}号主战员，相似度={cachedTarget.Confidence:0.0000}，跳过文字识别。",
                "activity");
            await CloseTargetMemberArchiveAsync(page, cancellationToken).ConfigureAwait(false);
            _targetMemberArchiveFlow.BeginClosingStatistics(DateTimeOffset.UtcNow);
            return true;
        }

        var namePoints = new[]
        {
            (X: 0.159, Y: 0.368),
            (X: 0.432, Y: 0.368),
            (X: 0.705, Y: 0.369)
        };
        var memberNames = namePoints
            .Select(point => FindBoxAtPoint(page, point.X, point.Y)?.Text.Trim() ?? string.Empty)
            .ToArray();
        if (memberNames.All(string.IsNullOrWhiteSpace))
        {
            var now = DateTimeOffset.UtcNow;
            if (!_targetMemberArchiveFlow.HasTimedOut(now))
            {
                return true;
            }

            page.Context.LogInfo(
                $"等待主战员列表超时（{_targetMemberArchiveFlow.Elapsed(now).TotalSeconds:0.0}秒），已取消本次头像捕获。",
                "warning");
            await CloseTargetMemberArchiveAsync(page, cancellationToken).ConfigureAwait(false);
            _targetMemberArchiveFlow.BeginClosingStatistics(DateTimeOffset.UtcNow);
            return true;
        }

        page.Context.LogInfo($"信息统计页面主战员：{string.Join(" / ", memberNames)}");
        var targetIndex = Array.FindIndex(
            memberNames,
            name => name.Length > 0
                    && (name.Contains(targetName, StringComparison.Ordinal)
                        || targetName.Contains(name, StringComparison.Ordinal)));
        var matcher = page.Context.FeatureMatcher;
        if (targetIndex < 0 || matcher is not IRuntimeFeatureMatcher runtimeMatcher)
        {
            page.Context.LogInfo(
                targetIndex < 0
                    ? $"三个主战员均未命中刷存档主战员「{targetName}」，已取消本次头像捕获。"
                    : "当前特征匹配器不支持运行时模板，无法保存目标主战员头像。",
                "warning");
            await CloseTargetMemberArchiveAsync(page, cancellationToken).ConfigureAwait(false);
            _targetMemberArchiveFlow.BeginClosingStatistics(DateTimeOffset.UtcNow);
            return true;
        }

        var savedTemplates = new Dictionary<string, RuntimeFeatureTemplate>(StringComparer.Ordinal);
        foreach (var runtimeFeature in TargetMemberAvatarCatalog.Features)
        {
            var savedTemplate = runtimeMatcher.RegisterRuntimeTemplate(
                runtimeFeature.Name,
                page.Frame,
                memberRegions[targetIndex],
                runtimeFeature.TargetRegion ?? memberRegions[targetIndex]);
            savedTemplates[runtimeFeature.Name] = savedTemplate;
        }

        try
        {
            page.Context.RuntimeFeatureTemplates?.SaveBundle(savedTemplates);
        }
        catch (Exception exception)
        {
            page.Context.LogInfo(
                $"主战员头像变体保存失败：{exception.Message}",
                "warning");
        }

        page.State.SaveTargetMember = true;
        page.Context.LogInfo(
            $"已保存刷存档主战员「{targetName}」头像特征，绑定第{targetIndex + 1}号主战员。",
            "activity");
        await CloseTargetMemberArchiveAsync(page, cancellationToken).ConfigureAwait(false);
        _targetMemberArchiveFlow.BeginClosingStatistics(DateTimeOffset.UtcNow);
        return true;
    }

    private static ValueTask CloseTargetMemberArchiveAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
        => ClickPointAsync(
            page,
            new RelativePoint(0.960, 0.054),
            "关闭信息统计页面",
            cancellationToken);
    // Python handle_equipment (utils.py:2711)：普通装备选择/安装流程，暂不接刻印专业模式。
    private static async ValueTask<bool> HandleEquipmentAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var title = FindBoxAtPoint(page, 0.499, 0.126);
        var hasEquipmentTitle = title is not null && MatchesAny(page, title, "装备");
        var isInstallPage = IsEquipmentInstallPage(page);
        if (!hasEquipmentTitle && !isInstallPage)
        {
            return false;
        }

        if (page.State.EquipmentChoiceConfirmAwaitingAllocation)
        {
            if (!isInstallPage)
            {
                page.Context.LogDebug("装备选择已确认，等待安装或主战员分配页面。");
                return true;
            }

            page.State.EquipmentChoiceConfirmAwaitingAllocation = false;
            page.State.ResetRecognitionMiss(EquipmentInstallTransitionActionKey);
        }

        if (page.State.EquipmentChoiceAwaitingInstall && isInstallPage)
        {
            page.State.EquipmentChoiceAwaitingInstall = false;
            page.State.ResetRecognitionMiss(EquipmentInstallTransitionActionKey);
        }
        else if (page.State.EquipmentChoiceAwaitingInstall
                 && IsEquipmentChoiceConfirmPage(page))
        {
            var confirmBox = FindFooterConfirmBox(page);
            if (confirmBox is null)
            {
                return false;
            }

            page.Context.LogInfo(
                "装备已选中，点击本页确认进入后续安装或主战员分配。",
                "activity");
            await ClickTextAsync(page, confirmBox, "确认装备选择", cancellationToken)
                .ConfigureAwait(false);
            page.State.EquipmentChoiceAwaitingInstall = false;
            page.State.EquipmentChoiceConfirmAwaitingAllocation = true;
            page.State.ResetRecognitionMiss(EquipmentInstallTransitionActionKey);
            return true;
        }
        else if (page.State.EquipmentChoiceAwaitingInstall)
        {
            var waitRounds = Math.Clamp(
                page.Context.GetTaskConfig(
                    ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey,
                    2),
                1,
                10);
            var missCount = page.State.IncrementRecognitionMiss(
                EquipmentInstallTransitionActionKey);
            if (missCount < waitRounds)
            {
                page.Context.LogDebug(
                    $"装备候选已点击，等待安装页 {missCount}/{waitRounds} 帧。");
                return false;
            }

            var ocrEvidence = string.Join(
                " | ",
                page.Recognized.Lines.Select(line =>
                    $"{line.Text}@{(line.Region.X + (line.Region.Width / 2d)) / page.Frame.Width:0.000},{(line.Region.Y + (line.Region.Height / 2d)) / page.Frame.Height:0.000}"));
            page.Context.LogInfo(
                $"装备流程无法推进：帧 #{page.FrameSequence}，候选已点击但未识别到后续页面或确认页。"
                + $" awaitingInstall={page.State.EquipmentChoiceAwaitingInstall}，"
                + $" awaitingConfirm={page.State.EquipmentInstallAwaitingConfirm}，"
                + $" bossReward={page.State.BossEquipmentRewardAwaitingAllocation}，"
                + $" isInstallPage={isInstallPage}，confirmPage={IsEquipmentChoiceConfirmPage(page)}，"
                + $" OCR={ocrEvidence}",
                "error");
            page.State.EquipmentChoiceAwaitingInstall = false;
            page.State.ResetRecognitionMiss(EquipmentInstallTransitionActionKey);
            page.Context.LogInfo(
                "等待装备后续页面超时，下一帧重新识别装备候选。",
                "warning");
            return false;
        }

        page.Context.LogInfo("检测到装备页面。", "activity");

        await CaptureDecisionSnapshotAsync(
                page,
                DecisionSnapshotCatalog.EquipmentChoiceReasonId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (isInstallPage)
        {
            return await HandleEquipmentInstallAsync(page, cancellationToken).ConfigureAwait(false);
        }

        return await HandleEquipmentChoiceAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private void ObserveEquipmentInstallLifecycle(ChaosModePage page)
    {
        if (!page.State.EquipmentInstallAwaitingConfirm)
        {
            return;
        }

        var route = _frameRouter.Resolve(page);
        if (route is null || string.Equals(route.Id, "equipment", StringComparison.Ordinal))
        {
            return;
        }

        page.Context.LogDebug(
            $"装备安装页已离开，结束本次确认等待。帧 #{page.FrameSequence}，路由={route.Id}。");
        CompleteEquipmentInstallAttempt(page, installed: true);
        page.State.BossEquipmentRewardAwaitingAllocation = false;
    }

    private static void BeginEquipmentInstallConfirmation(ChaosModePage page)
    {
        page.State.EquipmentInstallAwaitingConfirm = true;
        page.State.EquipmentInstallMemberClickedFrameSequence = page.FrameSequence;
        page.State.EquipmentInstallConfirmClickedFrameSequence = 0;
        page.State.EquipmentInstallConfirmAttempts = 0;
        page.State.ResetRecognitionMiss(EquipmentInstallConfirmWaitActionKey);
    }

    private static void BeginEquipmentInstallAttempt(
        ChaosModePage page,
        int slot,
        ChaosEquipmentCandidate candidate)
    {
        page.State.PendingEquipmentInstall = new EquipmentPendingInstall(
            slot,
            candidate.Name,
            candidate.Description,
            candidate.Quality,
            page.State.EquipmentNames[slot],
            page.State.EquipmentDescriptions[slot],
            page.State.EquipmentQualities[slot]);
        page.State.EquipmentNames[slot] = candidate.Name;
        page.State.EquipmentDescriptions[slot] = candidate.Description;
        page.State.EquipmentQualities[slot] = candidate.Quality;
        BeginEquipmentInstallConfirmation(page);
    }

    private static void CompleteEquipmentInstallAttempt(ChaosModePage page, bool installed)
    {
        if (!installed && page.State.PendingEquipmentInstall is { } pending)
        {
            page.State.EquipmentNames[pending.Slot] = pending.PreviousName;
            page.State.EquipmentDescriptions[pending.Slot] = pending.PreviousDescription;
            page.State.EquipmentQualities[pending.Slot] = pending.PreviousQuality;
        }

        page.State.PendingEquipmentInstall = null;
        page.State.EquipmentInstallAwaitingConfirm = false;
        page.State.EquipmentInstallMemberClickedFrameSequence = 0;
        page.State.EquipmentInstallConfirmClickedFrameSequence = 0;
        page.State.EquipmentInstallConfirmAttempts = 0;
        page.State.ResetRecognitionMiss(EquipmentInstallConfirmWaitActionKey);
    }

    private static async ValueTask<bool> HandleEquipmentInstallAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        page.Context.LogInfo("检测到安装装备界面。", "activity");
        if (page.State.EquipmentInstallAwaitingConfirm)
        {
            if (page.State.EquipmentInstallConfirmAttempts > 0)
            {
                if (page.FrameSequence <= page.State.EquipmentInstallConfirmClickedFrameSequence)
                {
                    page.Context.LogDebug(
                        $"忽略确认点击前的旧帧 #{page.FrameSequence}，上次确认帧 #{page.State.EquipmentInstallConfirmClickedFrameSequence}。");
                    return true;
                }

                page.Context.LogDebug(
                    $"安装装备确认已发送，同一安装页只点击一次，等待页面变化。帧 #{page.FrameSequence}。");
                return true;
            }

            var waitRounds = Math.Clamp(
                page.Context.GetTaskConfig(
                    ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey,
                    2),
                1,
                10);
            var confirmBox = FindFooterConfirmBox(page);
            if (confirmBox is null)
            {
                var missCount = page.State.IncrementRecognitionMiss(
                    EquipmentInstallConfirmWaitActionKey);
                if (missCount < waitRounds)
                {
                    page.Context.LogDebug(
                        $"主战员已选择，等待确认按钮 {missCount}/{waitRounds} 帧。");
                    return true;
                }

                page.Context.LogInfo(
                    $"安装装备确认等待超时，回退本次装备记录并重新选择主战员。等待帧数={missCount}/{waitRounds}。",
                    "warning");
                CompleteEquipmentInstallAttempt(page, installed: false);
                page.State.BossEquipmentRewardAwaitingAllocation = false;
                return true;
            }

            if (page.State.EquipmentInstallConfirmAttempts == 0
                && page.FrameSequence <= page.State.EquipmentInstallMemberClickedFrameSequence)
            {
                page.Context.LogDebug(
                    $"安装装备确认等待点击后的新帧。当前帧 #{page.FrameSequence}，选人帧 #{page.State.EquipmentInstallMemberClickedFrameSequence}。");
                return true;
            }


            var activeByColor = IsButtonActive(page.Frame, confirmBox);
            if (!activeByColor)
            {
                page.Context.LogDebug(
                    "安装装备确认按钮的颜色探测结果为灰色；当前已完成选人，按 OCR 确认文本继续执行确认。");
            }

            await ClickFooterConfirmAsync(
                    page,
                    confirmBox,
                    "安装装备确认",
                    cancellationToken)
                .ConfigureAwait(false);
            page.State.EquipmentInstallConfirmAttempts++;
            page.State.EquipmentInstallConfirmClickedFrameSequence = page.FrameSequence;
            page.State.ResetRecognitionMiss(EquipmentInstallConfirmWaitActionKey);
            page.State.BossEquipmentRewardAwaitingAllocation = false;
            return true;
        }

        var bottomButtons = FindOcrBoxes(page, new RelativeRegion(0.013, 0.881, 0.992, 0.994));
        var cancelBox = bottomButtons.FirstOrDefault(line => ContainsAny(page, line, "取消"));
        var purchaseBox = bottomButtons.FirstOrDefault(line => ContainsAny(page, line, "购买"));
        var priceBox = bottomButtons.FirstOrDefault(line => Regex.IsMatch(line.Text.Trim(), @"^\d+$"));
        var isPurchasePage = cancelBox is not null && purchaseBox is not null;
        int? equipmentPrice = priceBox is null ? null : ParseDiscountedPrice(priceBox.Text);
        var currentCredit = 0;
        if (isPurchasePage)
        {
            currentCredit = GetCurrentCredit(page);
            if (equipmentPrice is int price && price > currentCredit)
            {
                page.Context.LogInfo($"装备价格{price}大于当前信用点{currentCredit}，点击取消。", "activity");
                await ClickTextAsync(page, cancelBox!, "取消购买装备", cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        var refineCandidates = bottomButtons
            .Where(line => RelativeRegionContains(line, page, new RelativeRegion(0.563, 0.881, 0.998, 0.997)))
            .ToArray();
        var refineBoxes = refineCandidates
            .Where(line => ContainsAny(page, line, "提炼"))
            .ToArray();
        if (refineBoxes.Length > 0 && refineBoxes.Length == refineCandidates.Length)
        {
            page.Context.LogInfo("安装装备界面只有提炼按钮，直接点击提炼。", "activity");
            await ClickTextAsync(page, refineBoxes[0], "提炼装备", cancellationToken).ConfigureAwait(false);
            page.State.BossEquipmentRewardAwaitingAllocation = false;
            return true;
        }

        var candidate = ReadEquipmentCandidate(
            page,
            new RelativeRegion(0.217, 0.379, 0.469, 0.436),
            new RelativeRegion(0.188, 0.444, 0.323, 0.489),
            new RelativeRegion(0.179, 0.492, 0.542, 0.668),
            0.756,
            0.5);
        if (candidate is null)
        {
            page.Context.LogInfo("未能识别待安装装备的名称或类型，等待下一帧重新识别。", "warning");
            if (isPurchasePage)
            {
                await ClickTextAsync(page, cancelBox!, "取消购买装备", cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        page.Context.LogInfo($"待安装装备描述: 「{candidate.Description}」");
        var memberTags = FindMemberLevelTags(page, new RelativeRegion(0.609, 0.290, 0.652, 0.789));
        var targetMemberIndex = FindTargetMemberIndex(
            page,
            memberTags,
            new RelativeRegion(0.607, 0.192, 0.739, 0.856),
            "target_member_tiny");
        var tracksTargetMember = page.Context
            .GetTaskConfig("刷存档主战员", string.Empty)
            .Trim()
            .Length > 0;
        var preferredMemberIndex = targetMemberIndex
                                   ?? (!tracksTargetMember && memberTags.Count > 0 ? 0 : null);
        if (tracksTargetMember && targetMemberIndex is null && memberTags.Count > 0)
        {
            page.Context.LogInfo(
                "未识别到刷存档主战员头像，不会把第1号主战员当作刷存档目标。",
                "warning");
        }

        var slot = candidate.Slot;
        var currentName = page.State.EquipmentNames[slot];
        var currentQuality = page.State.EquipmentQualities[slot];
        var otherSlotQualities = page.State.EquipmentQualities
            .Where((_, index) => index != slot)
            .ToArray();
        var decision = ChaosEquipmentLogic.DecideInstall(
            currentName,
            currentQuality,
            candidate,
            otherSlotQualities);

        if (decision.ShouldInstall && preferredMemberIndex is int preferredIndex)
        {
            var chosen = memberTags[preferredIndex];
            if (!tracksTargetMember || targetMemberIndex is not null)
            {
                if (isPurchasePage)
                {
                    page.State.EquipmentNames[slot] = candidate.Name;
                    page.State.EquipmentDescriptions[slot] = candidate.Description;
                    page.State.EquipmentQualities[slot] = candidate.Quality;
                }
                else
                {
                    BeginEquipmentInstallAttempt(page, slot, candidate);
                }
            }
            var clickY = (chosen.Region.Y + (chosen.Region.Height / 2d)) / page.Frame.Height;
            var targetLabel = targetMemberIndex is int matchedIndex
                ? $"刷存档主战员（第{matchedIndex + 1}号）"
                : "第1号主战员";
            page.Context.LogInfo(
                $"{slot + 1}号位装备「{candidate.Name}」优于当前装备「{currentName}」，原因={decision.Reason}，安装给{targetLabel}。",
                "activity");
            await ClickPointAsync(
                    page,
                    new RelativePoint(0.756, clickY),
                    $"安装{slot + 1}号位装备",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!isPurchasePage)
            {
                BeginEquipmentInstallConfirmation(page);
            }

            if (isPurchasePage)
            {
                await ClickTextAsync(page, purchaseBox!, "购买装备", cancellationToken).ConfigureAwait(false);
                return true;
            }

            return true;
        }

        var otherMembers = memberTags
            .Where((_, index) => index != preferredMemberIndex)
            .ToArray();
        if (otherMembers.Length > 0)
        {
            var chosen = otherMembers[0];
            var clickY = (chosen.Region.Y + (chosen.Region.Height / 2d)) / page.Frame.Height;
            page.Context.LogInfo(
                $"{slot + 1}号位无需替换当前装备「{currentName}」，原因={decision.Reason}，安装给其他主战员。",
                "activity");
            await ClickPointAsync(
                    page,
                    new RelativePoint(0.756, clickY),
                    "安装装备给其他主战员",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!isPurchasePage)
            {
                BeginEquipmentInstallConfirmation(page);
            }

            if (isPurchasePage)
            {
                await ClickTextAsync(page, purchaseBox!, "购买装备", cancellationToken).ConfigureAwait(false);
                return true;
            }

            return true;
        }

        if (isPurchasePage)
        {
            await ClickTextAsync(page, cancelBox!, "购买装备无法分配，取消", cancellationToken).ConfigureAwait(false);
            return true;
        }

        var refineBox = FindRefineBox(page);
        if (refineBox is not null)
        {
            await ClickTextAsync(page, refineBox, "无需替换装备，点击提炼", cancellationToken).ConfigureAwait(false);
            page.State.BossEquipmentRewardAwaitingAllocation = false;
            return true;
        }

        page.Context.LogInfo("未找到可选择的主战员或提炼按钮。", "warning");
        return false;
    }

    private static OcrTextLine? FindFooterConfirmBox(ChaosModePage page)
        => FindText(
            page,
            new RelativeRegion(0.267, 0.867, 0.991, 0.979),
            GameText(page, "确认"));

    private static async ValueTask<bool> HandleEquipmentChoiceAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        var specs = new[]
        {
            (Name: new RelativeRegion(0.409, 0.219, 0.678, 0.276), Type: new RelativeRegion(0.384, 0.275, 0.562, 0.319), Description: new RelativeRegion(0.382, 0.324, 0.723, 0.496), ClickX: 0.518, ClickY: 0.454),
            (Name: new RelativeRegion(0.410, 0.551, 0.699, 0.608), Type: new RelativeRegion(0.384, 0.613, 0.573, 0.653), Description: new RelativeRegion(0.380, 0.658, 0.720, 0.836), ClickX: 0.521, ClickY: 0.600)
        };
        var candidates = new List<ChaosEquipmentCandidate>();
        foreach (var spec in specs)
        {
            var candidate = ReadEquipmentCandidate(
                page, spec.Name, spec.Type, spec.Description, spec.ClickX, spec.ClickY);
            if (candidate is null)
            {
                page.Context.LogInfo("选择装备界面的候选装备信息不完整，等待下轮重新识别。", "warning");
                return true;
            }

            candidates.Add(candidate);
        }

        ChaosEquipmentCandidate? chosen = null;
        for (var slot = 0; slot < 3 && chosen is null; slot++)
        {
            var currentName = page.State.EquipmentNames[slot];
            var priority = candidates.FirstOrDefault(candidate => candidate.Slot == slot)?.Priority ?? [];
            var currentRank = ChaosEquipmentLogic.EquipmentRank(currentName, priority);
            chosen = candidates.FirstOrDefault(candidate => candidate.Slot == slot
                && candidate.Rank is not null
                && (currentName.Length == 0 || candidate.Rank < currentRank));
        }

        chosen ??= candidates.FirstOrDefault(candidate => page.State.EquipmentNames[candidate.Slot].Length == 0);
        if (chosen is null)
        {
            page.Context.LogInfo("候选装备均未命中升级条件，选择第一个候选。", "activity");
            chosen = candidates[0];
        }

        await ClickPointAsync(
                page,
                new RelativePoint(chosen.ClickX, chosen.ClickY),
                $"选择{chosen.Slot + 1}号位装备「{chosen.Name}」",
                cancellationToken)
            .ConfigureAwait(false);
        page.State.EquipmentChoiceAwaitingInstall = true;
        page.State.ResetRecognitionMiss(EquipmentInstallTransitionActionKey);
        return true;
    }

    private static ChaosEquipmentCandidate? ReadEquipmentCandidate(
        ChaosModePage page,
        RelativeRegion nameRegion,
        RelativeRegion typeRegion,
        RelativeRegion descriptionRegion,
        double clickX,
        double clickY)
    {
        var ocrName = RegionText(page, nameRegion).Trim();
        var typeText = RegionText(page, typeRegion).Trim();
        var description = RegionText(page, descriptionRegion).Trim();
        if (ocrName.Length == 0)
        {
            return null;
        }

        var slot = ChaosEquipmentCatalog.Default.FindSlot(ocrName)
                   ?? ChaosEquipmentLogic.SlotFromType(typeText);
        if (slot is null)
        {
            return null;
        }

        var priority = page.Context.Config.GetStringList($"装备{slot.Value + 1}号位优先级");
        var (canonicalName, rank) = ChaosEquipmentLogic.MatchEquipmentName(ocrName, priority);
        var quality = ChaosEquipmentLogic.QualityAt(page.Frame, 0.117, 0.409);
        return new ChaosEquipmentCandidate(
            ocrName,
            canonicalName ?? ocrName,
            typeText,
            description,
            slot.Value,
            priority,
            rank,
            quality,
            clickX,
            clickY);
    }

    private static IReadOnlyList<OcrTextLine> FindOcrBoxes(ChaosModePage page, RelativeRegion region)
        => page.Classification.FindOcrBoxes(region);

    private static bool RelativeRegionContains(
        OcrTextLine line,
        ChaosModePage page,
        RelativeRegion region)
        => region.Contains(line, page.Frame.Width, page.Frame.Height);

    private static IReadOnlyList<OcrTextLine> FindMemberLevelTags(
        ChaosModePage page,
        RelativeRegion region)
    {
        var tags = FindFeatures(page, "leveltag", region, 0.7);
        var deduplicated = new List<FeatureBox>();
        foreach (var tag in tags.OrderByDescending(item => item.Confidence))
        {
            var centerX = tag.X + (tag.Width / 2d);
            var centerY = tag.Y + (tag.Height / 2d);
            if (deduplicated.Any(kept =>
                    Math.Sqrt(
                        Math.Pow(centerX - (kept.X + (kept.Width / 2d)), 2)
                        + Math.Pow(centerY - (kept.Y + (kept.Height / 2d)), 2))
                    < Math.Max(Math.Max(tag.Width, tag.Height), Math.Max(kept.Width, kept.Height))))
            {
                continue;
            }

            deduplicated.Add(tag);
        }

        return deduplicated
            .OrderBy(tag => tag.Y)
            .Take(3)
            .Select(tag => new OcrTextLine(
                string.Empty,
                tag.Confidence,
                new OcrTextRegion(tag.X, tag.Y, tag.Width, tag.Height)))
            .ToArray();
    }

    private static int? FindTargetMemberIndex(
        ChaosModePage page,
        IReadOnlyList<OcrTextLine> memberTags,
        RelativeRegion region,
        string featureName)
    {
        if (memberTags.Count == 0)
        {
            return null;
        }

        var target = FindFeatureOne(page, featureName, region, 0.4);
        if (target is null)
        {
            return null;
        }

        var targetMemberIndex = Enumerable.Range(0, memberTags.Count)
            .OrderBy(index =>
            {
                var centerX = memberTags[index].Region.X + (memberTags[index].Region.Width / 2d);
                var centerY = memberTags[index].Region.Y + (memberTags[index].Region.Height / 2d);
                return Math.Pow(centerX - (target.X + (target.Width / 2d)), 2)
                       + Math.Pow(centerY - (target.Y + (target.Height / 2d)), 2);
            })
            .First();
        page.Context.LogInfo(
            $"刷存档主战员头像匹配成功，相似度={target.Confidence:0.0000}，绑定第{targetMemberIndex + 1}号主战员。",
            "activity");
        return targetMemberIndex;
    }

    private static OcrTextLine? FindRefineBox(ChaosModePage page)
        => FindOcrBoxes(page, new RelativeRegion(0.522, 0.879, 0.999, 0.996))
            .FirstOrDefault(line => ContainsAny(page, line, "提炼"));

    private static int GetCurrentCredit(ChaosModePage page)
    {
        var credit = 0;
        foreach (var point in new[] { (X: 0.794, Y: 0.054), (X: 0.734, Y: 0.053) })
        {
            var box = FindBoxAtPoint(page, point.X, point.Y);
            if (box is not null && int.TryParse(box.Text.Trim(), out var value) && value > credit)
            {
                credit = value;
            }
        }

        return credit;
    }

    private static int? ParseDiscountedPrice(string priceText)
    {
        priceText = priceText.Trim();
        if (!Regex.IsMatch(priceText, @"^\d+$"))
        {
            return null;
        }

        if (priceText.Length == 6)
        {
            return int.TryParse(priceText[^3..], out var sixDigitPrice) ? sixDigitPrice : null;
        }

        if (priceText.Length is 4 or 5)
        {
            return int.TryParse(priceText[^2..], out var shortPrice) ? shortPrice : null;
        }

        return int.TryParse(priceText, out var price) ? price : null;
    }

    // Python handle_view_original (utils.py:5231) 的函数名沿用了“查看原件”，
    // 页面可能只显示顶部“查看原件/查看之前的闪光”，也可能同时显示“请决定闪光效果”。
    private static async ValueTask<bool> HandleFlashEffectAsync(
        ChaosModePage page,
        CancellationToken cancellationToken)
    {
        if (!IsFlashEffectPage(page))
        {
            return false;
        }

        var cards = page.Classification.Cards;
        if (cards.Count == 0)
        {
            var missCount = page.State.IncrementRecognitionMiss(FlashEffectCardsActionKey);
            if (missCount == 1)
            {
                page.Context.LogInfo(
                    "检测到闪光效果页面，但未识别到卡牌；保存诊断现场并继续尝试后续路由。",
                    "warning");
                var contextLogs = BuildRecognitionFailureContext(
                    page,
                    FlashEffectCardsActionKey,
                    "未识别到卡牌候选",
                    intendedFixedPoint: null,
                    fallbackRounds: null);
                await CaptureDecisionSnapshotAsync(
                        page,
                        DecisionSnapshotCatalog.FlashEffectNoCardsReasonId,
                        contextLogs: contextLogs,
                        candidateActionSource: "识别失败诊断",
                        force: true,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            return false;
        }

        page.State.ResetRecognitionMiss(FlashEffectCardsActionKey);
        var flashPriority = page.Context.Config.GetStringList("闪光优先级");
        var chosenCard = ChaosCardRecognition.ChooseByFlashPriority(cards, flashPriority);
        if (chosenCard is not null)
        {
            page.Context.LogInfo($"闪光优先选择「{chosenCard.Name}」。", "activity");
            var firstPriority = flashPriority.FirstOrDefault(keyword => !string.IsNullOrWhiteSpace(keyword));
            if (page.Context.GetTaskConfig("首层刷特定闪光", false)
                && firstPriority is not null
                && ChaosCardRecognition.MatchesFlashPriority(chosenCard, firstPriority))
            {
                page.State.GetSpecificFlash = true;
                page.Context.LogInfo("已命中闪光优先级第一项，记录已获得特定闪光", "activity");
            }
        }

        var targetBox = FindFeatureOne(
            page,
            "target",
            new RelativeRegion(0.090, 0.179, 0.927, 0.342));
        UpdateMeditationStateFromFlash(
            page,
            cards,
            hasChosenCard: chosenCard is not null,
            hasTargetCard: targetBox is not null);
        if (targetBox is not null)
        {
            var targetX = Math.Clamp(targetBox.CenterX(page.Frame.Width) - 0.0975, 0, 1);
            var targetY = Math.Clamp(targetBox.CenterY(page.Frame.Height) + 0.2460, 0, 1);
            page.Context.LogInfo("卡牌闪光事件检测到 target 卡牌。", "activity");
            await ClickPointAsync(
                    page,
                    new RelativePoint(targetX, targetY),
                    "选择 target 卡牌",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (chosenCard is null)
        {
            chosenCard = cards[Random.Shared.Next(cards.Count)];
            page.Context.LogInfo($"未命中闪光优先级，随机选择「{chosenCard.Name}」。", "activity");
        }

        await ClickPointAsync(
                page,
                new RelativePoint(chosenCard.X, chosenCard.Y),
                $"选择闪光卡牌「{chosenCard.Name}」",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
    private sealed class RouteNode(
        string featureName,
        string nodeType,
        FeatureBox box,
        List<string> specialFeatures)
    {
        public string FeatureName { get; } = featureName;
        public string NodeType { get; } = nodeType;
        public FeatureBox Box { get; } = box;
        public List<string> SpecialFeatures { get; } = specialFeatures;
    }
}
