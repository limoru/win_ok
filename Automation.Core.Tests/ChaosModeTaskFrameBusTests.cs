using Automation.Core.Configuration;
using Automation.Core.Dispatching;
using Automation.Core.DeveloperTools;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Kes.Tasks;
using Kes.Tasks.Configuration;
using Xunit;

namespace Automation.Core.Tests;

public sealed class ChaosModeTaskFrameBusTests
{
    [Theory]
    [InlineData(FrameDispatchErrorKind.NoConsumer, "error")]
    [InlineData(FrameDispatchErrorKind.QueueOverflow, "warning")]
    [InlineData(FrameDispatchErrorKind.ConsumerRejected, "error")]
    [InlineData(FrameDispatchErrorKind.ConsumerTimeout, "error")]
    [InlineData(FrameDispatchErrorKind.ConsumerFailed, "error")]
    [InlineData(FrameDispatchErrorKind.DeliveryRejected, "error")]
    public void FrameDispatchLogSeverityMatchesFailureClass(
        FrameDispatchErrorKind kind,
        string expected)
    {
        Assert.Equal(expected, ChaosModeTask.GetFrameDispatchLogTag(kind));
    }

    [Fact]
    public async Task ActiveCopyMemberConfirmationRoutesToCopyMemberConsumer()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var recognized = new OcrFrameResult(
            "选择要复制卡牌的主战员 确认",
            0.99,
            [
                new OcrTextLine(
                    "选择要复制卡牌的主战员",
                    0.99,
                    new OcrTextRegion(730, 820, 180, 30)),
                new OcrTextLine(
                    "确认",
                    0.99,
                    new OcrTextRegion(1450, 828, 70, 30))
            ]);
        var input = new RecordingInputSink();
        var samples = new RecordingFrameSampleSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "copy-member-route-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            ocr: new StaticOcrProvider(recognized),
            frameSamples: samples);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));

        var request = Assert.Single(samples.Requests);
        Assert.Equal("copy-member", request.ConsumerId);
        await input.WaitForClickCountAsync(1);
        Assert.InRange(input.Clicks[0].X, 0.920, 0.922);
        Assert.InRange(input.Clicks[0].Y, 0.930, 0.932);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task BossEquipmentRewardClaimsBeforeChoiceAndMemberAllocation()
    {
        var frame = CreateUniformFrame(50);
        var matcher = new FixedFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["leveltag"] =
                [
                    new FeatureBox("leveltag", 980, 330, 20, 20, 0.99),
                    new FeatureBox("leveltag", 980, 460, 20, 20, 0.98),
                    new FeatureBox("leveltag", 980, 590, 20, 20, 0.97)
                ]
            });
        var queuedOcr = new Queue<OcrFrameResult>(
            [
                BossEquipmentRewardResult(),
                EquipmentChoiceConfirmResult(),
                EquipmentChoiceConfirmResult(),
                EquipmentInstallSelectionResult(),
                EquipmentInstallResult()
            ]);
        var input = new RecordingInputSink();
        var samples = new RecordingFrameSampleSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "boss-equipment-reward-order-regression",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 139),
                new TestBgraFrameLease(frame, sequence: 140),
                new TestBgraFrameLease(frame, sequence: 141),
                new TestBgraFrameLease(frame, sequence: 142),
                new TestBgraFrameLease(frame, sequence: 143)),
            input,
            ocr: new RecordingOcrProvider(_ => queuedOcr.Dequeue()),
            featureMatcher: matcher,
            frameSamples: samples);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        Assert.Equal("boss-equipment-reward", samples.Requests[0].ConsumerId);
        await WaitUntilAsync(() => task.RunState.BossEquipmentRewardAwaitingAllocation);
        Assert.True(task.RunState.BossEquipmentRewardAwaitingAllocation);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);
        Assert.Equal("equipment", samples.Requests[1].ConsumerId);
        await WaitUntilAsync(() => task.RunState.EquipmentChoiceAwaitingInstall);
        Assert.True(task.RunState.EquipmentChoiceAwaitingInstall);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(3);
        Assert.Equal("equipment", samples.Requests[2].ConsumerId);
        await WaitUntilAsync(() => task.RunState.EquipmentChoiceConfirmAwaitingAllocation);
        Assert.True(task.RunState.EquipmentChoiceConfirmAwaitingAllocation);
        Assert.False(task.RunState.EquipmentChoiceAwaitingInstall);
        Assert.InRange(input.Clicks[0].X, 0.930, 0.934);
        Assert.InRange(input.Clicks[0].Y, 0.900, 0.930);
        Assert.InRange(input.Clicks[1].X, 0.517, 0.519);
        Assert.InRange(input.Clicks[1].Y, 0.453, 0.455);
        Assert.InRange(input.Clicks[2].X, 0.918, 0.925);
        Assert.InRange(input.Clicks[2].Y, 0.925, 0.932);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(4);
        Assert.Equal("equipment", samples.Requests[3].ConsumerId);
        await WaitUntilAsync(() => task.RunState.EquipmentInstallAwaitingConfirm);
        Assert.True(task.RunState.EquipmentInstallAwaitingConfirm);
        Assert.False(task.RunState.EquipmentChoiceConfirmAwaitingAllocation);
        Assert.InRange(input.Clicks[3].X, 0.755, 0.757);
        Assert.InRange(input.Clicks[3].Y, 0.376, 0.380);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(5);
        Assert.Equal("equipment", samples.Requests[4].ConsumerId);
        await WaitUntilAsync(() => !task.RunState.BossEquipmentRewardAwaitingAllocation);
        Assert.False(task.RunState.BossEquipmentRewardAwaitingAllocation);
        Assert.InRange(input.Clicks[4].X, 0.850, 0.970);
        Assert.InRange(input.Clicks[4].Y, 0.880, 0.960);
        Assert.DoesNotContain(
            context.GetRecentLogs(),
            log => log.Contains("NoConsumer", StringComparison.Ordinal)
                   || log.Contains("ConsumerRejected", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task SelectedEquipmentConfirmsWithoutBossRewardState()
    {
        var frame = CreateUniformFrame(50);
        var queuedOcr = new Queue<OcrFrameResult>(
            [EquipmentChoiceConfirmResult(), EquipmentChoiceConfirmResult()]);
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "selected-equipment-confirm-without-boss-state",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 201),
                new TestBgraFrameLease(frame, sequence: 202)),
            input,
            ocr: new RecordingOcrProvider(_ => queuedOcr.Dequeue()));
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        await WaitUntilAsync(() => task.RunState.EquipmentChoiceAwaitingInstall);
        Assert.True(task.RunState.EquipmentChoiceAwaitingInstall);
        Assert.False(task.RunState.BossEquipmentRewardAwaitingAllocation);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);
        await WaitUntilAsync(() => task.RunState.EquipmentChoiceConfirmAwaitingAllocation);
        Assert.False(task.RunState.EquipmentChoiceAwaitingInstall);
        Assert.True(task.RunState.EquipmentChoiceConfirmAwaitingAllocation);
        Assert.False(task.RunState.BossEquipmentRewardAwaitingAllocation);
        Assert.InRange(input.Clicks[1].X, 0.918, 0.925);
        Assert.InRange(input.Clicks[1].Y, 0.925, 0.932);
        Assert.DoesNotContain(
            context.GetRecentLogs(),
            log => log.Contains("NoConsumer", StringComparison.Ordinal)
                   || log.Contains("ConsumerRejected", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task BossEquipmentRewardClicksOnlyOnceUntilRouteChanges()
    {
        var frame = CreateUniformFrame(50);
        var queuedOcr = new Queue<OcrFrameResult>(
            [
                BossEquipmentRewardResult(),
                BossEquipmentRewardResult(),
                BossEquipmentRewardResult()
            ]);
        var input = new RecordingInputSink();
        var samples = new RecordingFrameSampleSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "boss-equipment-reward-once",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 139),
                new TestBgraFrameLease(frame, sequence: 140),
                new TestBgraFrameLease(frame, sequence: 141)),
            input,
            ocr: new RecordingOcrProvider(_ => queuedOcr.Dequeue()),
            featureMatcher: new FixedFeatureMatcher(
                new Dictionary<string, IReadOnlyList<FeatureBox>>()),
            frameSamples: samples);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("\u672a\u58f0\u660e\u81ea\u52a8\u91cd\u8bd5", StringComparison.Ordinal)));
        Assert.Single(input.Clicks);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        Assert.Single(input.Clicks);
        Assert.DoesNotContain(
            context.GetRecentLogs(),
            log => log.Contains("2147483647", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task EquipmentTransitionTimeoutWritesStructuredError()
    {
        var frame = CreateUniformFrame(50);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "equipment-transition-error-log",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame, sequence: 301)),
            new RecordingInputSink(),
            taskConfig: new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey] = 1
            },
            ocr: new StaticOcrProvider(EquipmentChoiceResult()));
        var task = new ChaosModeTask(useFrameDispatchBus: true);
        task.RunState.EquipmentChoiceAwaitingInstall = true;

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("装备流程无法推进", StringComparison.Ordinal)));

        var errorLog = Assert.Single(
            context.GetRecentLogs(),
            log => log.Contains("装备流程无法推进", StringComparison.Ordinal));
        Assert.Contains("awaitingInstall=True", errorLog, StringComparison.Ordinal);
        Assert.Contains("isInstallPage=False", errorLog, StringComparison.Ordinal);
        Assert.Contains("confirmPage=False", errorLog, StringComparison.Ordinal);
        Assert.Contains("讥笑匕首", errorLog, StringComparison.Ordinal);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task RunOncePublishesFrameWithoutWaitingForConsumerClick()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var recognized = new OcrFrameResult(
            "点击屏幕",
            0.99,
            [new OcrTextLine("点击屏幕", 0.99, new OcrTextRegion(700, 430, 200, 40))]);
        var input = new BlockingClickInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "frame-bus-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            ocr: new StaticOcrProvider(recognized));
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        var handled = await task
            .RunOnceAsync(context, cancellation.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(handled);

        await input.ClickEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(input.ClickFinished.Task.IsCompleted);

        input.ReleaseClick.TrySetResult(true);
        await input.ClickFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, input.ClickCount);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task DistinctConsumersExecuteBusinessActionsSerially()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var queuedOcr = new Queue<OcrFrameResult>([CloseResult(), ContinueResult(), EmptyResult()]);
        var ocr = new RecordingOcrProvider(call =>
            call.Width == 1600 && call.Height == 900
                ? queuedOcr.Dequeue()
                : EmptyResult());
        var input = new SerializedClickInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "serialized-frame-bus-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2),
                new TestBgraFrameLease(frame, sequence: 3)),
            input,
            ocr: ocr);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task
            .RunOnceAsync(context, cancellation.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2)));
        await input.WaitForClickCountAsync(1);
        Assert.True(await task
            .RunOnceAsync(context, cancellation.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await task
            .RunOnceAsync(context, cancellation.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2)));

        await Task.Delay(150);
        Assert.Equal(1, input.ClickCount);
        Assert.Equal(1, input.MaxConcurrentClicks);

        input.ReleaseClick();
        await input.WaitForClickCountAsync(2);
        input.ReleaseClick();
        await input.WaitForIdleAsync();

        Assert.Equal(1, input.MaxConcurrentClicks);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task ChaosRewardSettlementDoesNotRepeatFallbackContinueOnUnchangedFrame()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "reward-settlement-continue-once-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2)),
            input,
            ocr: new StaticOcrProvider(ChaosRewardSettlementContinueResult()));
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task
            .RunOnceAsync(context, cancellation.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2)));
        await input.WaitForClickCountAsync(1);

        Assert.True(await task
            .RunOnceAsync(context, cancellation.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2)));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("当前页面动作已经发送", StringComparison.Ordinal)));

        Assert.Single(input.Clicks);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task FrameBusUsesConfirmationRegionAndMappedFullFrameClick()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var calls = new List<(int Width, int Height)>();
        var ocr = new RecordingOcrProvider(call =>
        {
            calls.Add((call.Width, call.Height));
            return call.Width == 1600 && call.Height == 900
                ? SkipResult()
                : ConfirmationResult(700, 300);
        });
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "confirmation-roi-frame-bus-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2)),
            input,
            ocr: ocr);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);

        Assert.Equal([(1600, 900), (1550, 637)], calls);
        var confirmationClick = Assert.Single(input.Clicks.Skip(1));
        Assert.InRange(confirmationClick.X, 0.470, 0.473);
        Assert.InRange(confirmationClick.Y, 0.514, 0.516);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task FrameBusFallsBackToFullOcrAfterConfiguredRegionMisses()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var calls = new List<(int Width, int Height)>();
        var ocr = new RecordingOcrProvider(call =>
        {
            calls.Add((call.Width, call.Height));
            return call.Width == 1600 && call.Height == 900
                ? SkipResult()
                : EmptyResult();
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "confirmation-roi-fallback-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2),
                new TestBgraFrameLease(frame, sequence: 3)),
            new NoOpInputSink(),
            taskConfig: new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey] = 2
            },
            ocr: ocr);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));

        Assert.Equal(
            [(1600, 900), (1550, 637), (1550, 637), (1600, 900)],
            calls);
        Assert.DoesNotContain(
            context.GetRecentLogs(),
            log => log.Contains("NoConsumer", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task FrameBusFallsBackImmediatelyWhenConfirmationPageChanges()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var calls = new List<(int Width, int Height)>();
        var ocrCall = 0;
        var ocr = new RecordingOcrProvider(call =>
        {
            calls.Add((call.Width, call.Height));
            if (call.Width == 1600 && call.Height == 900)
            {
                return SkipResult();
            }

            ocrCall++;
            return ocrCall == 1
                ? ConfirmationResult(700, 300)
                : EmptyResult();
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = new RecordingInputSink();
        var context = new TaskContext(
            "confirmation-roi-page-change-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2),
                new TestBgraFrameLease(frame, sequence: 3)),
            input,
            ocr: ocr);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));

        Assert.Equal(
            [(1600, 900), (1550, 637), (1550, 637), (1600, 900)],
            calls);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task OrdinaryCloseActionDoesNotArmCenterProbeForFollowingContinuePage()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var calls = new List<(int Width, int Height)>();
        var ocr = new RecordingOcrProvider(call =>
        {
            calls.Add((call.Width, call.Height));
            return calls.Count == 1 ? CloseResult() : ContinueResult();
        });
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "ordinary-close-probe-regression",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2)),
            input,
            ocr: ocr);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);

        Assert.Equal([(1600, 900), (1600, 900)], calls);
        Assert.DoesNotContain(
            context.GetRecentLogs(),
            log => log.Contains("回退全图 OCR", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task EventSelectionDoesNotArmCenterProbeBeforeFooterConfirm()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var calls = new List<(int Width, int Height)>();
        var ocrCall = 0;
        var ocr = new RecordingOcrProvider(call =>
        {
            calls.Add((call.Width, call.Height));
            ocrCall++;
            return ocrCall == 1 ? EventOptionsResult() : FooterConfirmResult();
        });
        var matcher = new FixedFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["event1"] = [new FeatureBox("event1", 400, 850, 50, 20, 0.96)],
                ["event2"] = [new FeatureBox("event2", 900, 850, 50, 20, 0.97)]
            });
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "event-confirm-probe-regression",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2)),
            input,
            ocr: ocr,
            featureMatcher: matcher);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);

        Assert.Equal([(1600, 900), (1600, 900)], calls);
        Assert.DoesNotContain(
            context.GetRecentLogs(),
            log => log.Contains("回退全图 OCR", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task FrameBusRetriesAndRecoversEventOptionWithoutStoppingDispatch()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var features = new Dictionary<string, IReadOnlyList<FeatureBox>>
        {
            ["event1"] = [new FeatureBox("event1", 400, 850, 50, 20, 0.96)],
            ["event2"] = [new FeatureBox("event2", 900, 850, 50, 20, 0.97)]
        };
        var matcher = new FixedFeatureMatcher(features);
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "event-retry-frame-bus-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2),
                new TestBgraFrameLease(frame, sequence: 3),
                new TestBgraFrameLease(frame, sequence: 4),
                new TestBgraFrameLease(frame, sequence: 5),
                new TestBgraFrameLease(frame, sequence: 6),
                new TestBgraFrameLease(frame, sequence: 7)),
            input,
            taskConfig: new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey] = 2,
                [ChaosModeConfigSchema.ActionRecoveryWaitFramesKey] = 2,
                ["必选任务优先级"] = new[] { "获得资源" }
            },
            ocr: new StaticOcrProvider(EventOptionsResult()),
            featureMatcher: matcher);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        var firstClick = Assert.Single(input.Clicks);
        Assert.InRange(firstClick.X, 0.265, 0.269);
        Assert.InRange(firstClick.Y, 0.839, 0.843);

        features["check"] = [new FeatureBox("check", 800, 400, 50, 20, 0.99)];
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("总线继续投递帧 #2", StringComparison.Ordinal)));
        Assert.Single(input.Clicks);
        Assert.Equal(1, task.RunState.PendingEventOptionAttempts);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);
        Assert.Equal(2, task.RunState.PendingEventOptionAttempts);

        var retryClick = input.Clicks[1];
        Assert.InRange(retryClick.X, 0.265, 0.269);
        Assert.InRange(retryClick.Y, 0.839, 0.843);
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("总线继续投递帧 #4", StringComparison.Ordinal)));
        Assert.Equal(2, input.Clicks.Count);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(3);
        Assert.Equal(3, task.RunState.PendingEventOptionAttempts);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("总线继续投递帧 #6", StringComparison.Ordinal)));
        Assert.Equal(3, input.Clicks.Count);
        Assert.Equal(3, task.RunState.PendingEventOptionAttempts);

        features.Remove("check");
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(4);
        Assert.Equal(1, task.RunState.PendingEventOptionAttempts);

        await cancellation.CancelAsync();
    }
    [Fact]
    public async Task ClickScreenConsumerRetriesOnConfiguredFreshFramesUpToLimit()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "click-screen-bounded-retry-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2),
                new TestBgraFrameLease(frame, sequence: 3),
                new TestBgraFrameLease(frame, sequence: 4),
                new TestBgraFrameLease(frame, sequence: 5),
                new TestBgraFrameLease(frame, sequence: 6)),
            input,
            taskConfig: new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey] = 2
            },
            ocr: new StaticOcrProvider(ClickScreenResult()));
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("总线继续投递帧 #2", StringComparison.Ordinal)));
        Assert.Single(input.Clicks);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("总线继续投递帧 #4", StringComparison.Ordinal)));
        Assert.Equal(2, input.Clicks.Count);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(3);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("快速重试上限", StringComparison.Ordinal)));
        Assert.Equal(3, input.Clicks.Count);

        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task FooterConfirmRetriesOnConfiguredFreshFramesUpToLimit()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "footer-confirm-bounded-retry-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2),
                new TestBgraFrameLease(frame, sequence: 3),
                new TestBgraFrameLease(frame, sequence: 4),
                new TestBgraFrameLease(frame, sequence: 5),
                new TestBgraFrameLease(frame, sequence: 6)),
            input,
            taskConfig: new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey] = 2
            },
            ocr: new StaticOcrProvider(FooterConfirmResult()));
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("总线继续投递帧 #2", StringComparison.Ordinal)));
        Assert.Single(input.Clicks);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("总线继续投递帧 #4", StringComparison.Ordinal)));
        Assert.Equal(2, input.Clicks.Count);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(3);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("快速重试上限", StringComparison.Ordinal)));
        Assert.Equal(3, input.Clicks.Count);

        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task FooterConfirmDoesNotArmCenterProbeForFollowingDestinyPage()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var calls = new List<(int Width, int Height)>();
        var ocr = new RecordingOcrProvider(call =>
        {
            calls.Add((call.Width, call.Height));
            return calls.Count == 1 ? FooterConfirmResult() : DestinyResult();
        });
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "footer-confirm-probe-regression",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2)),
            input,
            ocr: ocr);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);

        Assert.Equal([(1600, 900), (1600, 900)], calls);
        Assert.DoesNotContain(
            context.GetRecentLogs(),
            log => log.Contains("回退全图 OCR", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task CopyCardChoiceDispatchesToCopyConsumerInsteadOfGenericSkip()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var ocr = new OcrFrameResult(
            "请选择要复制的卡牌。",
            0.99,
            [
                new OcrTextLine("请选择要复制的卡牌。", 0.99, new OcrTextRegion(700, 90, 200, 40)),
                new OcrTextLine("技能卡", 0.99, new OcrTextRegion(600, 160, 80, 20)),
                new OcrTextLine("技能", 0.99, new OcrTextRegion(610, 200, 60, 20)),
                new OcrTextLine("第二张描述关键字", 0.99, new OcrTextRegion(600, 340, 120, 20)),
                new OcrTextLine("跳过", 0.99, new OcrTextRegion(1450, 810, 70, 30))
            ]);
        var matcher = new FixedFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["skill"] = [new FeatureBox("skill", 600, 190, 20, 20, 0.97)]
            });
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "copy-card-choice-frame-bus-regression",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            taskConfig: new Dictionary<string, object>
            {
                ["复制卡牌列表"] = new[] { "技能卡" }
            },
            ocr: new StaticOcrProvider(ocr),
            featureMatcher: matcher);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);

        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.445, 0.448);
        Assert.InRange(click.Y, 0.180, 0.184);
        Assert.Contains(
            context.GetRecentLogs(),
            log => log.Contains("复制卡牌选择: 按优先级选择「技能卡」", StringComparison.Ordinal));
        Assert.DoesNotContain(
            context.GetRecentLogs(),
            log => log.Contains("检测到跳过按钮", StringComparison.Ordinal)
                   || log.Contains("ConsumerRejected", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task GetCardPageDispatchesToCardConsumerAndClosesWithoutPriorityMatch()
    {
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "get-card-consumer-regression",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            ocr: new StaticOcrProvider(GetCardResult()),
            featureMatcher: new FixedFeatureMatcher(new Dictionary<string, IReadOnlyList<FeatureBox>>()));
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);

        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.548, 0.550);
        Assert.InRange(click.Y, 0.935, 0.938);
        Assert.DoesNotContain(
            context.GetRecentLogs(),
            log => log.Contains("NoConsumer", StringComparison.Ordinal)
                   || log.Contains("ConsumerRejected", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task InactiveEquipmentConfirmDispatchesToEquipmentConsumerWithoutError()
    {
        var frame = CreateUniformFrame(150);
        var matcher = new FixedFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["leveltag"] =
                [
                    new FeatureBox("leveltag", 980, 330, 20, 20, 0.99),
                    new FeatureBox("leveltag", 980, 460, 20, 20, 0.98),
                    new FeatureBox("leveltag", 980, 590, 20, 20, 0.97)
                ]
            });
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "inactive-equipment-confirm-regression",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            ocr: new StaticOcrProvider(EquipmentInstallResult()),
            featureMatcher: matcher);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);

        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.755, 0.757);
        Assert.InRange(click.Y, 0.376, 0.380);
        Assert.DoesNotContain(
            context.GetRecentLogs(),
            log => log.Contains("NoConsumer", StringComparison.Ordinal)
                   || log.Contains("ConsumerRejected", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task EquipmentConfirmIsNotBlockedByPreviousMemberSelectionAction()
    {
        var frame = CreateUniformFrame(50);
        var matcher = new FixedFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["leveltag"] =
                [
                    new FeatureBox("leveltag", 980, 330, 20, 20, 0.99),
                    new FeatureBox("leveltag", 980, 460, 20, 20, 0.98),
                    new FeatureBox("leveltag", 980, 590, 20, 20, 0.97)
                ]
            });
        var queuedOcr = new Queue<OcrFrameResult>(
            [EquipmentInstallSelectionResult(), EquipmentInstallResult()]);
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "equipment-confirm-stage-regression",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2)),
            input,
            ocr: new RecordingOcrProvider(_ => queuedOcr.Dequeue()),
            featureMatcher: matcher);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        Assert.True(task.RunState.EquipmentInstallAwaitingConfirm);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);

        Assert.InRange(input.Clicks[0].X, 0.755, 0.757);
        Assert.InRange(input.Clicks[0].Y, 0.376, 0.380);
        Assert.InRange(input.Clicks[1].X, 0.850, 0.970);
        Assert.InRange(input.Clicks[1].Y, 0.880, 0.960);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task EquipmentInstallConfirmationIsClickedOnlyOnceWhilePageRemains()
    {
        var frame = CreateUniformFrame(50);
        var matcher = new FixedFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["leveltag"] =
                [
                    new FeatureBox("leveltag", 980, 330, 20, 20, 0.99),
                    new FeatureBox("leveltag", 980, 460, 20, 20, 0.98),
                    new FeatureBox("leveltag", 980, 590, 20, 20, 0.97)
                ]
            });
        var queuedOcr = new Queue<OcrFrameResult>(
            [
                EquipmentInstallSelectionResult(),
                EquipmentInstallResult(),
                EquipmentInstallResult(),
                EquipmentInstallResult()
            ]);
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "equipment-install-confirm-once",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2),
                new TestBgraFrameLease(frame, sequence: 3),
                new TestBgraFrameLease(frame, sequence: 4)),
            input,
            ocr: new RecordingOcrProvider(_ => queuedOcr.Dequeue()),
            featureMatcher: matcher);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        Assert.True(task.RunState.EquipmentInstallAwaitingConfirm);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);
        Assert.Equal(1, task.RunState.EquipmentInstallConfirmAttempts);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await WaitUntilAsync(() => context.GetRecentLogs().Any(log =>
            log.Contains("同一安装页只点击一次", StringComparison.Ordinal)));
        Assert.Equal(2, input.Clicks.Count);
        Assert.Equal(1, task.RunState.EquipmentInstallConfirmAttempts);
        Assert.InRange(input.Clicks[1].X, 0.850, 0.970);
        Assert.InRange(input.Clicks[1].Y, 0.880, 0.960);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task EquipmentChoiceToInstallStageIsNotBlockedByPriorActionBoundary()
    {
        var frame = CreateUniformFrame(50);
        var matcher = new FixedFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["leveltag"] =
                [
                    new FeatureBox("leveltag", 980, 330, 20, 20, 0.99),
                    new FeatureBox("leveltag", 980, 460, 20, 20, 0.98),
                    new FeatureBox("leveltag", 980, 590, 20, 20, 0.97)
                ]
            });
        var queuedOcr = new Queue<OcrFrameResult>(
            [EquipmentChoiceResult(), EquipmentInstallMisreadResult()]);
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "equipment-choice-to-install-stage-regression",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2)),
            input,
            ocr: new RecordingOcrProvider(_ => queuedOcr.Dequeue()),
            featureMatcher: matcher);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(1);
        Assert.False(task.RunState.EquipmentInstallAwaitingConfirm);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        await input.WaitForClickCountAsync(2);

        Assert.True(task.RunState.EquipmentInstallAwaitingConfirm);
        Assert.InRange(input.Clicks[0].X, 0.517, 0.519);
        Assert.InRange(input.Clicks[0].Y, 0.453, 0.455);
        Assert.InRange(input.Clicks[1].X, 0.755, 0.757);
        Assert.InRange(input.Clicks[1].Y, 0.376, 0.380);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task EquipmentChoiceWaitsForMisreadInstallPageWithoutReclickingCandidate()
    {
        var frame = CreateUniformFrame(50);
        var matcher = new FixedFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["leveltag"] =
                [
                    new FeatureBox("leveltag", 980, 330, 20, 20, 0.99),
                    new FeatureBox("leveltag", 980, 460, 20, 20, 0.98),
                    new FeatureBox("leveltag", 980, 590, 20, 20, 0.97)
                ]
            });
        var queuedOcr = new Queue<OcrFrameResult>(
            [EquipmentChoiceResult(), EquipmentChoiceResult(), EquipmentInstallMisreadResult()]);
        var input = new RecordingInputSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "equipment-misread-install-regression",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new QueuedFrameSource(
                new TestBgraFrameLease(frame, sequence: 1),
                new TestBgraFrameLease(frame, sequence: 2),
                new TestBgraFrameLease(frame, sequence: 3)),
            input,
            ocr: new RecordingOcrProvider(_ => queuedOcr.Dequeue()),
            featureMatcher: matcher);
        var task = new ChaosModeTask();

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        Assert.Single(input.Clicks);
        Assert.True(task.RunState.EquipmentChoiceAwaitingInstall);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        Assert.Single(input.Clicks);
        Assert.True(task.RunState.EquipmentChoiceAwaitingInstall);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        Assert.Equal(2, input.Clicks.Count);
        Assert.False(task.RunState.EquipmentChoiceAwaitingInstall);
        Assert.True(task.RunState.EquipmentInstallAwaitingConfirm);
        Assert.InRange(input.Clicks[0].X, 0.517, 0.519);
        Assert.InRange(input.Clicks[0].Y, 0.453, 0.455);
        Assert.InRange(input.Clicks[1].X, 0.755, 0.757);
        Assert.InRange(input.Clicks[1].Y, 0.376, 0.380);
        await cancellation.CancelAsync();
    }

    private static OcrFrameResult SkipResult()
        => new(
            "跳过",
            0.99,
            [new OcrTextLine("跳过", 0.99, new OcrTextRegion(1485, 810, 60, 30))]);

    private static OcrFrameResult CloseResult()
        => new(
            "关闭",
            0.99,
            [new OcrTextLine("关闭", 0.99, new OcrTextRegion(790, 820, 80, 30))]);

    private static OcrFrameResult ContinueResult()
        => new(
            "继续",
            0.99,
            [new OcrTextLine("继续", 0.99, new OcrTextRegion(800, 820, 80, 30))]);

    private static OcrFrameResult ChaosRewardSettlementContinueResult()
        => new(
            "结算 继续",
            0.99,
            [
                new OcrTextLine("结算", 0.99, new OcrTextRegion(845, 45, 76, 30)),
                new OcrTextLine("继续", 0.99, new OcrTextRegion(800, 820, 80, 30))
            ]);

    private static OcrFrameResult ConfirmationResult(int x, int y)
        => new(
            "确认 取消",
            0.99,
            [
                new OcrTextLine("确认", 0.99, new OcrTextRegion(x, y, 80, 25)),
                new OcrTextLine("取消", 0.99, new OcrTextRegion(x + 200, y, 80, 25))
            ]);

    private static OcrFrameResult FooterConfirmResult()
        => new(
            "确认",
            0.99,
            [new OcrTextLine("确认", 0.99, new OcrTextRegion(1450, 820, 60, 30))]);

    private static OcrFrameResult ClickScreenResult()
        => new(
            "点击屏幕",
            0.99,
            [new OcrTextLine("点击屏幕", 0.99, new OcrTextRegion(700, 430, 200, 40))]);

    private static OcrFrameResult DestinyResult()
        => new(
            "请选择你的命运 命运一",
            0.99,
            [
                new OcrTextLine("请选择你的命运", 0.99, new OcrTextRegion(740, 825, 120, 25)),
                new OcrTextLine("命运一", 0.99, new OcrTextRegion(500, 470, 100, 30))
            ]);

    private static OcrFrameResult GetCardResult()
        => new(
            "获得卡牌 贫血 咒术 关闭",
            0.99,
            [
                new OcrTextLine("获得卡牌", 0.99, new OcrTextRegion(723, 76, 157, 50)),
                new OcrTextLine("贫血", 0.99, new OcrTextRegion(693, 208, 79, 49)),
                new OcrTextLine("咒术", 0.99, new OcrTextRegion(693, 246, 111, 45)),
                new OcrTextLine("关闭", 0.99, new OcrTextRegion(860, 829, 38, 29))
            ]);

    private static OcrFrameResult EquipmentInstallResult()
        => new(
            "装备 请选择主战员。讥笑匕首 攻击力 确认",
            0.99,
            [
                new OcrTextLine("装备", 0.99, new OcrTextRegion(756, 87, 86, 56)),
                new OcrTextLine("请选择主战员。", 0.99, new OcrTextRegion(1387, 100, 173, 37)),
                new OcrTextLine("讥笑匕首", 0.99, new OcrTextRegion(345, 348, 126, 42)),
                new OcrTextLine("攻击力", 0.99, new OcrTextRegion(302, 398, 84, 37)),
                new OcrTextLine("击败敌人时，全体敌人脆弱2", 0.99, new OcrTextRegion(300, 448, 314, 32)),
                new OcrTextLine("确认", 0.99, new OcrTextRegion(1435, 813, 76, 45))
            ]);

    private static OcrFrameResult BossEquipmentRewardResult()
        => new(
            string.Empty,
            0.90,
            [
                new OcrTextLine("\u672A\u53D1\u73B0\u7684\u95EA\u5149", 1.00, new OcrTextRegion(1308, 168, 156, 39)),
                new OcrTextLine("\u88C5\u5907", 1.00, new OcrTextRegion(1362, 272, 192, 65)),
                new OcrTextLine("\u8DF3\u8FC7", 1.00, new OcrTextRegion(1139, 801, 77, 46)),
                new OcrTextLine("\u83B7\u5F97\u2461", 0.82, new OcrTextRegion(1431, 798, 120, 51)),
                new OcrTextLine("5", 0.106, new OcrTextRegion(641, 338, 23, 14))
            ]);

    private static OcrFrameResult EquipmentChoiceResult()
        => new(
            "装备 讥笑匕首 攻击力 突击护手 防御力",
            0.99,
            [
                new OcrTextLine("装备", 0.99, new OcrTextRegion(756, 87, 86, 56)),
                new OcrTextLine("讥笑匕首", 0.99, new OcrTextRegion(700, 210, 126, 42)),
                new OcrTextLine("攻击力", 0.99, new OcrTextRegion(650, 260, 84, 37)),
                new OcrTextLine("击败敌人时，全体敌人脆弱2", 0.99, new OcrTextRegion(650, 320, 314, 32)),
                new OcrTextLine("突击护手", 0.99, new OcrTextRegion(700, 510, 126, 42)),
                new OcrTextLine("防御力", 0.99, new OcrTextRegion(650, 560, 84, 37)),
                new OcrTextLine("战斗开始时，反击3", 0.99, new OcrTextRegion(650, 620, 314, 32))
            ]);

    private static OcrFrameResult EquipmentChoiceConfirmResult()
        => new(
            "装备 请选择装备。量子收集器 生命值 RFS-17 攻击力 提炼 确认",
            0.99,
            [
                new OcrTextLine("装备", 0.99, new OcrTextRegion(757, 88, 84, 52)),
                new OcrTextLine("请选择装备。", 0.99, new OcrTextRegion(1412, 101, 147, 36)),
                new OcrTextLine("量子收集器", 0.99, new OcrTextRegion(656, 200, 155, 39)),
                new OcrTextLine("生命值", 0.99, new OcrTextRegion(616, 250, 83, 33)),
                new OcrTextLine("RFS-17", 0.99, new OcrTextRegion(658, 502, 104, 35)),
                new OcrTextLine("攻击力", 0.99, new OcrTextRegion(617, 552, 79, 30)),
                new OcrTextLine("提炼", 0.99, new OcrTextRegion(942, 813, 109, 48)),
                new OcrTextLine("确认", 0.99, new OcrTextRegion(1437, 813, 74, 45))
            ]);

    private static OcrFrameResult EquipmentInstallSelectionResult()
        => new(
            "装备 请选择主战员。讥笑匕首 攻击力",
            0.99,
            [
                new OcrTextLine("装备", 0.99, new OcrTextRegion(756, 87, 86, 56)),
                new OcrTextLine("请选择主战员。", 0.99, new OcrTextRegion(1387, 100, 173, 37)),
                new OcrTextLine("讥笑匕首", 0.99, new OcrTextRegion(345, 348, 126, 42)),
                new OcrTextLine("攻击力", 0.99, new OcrTextRegion(302, 398, 84, 37)),
                new OcrTextLine("击败敌人时，全体敌人脆弱2", 0.99, new OcrTextRegion(300, 448, 314, 32))
            ]);

    private static OcrFrameResult EquipmentInstallMisreadResult()
        => new(
            "装备 请选择装备。未知护符 攻击力 长按已佩戴装备的图标可进行比较。 确认",
            0.99,
            [
                new OcrTextLine("装备", 0.99, new OcrTextRegion(756, 87, 86, 56)),
                new OcrTextLine("请选择装备。", 0.99, new OcrTextRegion(1412, 101, 147, 36)),
                new OcrTextLine("未知护符", 0.99, new OcrTextRegion(336, 350, 128, 39)),
                new OcrTextLine("攻击力", 0.99, new OcrTextRegion(302, 398, 84, 37)),
                new OcrTextLine("击败敌人时，全体敌人脆弱2", 0.99, new OcrTextRegion(300, 448, 314, 32)),
                new OcrTextLine("长按已佩戴装备的图标可进行比较。", 0.99, new OcrTextRegion(314, 634, 349, 29)),
                new OcrTextLine("确认", 0.99, new OcrTextRegion(1437, 813, 74, 45))
            ]);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not reached before the test timeout.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static BgraFrame CreateUniformFrame(byte value)
    {
        var pixels = new byte[1600 * 900 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = value;
            pixels[index + 1] = value;
            pixels[index + 2] = value;
            pixels[index + 3] = 255;
        }

        return new BgraFrame(1600, 900, pixels);
    }
    private static OcrFrameResult EventOptionsResult()
        => new(
            "获得资源 挑战敌人",
            0.99,
            [
                new OcrTextLine("获得资源", 0.99, new OcrTextRegion(260, 700, 180, 30)),
                new OcrTextLine("挑战敌人", 0.99, new OcrTextRegion(760, 700, 180, 30))
            ]);
    private static OcrFrameResult EmptyResult()
        => new(string.Empty, 0, []);
    private sealed class FixedFeatureMatcher(
        IReadOnlyDictionary<string, IReadOnlyList<FeatureBox>> features) : IFeatureMatcher
    {
        public IReadOnlyList<FeatureBox> Find(
            BgraFrame frame,
            string featureName,
            FeatureSearchRegion? region = null,
            double? threshold = null)
        {
            if (!features.TryGetValue(featureName, out var boxes))
            {
                return [];
            }

            return boxes
                .Where(box => threshold is null || box.Confidence >= threshold.Value)
                .Where(box => region is null
                    || (box.CenterX(frame.Width) >= region.Left
                        && box.CenterX(frame.Width) <= region.Right
                        && box.CenterY(frame.Height) >= region.Top
                        && box.CenterY(frame.Height) <= region.Bottom))
                .ToArray();
        }
    }
    private sealed class StaticFrameSource(FrameLease frame) : IFrameSource
    {
        public ValueTask<FrameLease> GetLatestAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(frame);

        public ValueTask<FrameLease> WaitNextAsync(
            long afterSequence,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(frame);
    }

    private sealed class QueuedFrameSource(params FrameLease[] frames) : IFrameSource
    {
        private readonly Queue<FrameLease> _frames = new(frames);

        public ValueTask<FrameLease> GetLatestAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Dequeue());

        public ValueTask<FrameLease> WaitNextAsync(
            long afterSequence,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Dequeue());

        private FrameLease Dequeue()
            => _frames.Count > 0
                ? _frames.Dequeue()
                : throw new InvalidOperationException("No queued test frame is available.");
    }

    private sealed class RecordingOcrProvider(Func<BgraFrame, OcrFrameResult> recognize) : IOcrProvider
    {
        public OcrFrameResult Recognize(BgraFrame frame) => recognize(frame);
    }

    private sealed class RecordingInputSink : IInputSink
    {
        private readonly object _gate = new();
        private readonly List<RelativePoint> _clicks = [];
        private readonly SemaphoreSlim _clickSignal = new(0);

        public IReadOnlyList<RelativePoint> Clicks
        {
            get
            {
                lock (_gate)
                {
                    return _clicks.ToArray();
                }
            }
        }

        public ValueTask ClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _clicks.Add(point);
            }

            _clickSignal.Release();
            return ValueTask.CompletedTask;
        }

        public async Task WaitForClickCountAsync(int count)
        {
            while (Clicks.Count < count)
            {
                await _clickSignal
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
        }

        public ValueTask SwipeAsync(
            RelativePoint from,
            RelativePoint to,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask KeyAsync(InputKey key, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask TextAsync(string text, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class RecordingFrameSampleSink : IFrameSampleArchiveSink
    {
        public bool IsEnabled => true;
        public List<FrameSampleRequest> Requests { get; } = [];

        public ValueTask<FrameSampleArchiveResult?> CaptureAsync(
            FrameSampleRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult<FrameSampleArchiveResult?>(null);
        }
    }

    private sealed class NoOpInputSink : IInputSink
    {
        public ValueTask ClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SwipeAsync(
            RelativePoint from,
            RelativePoint to,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask KeyAsync(InputKey key, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask TextAsync(string text, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
    private sealed class StaticOcrProvider(OcrFrameResult result) : IOcrProvider
    {
        public OcrFrameResult Recognize(BgraFrame frame) => result;
    }

    private sealed class SerializedClickInputSink : IInputSink
    {
        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource<bool>> _releaseWaiters = new();
        private readonly SemaphoreSlim _clickSignal = new(0);
        private int _activeClicks;
        private int _maxConcurrentClicks;
        private int _clickCount;

        public int ClickCount => Volatile.Read(ref _clickCount);
        public int MaxConcurrentClicks => Volatile.Read(ref _maxConcurrentClicks);

        public void ReleaseClick()
        {
            TaskCompletionSource<bool>? waiter;
            lock (_gate)
            {
                waiter = _releaseWaiters.Count > 0 ? _releaseWaiters.Dequeue() : null;
            }

            waiter?.TrySetResult(true);
        }

        public async ValueTask ClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeClicks);
            UpdateMaxConcurrent(active);
            var release = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _releaseWaiters.Enqueue(release);
            }

            Interlocked.Increment(ref _clickCount);
            _clickSignal.Release();
            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref _activeClicks);
        }

        public async Task WaitForClickCountAsync(int count)
        {
            while (ClickCount < count)
            {
                await _clickSignal
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
        }

        public async Task WaitForIdleAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (Volatile.Read(ref _activeClicks) != 0)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("Clicks did not finish.");
                }

                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        public ValueTask SwipeAsync(
            RelativePoint from,
            RelativePoint to,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask KeyAsync(InputKey key, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask TextAsync(string text, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        private void UpdateMaxConcurrent(int active)
        {
            var current = Volatile.Read(ref _maxConcurrentClicks);
            while (active > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maxConcurrentClicks,
                    active,
                    current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class BlockingClickInputSink : IInputSink
    {
        public TaskCompletionSource<bool> ClickEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseClick { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ClickFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _clickCount;

        public int ClickCount => Volatile.Read(ref _clickCount);

        public async ValueTask ClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _clickCount);
            ClickEntered.TrySetResult(true);
            await ReleaseClick.Task.WaitAsync(cancellationToken);
            ClickFinished.TrySetResult(true);
        }

        public ValueTask SwipeAsync(
            RelativePoint from,
            RelativePoint to,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask KeyAsync(InputKey key, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask TextAsync(string text, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class TestBgraFrameLease(BgraFrame frame, long sequence = 1) : FrameLease(
        sequence,
        DateTimeOffset.Now.AddMinutes(sequence),
        frame.Width,
        frame.Height,
        "BGRA32"),
        IBgraFrameLease
    {
        public BgraFrame Frame { get; } = frame;

        public override void Dispose()
        {
        }
    }
}
