using System.Diagnostics;
using Automation.Core.Arbitration;
using Automation.Core.DeveloperTools;
using Automation.Core.Dispatching;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Vision;
using Kes.Tasks.Configuration;

namespace Kes.Tasks;

public sealed partial class ChaosModeTask
{
    private const string TargetMemberArchiveConsumerId = "target-member-archive";
    private const string NoConsumerSampleId = "no-consumer";
    private const string ClosePageConsumerId = "close-page";
    private const string ConfirmConsumerId = "confirm";
    private const string SharedConfirmationConsumerId = "shared-confirmation";
    private const string ResumeTargetMemberArchiveConsumerId = "resume-target-member-archive";
    private const string SaveTargetMemberConsumerId = "save-target-member";

    private static readonly TimeSpan FrameBusWaitTimeout = TimeSpan.FromMilliseconds(250);
    private readonly FrameDispatchErrorLogGate _frameDispatchErrorLogGate = new();

    private async ValueTask<bool> RunFrameBusOnceAsync(
        TaskContext context,
        IOcrProvider ocr,
        CancellationToken cancellationToken)
    {
        var runStarted = Stopwatch.GetTimestamp();
        FrameLease? frameLease = null;
        var ownershipTransferred = false;

        try
        {
            try
            {
                frameLease = Volatile.Read(ref _lastSequence) == 0
                    ? await context.GetLatestFrameAsync(cancellationToken).ConfigureAwait(false)
                    : await context
                        .GetNextFrameAsync(Volatile.Read(ref _lastSequence), FrameBusWaitTimeout, cancellationToken)
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
                        "抓帧暂时没有新画面，帧总线继续轮询。",
                        null,
                        null,
                        DateTimeOffset.Now));
                    context.LogInfo("等待新画面超时，帧总线将继续轮询。", "warning");
                }

                return true;
            }

            _waitingReported = false;
            var frameWaitMs = Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds;
            if (frameLease is not IBgraFrameLease bgraFrameLease)
            {
                throw new InvalidOperationException(
                    $"自动卡厄思模式仅支持 BGRA 帧，当前帧类型为 {frameLease.GetType().Name}。 ");
            }

            var ocrStarted = Stopwatch.GetTimestamp();
            var usedRegionProbe = false;
            var probeMissRounds = GetConfirmationProbeMissRounds(context);
            OcrFrameResult recognized;
            if (Volatile.Read(ref _confirmationProbeArmed) != 0 && !_targetMemberArchiveFlow.IsActive)
            {
                var scope = OcrScanScope.FromRelative(
                    CenterConfirmationProbeId,
                    bgraFrameLease.Frame.Width,
                    bgraFrameLease.Frame.Height,
                    CenterConfirmationProbeRegion.Left,
                    CenterConfirmationProbeRegion.Top,
                    CenterConfirmationProbeRegion.Right,
                    CenterConfirmationProbeRegion.Bottom);
                var probeStarted = Stopwatch.GetTimestamp();
                var probe = _regionOcrAnalyzer.Analyze(bgraFrameLease.Frame, scope, ocr);
                var probeMs = Stopwatch.GetElapsedTime(probeStarted).TotalMilliseconds;
                var probePage = CreatePage(context, frameLease, bgraFrameLease, probe.Recognition);
                var probeConfirmation = FindCenterConfirmation(probePage);
                if (probeConfirmation is not null)
                {
                    usedRegionProbe = true;
                    recognized = probe.Recognition;
                    Interlocked.Exchange(ref _confirmationProbeMisses, 0);
                    _lastProbeHitCenterConfirmation = true;
                    context.LogDebug(
                        $"[roi-probe] 帧 #{frameLease.Sequence} scope={scope.Id} box={scope.X},{scope.Y},{scope.Width},{scope.Height} hit=true ocr={probeMs:0}ms");
                }
                else
                {
                    Interlocked.Increment(ref _confirmationProbeMisses);
                    context.LogDebug(
                        $"[roi-probe] 帧 #{frameLease.Sequence} scope={scope.Id} box={scope.X},{scope.Y},{scope.Width},{scope.Height} hit=false miss={_confirmationProbeMisses}/{probeMissRounds} ocr={probeMs:0}ms");
                    if (!_lastProbeHitCenterConfirmation
                        && _confirmationProbeMisses < probeMissRounds)
                    {
                        AdvanceLastSequence(frameLease.Sequence);
                        var probeTotalMs = Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds;
                        ReportSlowFrame(
                            context,
                            frameWaitMs,
                            probeMs,
                            0,
                            0,
                            0,
                            0,
                            0,
                            probeTotalMs);
                        return true;
                    }

                    var fallbackReason = _lastProbeHitCenterConfirmation
                        ? "确认页面已变化"
                        : "连续未命中达到阈值";
                    context.LogDebug(
                        $"[roi-probe] 帧 #{frameLease.Sequence} 回退全图 OCR，原因：{fallbackReason}。");
                    _lastProbeHitCenterConfirmation = false;
                    Interlocked.Exchange(ref _confirmationProbeMisses, 0);
                    Volatile.Write(ref _confirmationProbeArmed, 0);
                    var analysis = _frameOcrAnalyzer.Analyze(
                        context.SessionId,
                        frameLease,
                        ocr,
                        _eventBus);
                    recognized = analysis.Recognition;
                }
            }
            else
            {
                _lastProbeHitCenterConfirmation = false;
                Interlocked.Exchange(ref _confirmationProbeMisses, 0);
                var analysis = _frameOcrAnalyzer.Analyze(
                    context.SessionId,
                    frameLease,
                    ocr,
                    _eventBus);
                recognized = analysis.Recognition;
            }

            var ocrMs = Stopwatch.GetElapsedTime(ocrStarted).TotalMilliseconds;
            AdvanceLastSequence(frameLease.Sequence);

            var page = CreatePage(context, frameLease, bgraFrameLease, recognized);
            var centerConfirmation = FindCenterConfirmation(page);
            page.Observation = PageObservation.FromOcr(
                context.SessionId,
                page.FrameSequence,
                page.Frame.Width,
                page.Frame.Height,
                page.Recognized,
                centerConfirmation?.Point,
                frameLease.CapturedAt);
            ObserveEquipmentInstallLifecycle(page);
            var routeStarted = Stopwatch.GetTimestamp();
            var routedRoutes = _frameRouter.ResolveCandidates(page);
            page.Observation = page.Observation with
            {
                RouteIds = routedRoutes.Select(route => route.Id).ToArray()
            };
            var routedRoute = routedRoutes.FirstOrDefault();
            var routedConsumerId = routedRoute?.Id;
            var consumerId = ResolveFrameConsumerId(page);
            UpdateConfirmationProbeState(
                page,
                usedRegionProbe,
                centerConfirmation is not null,
                routedConsumerId,
                routedRoute?.ExpectsCenterConfirmation == true);
            var routeMs = Stopwatch.GetElapsedTime(routeStarted).TotalMilliseconds;

            var bus = GetOrCreateFrameDispatchBus(context, cancellationToken);
            var publishStarted = Stopwatch.GetTimestamp();
            var publishResult = bus.TryPublish(new FrameDelivery<ChaosModePage>(
                context.SessionId,
                page.FrameSequence,
                consumerId ?? string.Empty,
                page.Observation.Fingerprint.Value,
                page));
            var publishMs = Stopwatch.GetElapsedTime(publishStarted).TotalMilliseconds;
            ownershipTransferred = publishResult.Published;

            var sampleCreateMs = 0d;
            var archiveMs = 0d;
            if ((publishResult.Kind is FramePublishKind.Published
                 or FramePublishKind.NoConsumer)
                && frameLease.Sequence > Interlocked.Read(ref _lastSampleFrameSequence))
            {
                var sampleCreateStarted = Stopwatch.GetTimestamp();
                var sampleRequest = CreateFrameSampleRequest(
                    context,
                    page,
                    consumerId ?? NoConsumerSampleId,
                    frameLease.CapturedAt);
                sampleCreateMs = Stopwatch.GetElapsedTime(sampleCreateStarted).TotalMilliseconds;
                if (sampleRequest is not null)
                {
                    var archiveStarted = Stopwatch.GetTimestamp();
                    var sampleAccepted = await ArchiveFrameSampleAsync(
                            context,
                            sampleRequest,
                            cancellationToken)
                        .ConfigureAwait(false);
                    archiveMs = Stopwatch.GetElapsedTime(archiveStarted).TotalMilliseconds;
                    if (sampleAccepted)
                    {
                        Interlocked.Exchange(ref _lastSampleFrameSequence, frameLease.Sequence);
                    }
                }
            }
            var totalMs = Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds;
            ReportSlowFrame(
                context,
                frameWaitMs,
                ocrMs,
                0,
                routeMs,
                sampleCreateMs,
                archiveMs,
                publishMs,
                totalMs);
            return publishResult.Kind is FramePublishKind.Published
                or FramePublishKind.NoConsumer
                or FramePublishKind.QueueOverflow;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                frameLease?.Dispose();
            }
        }
    }

    private ChaosModePage CreatePage(
        TaskContext context,
        FrameLease lease,
        IBgraFrameLease frame,
        OcrFrameResult recognized)
        => new(
            context,
            frame.Frame,
            lease.Sequence,
            recognized,
            _state,
            _actionArbiter,
            _commandBus,
            lease,
            ArmCenterConfirmationProbe);

    private static int GetConfirmationProbeMissRounds(TaskContext context)
    {
        var configured = context.GetTaskConfig(
            ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey,
            2);
        return Math.Clamp(configured, 1, 10);
    }

    private void UpdateConfirmationProbeState(
        ChaosModePage page,
        bool usedRegionProbe,
        bool hasCenterConfirmation,
        string? routedConsumerId,
        bool expectsCenterConfirmation)
    {
        page.Context.LogDebug(
            $"[roi-probe] 帧 #{page.FrameSequence} 全图路由={routedConsumerId ?? "none"}，当前探测={Volatile.Read(ref _confirmationProbeArmed) != 0}");

        if (usedRegionProbe && hasCenterConfirmation)
        {
            Volatile.Write(ref _confirmationProbeArmed, 1);
            _lastProbeHitCenterConfirmation = true;
            Interlocked.Exchange(ref _confirmationProbeMisses, 0);
            return;
        }

        Volatile.Write(ref _confirmationProbeArmed, expectsCenterConfirmation ? 1 : 0);
        _lastProbeHitCenterConfirmation = false;
        Interlocked.Exchange(ref _confirmationProbeMisses, 0);
    }
    private FrameDispatchBus<ChaosModePage> GetOrCreateFrameDispatchBus(
        TaskContext context,
        CancellationToken cancellationToken)
    {
        lock (_frameDispatchBusGate)
        {
            if (_frameDispatchBus is null
                || (_frameDispatchBus.IsCompleted && !_frameDispatchBus.IsFaulted))
            {
                var consumers = new List<FrameConsumer<ChaosModePage>>
                {
                    new(TargetMemberArchiveConsumerId, ProcessFrameBusDeliveryAsync),
                    new(ResumeTargetMemberArchiveConsumerId, ProcessFrameBusDeliveryAsync),
                    new(SaveTargetMemberConsumerId, ProcessFrameBusDeliveryAsync)
                };
                consumers.AddRange(_frameRouter.Routes.Select(route =>
                    new FrameConsumer<ChaosModePage>(route.Id, ProcessFrameBusDeliveryAsync)));

                _frameDispatchBus = new FrameDispatchBus<ChaosModePage>(
                    consumers,
                    capacity: 8,
                    consumerTimeout: TimeSpan.FromSeconds(30),
                    errorSink: error => LogFrameDispatchError(context, error),
                    cancellationToken: cancellationToken);
            }

            return _frameDispatchBus;
        }
    }

    private string? ResolveFrameConsumerId(ChaosModePage page)
    {
        var priorityOverlay = _frameRouter.ResolvePriorityOverlay(page);
        if (priorityOverlay is not null)
        {
            return priorityOverlay.Id;
        }

        if (_targetMemberArchiveFlow.IsActive)
        {
            return TargetMemberArchiveConsumerId;
        }

        var routed = _frameRouter.Resolve(page);

        var targetName = page.Context.GetTaskConfig("刷存档主战员", string.Empty).Trim();
        if (targetName.Length > 0 && !page.State.SaveTargetMember)
        {
            if (LooksLikeTargetMemberListPage(page))
            {
                return ResumeTargetMemberArchiveConsumerId;
            }

            if (GetCurrentHpPercent(page) is not null)
            {
                return SaveTargetMemberConsumerId;
            }
        }

        return routed?.Id;
    }

    private static FrameSampleRequest? CreateFrameSampleRequest(
        TaskContext context,
        ChaosModePage page,
        string consumerId,
        DateTimeOffset capturedAt)
    {
        var sink = context.FrameSamples;
        if (sink is null || !sink.IsEnabled)
        {
            return null;
        }

        var lines = page.Recognized.Lines
            .Select(line => new OcrTextLine(
                line.Text,
                line.Score,
                new OcrTextRegion(
                    line.Region.X,
                    line.Region.Y,
                    line.Region.Width,
                    line.Region.Height)))
            .ToArray();
        return new FrameSampleRequest(
            context.SessionId,
            context.RunId,
            consumerId,
            page.FrameSequence,
            capturedAt,
            new BgraFrame(page.Frame.Width, page.Frame.Height, page.Frame.Pixels),
            new OcrFrameResult(page.Recognized.Text, page.Recognized.Score, lines),
            page.Observation.Fingerprint.Value);
    }

    private static async ValueTask<bool> ArchiveFrameSampleAsync(
        TaskContext context,
        FrameSampleRequest request,
        CancellationToken cancellationToken)
    {
        var sink = context.FrameSamples;
        if (sink is null)
        {
            return false;
        }

        try
        {
            if (sink is IBackgroundFrameSampleArchiveSink backgroundSink)
            {
                var accepted = backgroundSink.TryEnqueue(request);
                if (!accepted)
                {
                    context.LogInfo(
                        $"派发样本未入队：{request.ConsumerId} · 帧 #{request.FrameSequence}，将在下一帧继续尝试。",
                        "warning");
                }

                return accepted;
            }

            var result = await sink.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return true;
            }

            switch (result.Outcome)
            {
                case FrameSampleArchiveOutcome.Saved:
                    context.LogDebug($"派发样本归档：{request.ConsumerId} · {result.SavedCount}/{result.Limit} · {Path.GetFileName(result.ImagePath!)}");
                    break;
                case FrameSampleArchiveOutcome.Duplicate:
                    context.LogDebug($"派发样本归档跳过：{request.ConsumerId} · {result.SavedCount}/{result.Limit} · 重复帧");
                    break;
                case FrameSampleArchiveOutcome.LimitReached:
                    context.LogDebug($"派发样本归档跳过：{request.ConsumerId} · {result.SavedCount}/{result.Limit} · 已达上限");
                    break;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            context.LogInfo(
                $"开发派发样本保存失败：{request.ConsumerId} · {exception.Message}",
                "warning");
            return false;
        }
    }
    private async ValueTask ProcessFrameBusDeliveryAsync(
        FrameDelivery<ChaosModePage> delivery,
        CancellationToken cancellationToken)
    {
        var page = delivery.Payload;
        try
        {
            await _frameActionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (page.FrameSequence <= Volatile.Read(ref _lastSerializedFrameSequence))
                {
                    page.Context.LogDebug(
                        $"忽略过期帧 #{page.FrameSequence}，当前已处理到帧 #{Volatile.Read(ref _lastSerializedFrameSequence)}。");
                    return;
                }

                try
                {
                    var actualConsumerId = ResolveFrameConsumerId(page);
                    if (!string.Equals(actualConsumerId, delivery.ConsumerId, StringComparison.Ordinal))
                    {
                        var reason = actualConsumerId is null
                            ? $"消费者 {delivery.ConsumerId} 收到本帧，但当前帧没有匹配消费者。"
                            : $"消费者 {delivery.ConsumerId} 收到本帧，但当前路由消费者是 {actualConsumerId}。";
                        ReportConsumerRejected(delivery, reason);
                        return;
                    }
                    if (!string.Equals(actualConsumerId, "select-card", StringComparison.Ordinal))
                    {
                        page.State.ResetCardSelectionSearch();
                    }


                    var routedDefinition = _frameRouter.FindById(actualConsumerId!);
                    var actionKey = routedDefinition?.UseActionBoundary == false ? null : ResolveActionBoundaryKey(actualConsumerId!, page);
                    var observationDecision = _actionArbiter.ObservePage(
                        page.Observation,
                        actionKey);
                    if (observationDecision.Kind == ActionDecisionKind.SuppressDuplicate)
                    {
                        page.Context.LogDebug(
                            $"总线继续投递帧 #{page.FrameSequence}；页面观察未释放动作权，由消费者决定等待或重试。");
                    }

                    var handled = delivery.ConsumerId switch
                    {
                        TargetMemberArchiveConsumerId => await HandleTargetMemberArchiveAsync(
                            page,
                            cancellationToken).ConfigureAwait(false),

                        ResumeTargetMemberArchiveConsumerId => await HandleResumeTargetMemberArchiveAsync(
                            page,
                            cancellationToken).ConfigureAwait(false),
                        SaveTargetMemberConsumerId => await HandleSaveTargetMemberAsync(
                            page,
                            cancellationToken).ConfigureAwait(false),
                        _ => await HandlePageAsync(
                            page,
                            cancellationToken,
                            delivery.ConsumerId).ConfigureAwait(false)
                    };

                    if (handled)
                    {
                        _frameDispatchErrorLogGate.Reset();
                        AdvanceLastSequence(page.FrameSequence);
                        _lastUnhandledPageSignature = string.Empty;
                        _lastUnhandledPageLogAt = DateTimeOffset.MinValue;
                        return;
                    }

                    ReportConsumerRejected(
                        delivery,
                        $"消费者 {delivery.ConsumerId} 已接收帧 #{page.FrameSequence}，但未处理当前页面。");
                }
                finally
                {
                    AdvanceSequence(ref _lastSerializedFrameSequence, page.FrameSequence);
                }
            }
            finally
            {
                _frameActionGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _frameDispatchBus?.ReportConsumerFailed(delivery, exception);
        }
        finally
        {
            page.DisposeFrame();
        }
    }

    private void ReportConsumerRejected(
        FrameDelivery<ChaosModePage> delivery,
        string reason)
    {
        _frameDispatchBus?.ReportConsumerRejected(delivery, reason);
    }

    internal static string GetFrameDispatchLogTag(FrameDispatchErrorKind kind)
        => kind switch
        {
            FrameDispatchErrorKind.NoConsumer => "error",
            FrameDispatchErrorKind.QueueOverflow => "warning",
            _ => "error"
        };

    private void LogFrameDispatchError(
        TaskContext context,
        FrameDispatchError error)
    {
        var threshold = Math.Clamp(
            context.GetTaskConfig(
                ChaosModeConfigSchema.FrameDispatchErrorAlertThresholdKey,
                3),
            3,
            5);
        var decision = _frameDispatchErrorLogGate.Record(error, threshold);
        if (decision.Kind == FrameDispatchErrorLogDecisionKind.Suppress)
        {
            return;
        }

        if (decision.Kind == FrameDispatchErrorLogDecisionKind.Observe)
        {
            context.LogDebug(
                $"Frame dispatch error streak {decision.ConsecutiveCount}/{threshold}: [{error.Kind}] frame #{error.FrameSequence} consumer={error.ConsumerId}.");
            return;
        }

        var tag = GetFrameDispatchLogTag(error.Kind);
        var prefix = string.Equals(tag, "error", StringComparison.Ordinal)
            ? "帧总线下发错误"
            : "帧总线诊断";
        context.LogInfo(
            $"{prefix} [{error.Kind}] 帧 #{error.FrameSequence} 消费者={error.ConsumerId}：{error.Message}",
            tag);
    }
}
