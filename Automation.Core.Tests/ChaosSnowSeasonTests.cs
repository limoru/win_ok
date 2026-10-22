using Automation.Core.Configuration;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Kes.Tasks;
using Kes.Tasks.Configuration;
using Xunit;

namespace Automation.Core.Tests;

public sealed class ChaosSnowSeasonTests
{
    [Fact]
    public async Task SnowWhiteMemoryClicksScreenCenterOnlyForSnowSeason()
    {
        var snowInput = new RecordingInputSink();
        var snowTask = new ChaosModeTask();
        var snowContext = CreateContext(
            CreateOcr("纯白的记忆", Line("纯白的记忆", 650, 390, 300, 40)),
            snowInput,
            new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.SnowboundPromiseSeason
            });

        Assert.True(await snowTask.RunOnceAsync(snowContext, CancellationToken.None));
        var click = Assert.Single(snowInput.Clicks);
        Assert.InRange(click.X, 0.499, 0.501);
        Assert.InRange(click.Y, 0.499, 0.501);

        var otherInput = new RecordingInputSink();
        var otherTask = new ChaosModeTask();
        var otherContext = CreateContext(
            CreateOcr("纯白的记忆", Line("纯白的记忆", 650, 390, 300, 40)),
            otherInput,
            new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.NoSeasonContent
            });

        await otherTask.RunOnceAsync(otherContext, CancellationToken.None);
        Assert.Empty(otherInput.Clicks);
    }

    [Fact]
    public async Task SnowEventWithoutIconPressesF1OnlyOnceForSamePage()
    {
        var task = new ChaosModeTask();
        var ocr = CreateOcr("铭刻的记忆", Line("铭刻的记忆", 610, 90, 380, 42));
        var config = new Dictionary<string, object>
        {
            [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.SnowboundPromiseSeason
        };
        var input = new RecordingInputSink();

        Assert.True(await task.RunOnceAsync(
            CreateContext(ocr, input, config),
            CancellationToken.None));
        Assert.Equal([InputKey.F1], input.Keys);

        Assert.True(await task.RunOnceAsync(
            CreateContext(ocr, input, config),
            CancellationToken.None));
        Assert.Equal([InputKey.F1], input.Keys);
    }

    [Theory]
    [InlineData("领受计划的指示", SnowSeasonPhase.PlanMemoryChoice)]
    [InlineData("精密加工记忆", SnowSeasonPhase.PrecisionEventSelected)]
    public async Task SnowSeasonEventsTakePriorityOverOrdinaryEventOptions(
        string preferredDescription,
        SnowSeasonPhase expectedPhase)
    {
        var input = new RecordingInputSink();
        var task = new ChaosModeTask();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["event1"] = [new FeatureBox("event1", 400, 850, 50, 20, 0.96)],
                ["event2"] = [new FeatureBox("event2", 900, 850, 50, 20, 0.98)]
            });
        var ocr = CreateOcr(
            "普通事件选项 " + preferredDescription,
            Line("普通事件选项", 260, 690, 240, 34),
            Line(preferredDescription, 760, 690, 280, 34));

        Assert.True(await task.RunOnceAsync(
            CreateContext(
                ocr,
                input,
                new Dictionary<string, object>
                {
                    [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.SnowboundPromiseSeason
                },
                matcher),
            CancellationToken.None));

        Assert.Equal(expectedPhase, task.RunState.SnowSeasonPhase);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.55, 0.58);
        Assert.InRange(click.Y, 0.81, 0.85);
    }

    [Fact]
    public async Task ConfiguredSnowMemoryIsSelectedBeforeAnyFooterConfirmation()
    {
        var input = new RecordingInputSink();
        var task = new ChaosModeTask();
        var ocr = CreateSnowMemoryOcr("目标记忆", "霜之一", "霜之二");

        Assert.True(await task.RunOnceAsync(
            CreateContext(
                ocr,
                input,
                new Dictionary<string, object>
                {
                    [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.SnowboundPromiseSeason,
                    [ChaosModeConfigSchema.SnowMemoryPriorityKey] = new[] { "目标记忆" }
                }),
            CancellationToken.None));

        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.145, 0.165);
        Assert.InRange(click.Y, 0.37, 0.42);
        Assert.Equal(SnowSeasonPhase.PlanCardSelected, task.RunState.SnowSeasonPhase);
    }

    [Fact]
    public async Task SnowMemoryRefreshesEachCardThreeTimesThenChoosesRandomCard()
    {
        var task = new ChaosModeTask();
        var config = new Dictionary<string, object>
        {
            [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.SnowboundPromiseSeason,
            [ChaosModeConfigSchema.SnowMemoryPriorityKey] = Array.Empty<string>()
        };
        var ocr = CreateSnowMemoryOcr("霜之一", "霜之二", "霜之三");
        var refreshClicks = new List<RelativePoint>();

        for (var index = 0; index < 9; index++)
        {
            var input = new RecordingInputSink();
            Assert.True(await task.RunOnceAsync(
                CreateContext(ocr, input, config),
                CancellationToken.None));
            refreshClicks.Add(Assert.Single(input.Clicks));
        }

        Assert.Equal([3, 3, 3], task.RunState.SnowMemoryRefreshAttempts);
        foreach (var group in refreshClicks.Chunk(3))
        {
            Assert.Single(group.Select(point => Math.Round(point.X, 4)).Distinct());
            Assert.All(group, point => Assert.InRange(point.Y, 0.85, 0.87));
        }

        var randomInput = new RecordingInputSink();
        Assert.True(await task.RunOnceAsync(
            CreateContext(ocr, randomInput, config),
            CancellationToken.None));
        var randomClick = Assert.Single(randomInput.Clicks);
        Assert.InRange(randomClick.Y, 0.37, 0.42);
        Assert.Equal(SnowSeasonPhase.PlanCardSelected, task.RunState.SnowSeasonPhase);
    }

    [Fact]
    public async Task SnowMemoryWaitsWhenRerollButtonsAreNotFullyRecognized()
    {
        var input = new RecordingInputSink();
        var task = new ChaosModeTask();
        var ocr = CreateOcr(
            "选择铭刻的记忆 霜之一 霜之二 霜之三",
            Line("选择铭刻的记忆", 560, 90, 360, 38),
            Line("霜之一", 150, 350, 200, 38),
            Line("霜之二", 550, 350, 200, 38),
            Line("霜之三", 950, 350, 200, 38),
            Line("重新搜索 3/3", 150, 760, 180, 30),
            Line("重新搜索 3/3", 550, 760, 180, 30));

        Assert.True(await task.RunOnceAsync(
            CreateContext(
                ocr,
                input,
                new Dictionary<string, object>
                {
                    [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.SnowboundPromiseSeason,
                    [ChaosModeConfigSchema.SnowMemoryPriorityKey] = Array.Empty<string>()
                }),
            CancellationToken.None));

        Assert.Empty(input.Clicks);
        Assert.Equal([0, 0, 0], task.RunState.SnowMemoryRefreshAttempts);
    }

    [Fact]
    public async Task PrecisionMemoryUsesSeparatePriorityThenConfirmsAndCloses()
    {
        var task = new ChaosModeTask();
        var config = new Dictionary<string, object>
        {
            [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.SnowboundPromiseSeason,
            [ChaosModeConfigSchema.SnowPrecisionMemoryPriorityKey] = new[] { "目标加工记忆" }
        };

        var processingInput = new RecordingInputSink();
        var processingTask = new ChaosModeTask();
        var processingOcr = CreateOcr(
            "加工记忆 1 精密加工记忆 1",
            Line("加工记忆 1", 1120, 805, 180, 34),
            Line("精密加工记忆 1", 1120, 850, 240, 34));
        Assert.True(await processingTask.RunOnceAsync(
            CreateContext(processingOcr, processingInput, config),
            CancellationToken.None));
        var processingClick = Assert.Single(processingInput.Clicks);
        Assert.InRange(processingClick.X, 0.73, 0.79);
        Assert.Equal(SnowSeasonPhase.PrecisionCardChoice, processingTask.RunState.SnowSeasonPhase);

        var selectionInput = new RecordingInputSink();
        var selectionOcr = CreateOcr(
            "选择铭刻的记忆（加工记忆） 目标加工记忆 普通记忆",
            Line("选择铭刻的记忆（加工记忆）", 510, 90, 520, 38),
            Line("目标加工记忆", 170, 360, 220, 38),
            Line("普通记忆", 570, 360, 200, 38),
            Line("普通记忆二", 970, 360, 200, 38));
        Assert.True(await processingTask.RunOnceAsync(
            CreateContext(selectionOcr, selectionInput, config),
            CancellationToken.None));
        var selectionClick = Assert.Single(selectionInput.Clicks);
        Assert.InRange(selectionClick.Y, 0.38, 0.43);
        Assert.Equal(SnowSeasonPhase.PrecisionEventSelected, processingTask.RunState.SnowSeasonPhase);

        var precisionInput = new RecordingInputSink();
        var precisionOcr = CreateOcr(
            "精密加工记忆 1",
            Line("精密加工记忆 1", 720, 820, 260, 38));
        Assert.True(await processingTask.RunOnceAsync(
            CreateContext(precisionOcr, precisionInput, config),
            CancellationToken.None));
        Assert.Single(precisionInput.Clicks);
        Assert.Equal(SnowSeasonPhase.PrecisionConfirmPending, processingTask.RunState.SnowSeasonPhase);

        var confirmInput = new RecordingInputSink();
        var confirmOcr = CreateOcr(
            "确认 取消",
            Line("确认", 900, 600, 80, 36),
            Line("取消", 1040, 600, 80, 36));
        Assert.True(await processingTask.RunOnceAsync(
            CreateContext(confirmOcr, confirmInput, config),
            CancellationToken.None));
        Assert.Single(confirmInput.Clicks);

        var closeInput = new RecordingInputSink();
        var closeOcr = CreateOcr(
            "点击屏幕可关闭",
            Line("点击屏幕可关闭", 700, 470, 260, 38));
        TaskContext? closeContext = null;
        for (var attempt = 0; attempt < 2 && closeInput.Clicks.Count == 0; attempt++)
        {
            closeContext = CreateContext(closeOcr, closeInput, config);
            Assert.True(await processingTask.RunOnceAsync(closeContext, CancellationToken.None));
        }

        Assert.True(
            closeInput.Clicks.Count == 1,
            string.Join(Environment.NewLine, closeContext?.GetRecentLogs() ?? []));
    }

    [Fact]
    public async Task SnowMemberSelectionClicksTargetBeforeFooterConfirm()
    {
        var task = new ChaosModeTask();
        task.RunState.SnowSeasonPhase = SnowSeasonPhase.PlanMemberChoice;
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["target_member_large"] = [new FeatureBox("target_member_large", 1200, 350, 50, 50, 0.99)]
            });
        var ocr = CreateOcr(
            "请选择主战员 确认",
            Line("请选择主战员", 650, 150, 240, 36),
            Line("确认", 1450, 830, 70, 32));
        var config = new Dictionary<string, object>
        {
            [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.SnowboundPromiseSeason
        };

        var selectInput = new RecordingInputSink();
        Assert.True(await task.RunOnceAsync(
            CreateContext(ocr, selectInput, config, matcher),
            CancellationToken.None));
        var memberClick = Assert.Single(selectInput.Clicks);
        Assert.InRange(memberClick.X, 0.75, 0.76);
        Assert.Equal(1, task.RunState.SnowMemberAttemptIndex);
        Assert.Equal(SnowSeasonPhase.PlanMemberChoice, task.RunState.SnowSeasonPhase);

        var confirmInput = new RecordingInputSink();
        Assert.True(await task.RunOnceAsync(
            CreateContext(ocr, confirmInput, config, matcher),
            CancellationToken.None));
        var confirmClick = Assert.Single(confirmInput.Clicks);
        Assert.InRange(confirmClick.X, 0.91, 0.93);
        Assert.InRange(confirmClick.Y, 0.92, 0.94);
        Assert.Equal(SnowSeasonPhase.None, task.RunState.SnowSeasonPhase);
    }

    [Fact]
    public async Task SnowMemoryEndClicksGoToMemoryEndAndOldPhaseDoesNotClaimUnrelatedFrame()
    {
        var task = new ChaosModeTask();
        task.RunState.SnowSeasonPhase = SnowSeasonPhase.PrecisionCardChoice;
        var config = new Dictionary<string, object>
        {
            [ChaosModeConfigSchema.SeasonSelectorKey] = ChaosModeConfigSchema.SnowboundPromiseSeason
        };
        var unrelatedInput = new RecordingInputSink();
        Assert.False(await task.RunOnceAsync(
            CreateContext(CreateOcr("普通记忆页面", Line("普通记忆页面", 600, 300, 300, 38)), unrelatedInput, config),
            CancellationToken.None));
        Assert.Empty(unrelatedInput.Clicks);

        var endInput = new RecordingInputSink();
        Assert.True(await task.RunOnceAsync(
            CreateContext(CreateOcr("前往记忆之末", Line("前往记忆之末", 650, 790, 300, 38)), endInput, config),
            CancellationToken.None));
        var endClick = Assert.Single(endInput.Clicks);
        Assert.InRange(endClick.X, 0.49, 0.52);
        Assert.InRange(endClick.Y, 0.88, 0.91);
    }

    private static OcrFrameResult CreateSnowMemoryOcr(
        string first,
        string second,
        string third)
        => CreateOcr(
            $"选择铭刻的记忆 {first} {second} {third}",
            Line("选择铭刻的记忆", 560, 90, 360, 38),
            Line(first, 150, 350, 200, 38),
            Line(second, 550, 350, 200, 38),
            Line(third, 950, 350, 200, 38),
            Line("重新搜索 3/3", 150, 760, 180, 30),
            Line("重新搜索 3/3", 550, 760, 180, 30),
            Line("重新搜索 3/3", 950, 760, 180, 30));

    private static OcrFrameResult CreateOcr(string text, params OcrTextLine[] lines)
        => new(text, 0.99, lines);

    private static OcrTextLine Line(string text, int x, int y, int width, int height)
        => new(text, 0.99, new OcrTextRegion(x, y, width, height));

    private static TaskContext CreateContext(
        OcrFrameResult ocr,
        IInputSink input,
        IReadOnlyDictionary<string, object>? config,
        IFeatureMatcher? matcher = null)
        => new(
            "snow-season-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(CreateFrame())),
            input,
            taskConfig: config,
            ocr: new StaticOcrProvider(ocr),
            featureMatcher: matcher ?? new StaticFeatureMatcher(
                new Dictionary<string, IReadOnlyList<FeatureBox>>()));

    private static BgraFrame CreateFrame(int width = 1600, int height = 900)
        => new(width, height, new byte[width * height * 4]);

    private sealed class StaticFrameSource(FrameLease frame) : IFrameSource
    {
        private static long _lastSequence = 30000;

        public ValueTask<FrameLease> GetLatestAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Next());

        public ValueTask<FrameLease> WaitNextAsync(
            long afterSequence,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Next());

        private FrameLease Next()
        {
            var layer = (IBgraFrameLease)frame;
            var sequence = Interlocked.Increment(ref _lastSequence);
            return new TestBgraFrameLease(layer.Frame, sequence);
        }
    }

    private sealed class TestBgraFrameLease(BgraFrame frame, long sequence = 1) : FrameLease(
        sequence,
        DateTimeOffset.Now,
        frame.Width,
        frame.Height,
        "BGRA32"), IBgraFrameLease
    {
        public BgraFrame Frame { get; } = frame;

        public override void Dispose()
        {
        }
    }

    private sealed class StaticOcrProvider(OcrFrameResult result) : IOcrProvider
    {
        public OcrFrameResult Recognize(BgraFrame frame) => result;
    }

    private sealed class StaticFeatureMatcher(
        IReadOnlyDictionary<string, IReadOnlyList<FeatureBox>> features) : IFeatureMatcher
    {
        public IReadOnlyList<FeatureBox> Find(
            BgraFrame frame,
            string featureName,
            FeatureSearchRegion? region = null,
            double? threshold = null)
            => features.TryGetValue(featureName, out var boxes)
                ? boxes
                : [];
    }

    private sealed class RecordingInputSink : IInputSink
    {
        public List<RelativePoint> Clicks { get; } = [];
        public List<InputKey> Keys { get; } = [];

        public ValueTask ClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
        {
            Clicks.Add(point);
            return ValueTask.CompletedTask;
        }

        public ValueTask SwipeAsync(
            RelativePoint from,
            RelativePoint to,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ScrollAsync(
            RelativePoint point,
            int wheelNotches,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask KeyAsync(
            InputKey key,
            CancellationToken cancellationToken = default)
        {
            Keys.Add(key);
            return ValueTask.CompletedTask;
        }

        public ValueTask TextAsync(
            string text,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
