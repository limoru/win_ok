using System.Diagnostics;
using Automation.Core.Arbitration;
using Automation.Core.Dispatching;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Vision;

namespace Kes.Tasks;

public sealed partial class ChaosModeTask
{
    private const string TargetMemberArchiveConsumerId = "target-member-archive";
    private const string ClosePageConsumerId = "close-page";
    private const string ResumeTargetMemberArchiveConsumerId = "resume-target-member-archive";
    private const string SaveTargetMemberConsumerId = "save-target-member";

    private static readonly TimeSpan FrameBusWaitTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FrameDuplicateRetention = TimeSpan.FromSeconds(5);

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
                frameLease = _lastSequence == 0
                    ? await context.GetLatestFrameAsync(cancellationToken).ConfigureAwait(false)
                    : await context
                        .GetNextFrameAsync(_lastSequence, FrameBusWaitTimeout, cancellationToken)
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
            var analysis = _frameOcrAnalyzer.Analyze(
                context.SessionId,
                frameLease,
                ocr,
                _eventBus);
            var ocrMs = Stopwatch.GetElapsedTime(ocrStarted).TotalMilliseconds;
            _lastSequence = frameLease.Sequence;

            var page = new ChaosModePage(
                context,
                bgraFrameLease.Frame,
                frameLease.Sequence,
                analysis.Recognition,
                _state,
                _actionArbiter,
                _commandBus,
                frameLease);
            var centerConfirmation = FindCenterConfirmation(page);
            page.Observation = PageObservation.FromOcr(
                context.SessionId,
                page.FrameSequence,
                page.Frame.Width,
                page.Frame.Height,
                page.Recognized,
                centerConfirmation?.Point,
                frameLease.CapturedAt);

            var consumerId = ResolveFrameConsumerId(page);
            if (string.IsNullOrWhiteSpace(consumerId))
            {
                ReportUnhandledPage(context, page.Recognized);
                return false;
            }

            var bus = GetOrCreateFrameDispatchBus(cancellationToken);
            var publishResult = bus.TryPublish(new FrameDelivery<ChaosModePage>(
                context.SessionId,
                page.FrameSequence,
                consumerId,
                page.Observation.Fingerprint.Value,
                page));
            ownershipTransferred = publishResult.Published;

            var totalMs = Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds;
            ReportSlowFrame(context, frameWaitMs, ocrMs, 0, totalMs);
            return publishResult.Kind is FramePublishKind.Published
                or FramePublishKind.SuppressedDuplicate;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                frameLease?.Dispose();
            }
        }
    }

    private FrameDispatchBus<ChaosModePage> GetOrCreateFrameDispatchBus(
        CancellationToken cancellationToken)
    {
        lock (_frameDispatchBusGate)
        {
            if (_frameDispatchBus is null || _frameDispatchBus.IsCompleted)
            {
                _frameDispatchBus = new FrameDispatchBus<ChaosModePage>(
                    ProcessFrameBusDeliveryAsync,
                    capacity: 4,
                    duplicateRetention: FrameDuplicateRetention,
                    cancellationToken: cancellationToken);
            }

            return _frameDispatchBus;
        }
    }

    private string? ResolveFrameConsumerId(ChaosModePage page)
    {
        if (_targetMemberArchiveFlow.IsActive)
        {
            return TargetMemberArchiveConsumerId;
        }

        if (FindTextAnywhere(page, GameText(page, "点击屏幕")) is not null)
        {
            return ClosePageConsumerId;
        }

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

        return _frameRouter.Resolve(page)?.Id;
    }

    private async ValueTask ProcessFrameBusDeliveryAsync(
        FrameDelivery<ChaosModePage> delivery,
        CancellationToken cancellationToken)
    {
        var page = delivery.Payload;
        try
        {
            var observationDecision = _actionArbiter.ObservePage(
                page.Observation,
                delivery.ConsumerId);
            if (observationDecision.Kind == ActionDecisionKind.SuppressDuplicate)
            {
                page.Context.LogInfo(observationDecision.Reason, "activity");
                return;
            }

            var handled = delivery.ConsumerId switch
            {
                TargetMemberArchiveConsumerId => await HandleTargetMemberArchiveAsync(
                    page,
                    cancellationToken).ConfigureAwait(false),
                ClosePageConsumerId => await HandleClosePageAsync(
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
                _lastSequence = Math.Max(_lastSequence, page.FrameSequence);
                _lastUnhandledPageSignature = string.Empty;
                _lastUnhandledPageLogAt = DateTimeOffset.MinValue;
                return;
            }

            ReportUnhandledPage(page.Context, page.Recognized);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            page.Context.LogInfo(
                $"帧 #{page.FrameSequence} 消费者 {delivery.ConsumerId} 执行失败：{exception.Message}",
                "warning");
        }
        finally
        {
            page.DisposeFrame();
        }
    }
}
