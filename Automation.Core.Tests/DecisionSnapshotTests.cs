using Automation.Core.Configuration;
using Automation.Core.DeveloperTools;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Kes.Tasks;
using Xunit;

namespace Automation.Core.Tests;

public sealed class DecisionSnapshotTests
{
    [Fact]
    public void CatalogContainsFirstBatchOfDecisionPoints()
    {
        var reasonIds = new[]
        {
            DecisionSnapshotCatalog.CenterSkipReasonId,
            DecisionSnapshotCatalog.CenterConfirmReasonId,
            DecisionSnapshotCatalog.ValidationCardPromptReasonId,
            DecisionSnapshotCatalog.DestinyChoiceReasonId,
            DecisionSnapshotCatalog.BattleSettlementReasonId,
            DecisionSnapshotCatalog.EventOptionsReasonId,
            DecisionSnapshotCatalog.CardRewardReasonId,
            DecisionSnapshotCatalog.CardAssignReasonId,
            DecisionSnapshotCatalog.EquipmentChoiceReasonId,
            DecisionSnapshotCatalog.FlashReasonId,
            DecisionSnapshotCatalog.ReflashReasonId,
            DecisionSnapshotCatalog.GrantFlashReasonId,
            DecisionSnapshotCatalog.ExpeditionResultReasonId,
            DecisionSnapshotCatalog.ShopReasonId,
            DecisionSnapshotCatalog.RestReasonId,
            DecisionSnapshotCatalog.RouteSelectionReasonId,
            DecisionSnapshotCatalog.FixedClickBlockedReasonId,
            DecisionSnapshotCatalog.FlashEffectNoCardsReasonId
        };

        foreach (var reasonId in reasonIds)
        {
            var definition = DecisionSnapshotCatalog.Resolve(reasonId);
            Assert.Equal(reasonId, definition.ReasonId);
            Assert.False(string.IsNullOrWhiteSpace(definition.PageLabel));
            Assert.False(string.IsNullOrWhiteSpace(definition.Why));
            Assert.NotEmpty(definition.SuggestedRegions);
        }
    }

    [Fact]
    public void CatalogRegionsStayInsideNormalizedFrame()
    {
        foreach (var definition in DecisionSnapshotCatalog.All)
        {
            foreach (var region in definition.SuggestedRegions)
            {
                Assert.InRange(region.Left, 0, 1);
                Assert.InRange(region.Top, 0, 1);
                Assert.InRange(region.Right, 0, 1);
                Assert.InRange(region.Bottom, 0, 1);
                Assert.True(region.Right > region.Left);
                Assert.True(region.Bottom > region.Top);
                Assert.False(string.IsNullOrWhiteSpace(region.Label));
                Assert.False(string.IsNullOrWhiteSpace(region.Why));
            }
        }
    }

    [Fact]
    public void MetadataBuilderConvertsNormalizedRegionToPixels()
    {
        var frame = CreateFrame(1600, 900);
        var definition = DecisionSnapshotCatalog.Resolve(
            DecisionSnapshotCatalog.CenterConfirmReasonId);
        var request = definition.CreateRequest(
            "WIN-01",
            42,
            DateTimeOffset.Parse("2026-10-13T10:20:30+08:00"));
        var metadata = DecisionSnapshotMetadataBuilder.Build(
            request,
            frame,
            new OcrFrameResult(
                "confirm",
                0.98,
                [new OcrTextLine("confirm", 0.98, new OcrTextRegion(700, 500, 120, 40))]));

        var region = Assert.Single(
            metadata.Regions,
            item => item.Id == "center-confirm-button");

        Assert.Equal(480, region.Pixels.X);
        Assert.Equal(468, region.Pixels.Y);
        Assert.Equal(640, region.Pixels.Width);
        Assert.Equal(252, region.Pixels.Height);
        Assert.Equal(42, metadata.FrameSequence);
        Assert.Equal(1, metadata.Ocr?.Lines.Count);
    }

    [Fact]
    public void JsonOutputIncludesReasonAndReviewStatus()
    {
        var frame = CreateFrame(1600, 900);
        var request = DecisionSnapshotCatalog
            .Resolve(DecisionSnapshotCatalog.ShopReasonId)
            .CreateRequest(
                "WIN-01",
                7,
                DateTimeOffset.Now,
                contextLogs: "2026-10-13 22:00:00.000 [warning] fixed-click-blocked");
        var metadata = DecisionSnapshotMetadataBuilder.Build(request, frame, null);

        var metadataJson = DecisionSnapshotJson.SerializeMetadata(metadata);
        var regionsJson = DecisionSnapshotJson.SerializeSuggestedRegions(metadata);

        Assert.Contains("\"reasonId\": \"shop\"", metadataJson, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"pending-human-review\"", metadataJson, StringComparison.Ordinal);
        Assert.Contains("\"contextLogs\": \"2026-10-13 22:00:00.000 [warning] fixed-click-blocked\"", metadataJson, StringComparison.Ordinal);
        Assert.Contains("\"regions\"", regionsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CenterConfirmDecisionPointSubmitsDeveloperSnapshot()
    {
        var frame = CreateFrame(1600, 900);
        var sink = new RecordingDecisionSnapshotSink();
        var context = new TaskContext(
            "WIN-01",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame, 55)),
            new RecordingInputSink(),
            taskConfig: new Dictionary<string, object>(),
            ocr: new StaticOcrProvider(
                new OcrFrameResult(
                    "confirm",
                    0.99,
                    [new OcrTextLine("确认", 0.99, new OcrTextRegion(700, 500, 120, 40))])),
            decisionSnapshots: sink);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var request = Assert.Single(sink.Requests);
        Assert.Equal(DecisionSnapshotCatalog.CenterConfirmReasonId, request.ReasonId);
        Assert.Equal(55, request.FrameSequence);
        Assert.Contains(
            request.CandidateActions,
            action => action.Label == "点击中心确认");
    }

    [Fact]
    public async Task DestinyChoiceSubmitsDeveloperSnapshot()
    {
        var frame = CreateFrame(1600, 900);
        var sink = new RecordingDecisionSnapshotSink();
        var context = new TaskContext(
            "WIN-01",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame, 77)),
            new RecordingInputSink(),
            taskConfig: new Dictionary<string, object>(),
            ocr: new StaticOcrProvider(
                new OcrFrameResult(
                    "destiny",
                    0.99,
                    [
                        new OcrTextLine("请选择你的命运", 0.99, new OcrTextRegion(730, 825, 140, 35)),
                        new OcrTextLine("命运一", 0.99, new OcrTextRegion(760, 460, 100, 30))
                    ])),
            decisionSnapshots: sink);
        var task = new ChaosModeTask();

        _ = await task.RunOnceAsync(context, CancellationToken.None);

        var request = Assert.Single(sink.Requests);
        Assert.Equal(DecisionSnapshotCatalog.DestinyChoiceReasonId, request.ReasonId);
        Assert.Equal(77, request.FrameSequence);
    }

    [Fact]
    public async Task SettlementSubmitsDeveloperSnapshot()
    {
        var frame = CreateFrame(1600, 900);
        var sink = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            frame,
            new OcrFrameResult(
                "结算",
                0.99,
                [new OcrTextLine("结算", 0.99, new OcrTextRegion(1450, 800, 110, 50))]),
            sink);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(
            sink.Requests,
            request => request.ReasonId == DecisionSnapshotCatalog.BattleSettlementReasonId);
    }

    [Fact]
    public async Task EventOptionsSubmitsDeveloperSnapshotBeforeForbiddenFiltering()
    {
        var frame = CreateFrame(1600, 900);
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["event1"] = [new FeatureBox("event1", 400, 850, 50, 20, 0.96)],
                ["forbidden_event"] = [new FeatureBox("forbidden_event", 250, 645, 30, 20, 0.99)]
            });
        var sink = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            frame,
            new OcrFrameResult(
                "测试事件",
                0.99,
                [new OcrTextLine("测试事件", 0.99, new OcrTextRegion(300, 700, 160, 40))]),
            sink,
            matcher);
        var task = new ChaosModeTask();

        _ = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.Single(
            sink.Requests,
            request => request.ReasonId == DecisionSnapshotCatalog.EventOptionsReasonId);
    }

    [Fact]
    public async Task CardRewardSubmitsDeveloperSnapshotBeforeMatcherCheck()
    {
        var frame = CreateFrame(1600, 900);
        var sink = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            frame,
            new OcrFrameResult(
                "卡牌奖励",
                0.99,
                [new OcrTextLine("卡牌奖励", 0.99, new OcrTextRegion(700, 25, 200, 30))]),
            sink);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(
            sink.Requests,
            request => request.ReasonId == DecisionSnapshotCatalog.CardRewardReasonId);
    }

    [Fact]
    public async Task CardAssignSubmitsDeveloperSnapshot()
    {
        var frame = CreateFrame(1600, 900);
        var sink = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            frame,
            new OcrFrameResult(
                "请选择要接受卡牌的主战员",
                0.99,
                [
                    new OcrTextLine(
                        "请选择要接受卡牌的主战员",
                        0.99,
                        new OcrTextRegion(1340, 105, 100, 30))
                ]),
            sink);
        var task = new ChaosModeTask();

        _ = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.Single(
            sink.Requests,
            request => request.ReasonId == DecisionSnapshotCatalog.CardAssignReasonId);
    }

    [Fact]
    public async Task EquipmentChoiceSubmitsDeveloperSnapshot()
    {
        var frame = CreateFrame(1600, 900);
        var sink = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            frame,
            new OcrFrameResult(
                "装备",
                0.99,
                [new OcrTextLine("装备", 0.99, new OcrTextRegion(760, 95, 80, 30))]),
            sink);
        var task = new ChaosModeTask();

        _ = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.Single(
            sink.Requests,
            request => request.ReasonId == DecisionSnapshotCatalog.EquipmentChoiceReasonId);
    }

    [Theory]
    [InlineData("闪光", DecisionSnapshotCatalog.FlashReasonId)]
    [InlineData("重新闪光", DecisionSnapshotCatalog.ReflashReasonId)]
    [InlineData("赋予闪光", DecisionSnapshotCatalog.GrantFlashReasonId)]
    public async Task FlashBranchesSubmitDeveloperSnapshot(string text, string reasonId)
    {
        var frame = CreateFrame(1600, 900);
        var sink = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            frame,
            new OcrFrameResult(
                text,
                0.99,
                [new OcrTextLine(text, 0.99, new OcrTextRegion(1450, 800, 110, 50))]),
            sink);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Single(sink.Requests, request => request.ReasonId == reasonId);
    }

    [Fact]
    public void TaskContextKeepsRecentLogsForDiagnostics()
    {
        var sink = new RecordingDecisionSnapshotSink();
        var context = CreateContext(
            CreateFrame(1600, 900),
            new OcrFrameResult("empty", 1, []),
            sink);

        context.LogInfo("第一行", "activity");
        context.LogInfo("第二行", "warning");

        Assert.Contains(context.GetRecentLogs(), line => line.Contains("第一行", StringComparison.Ordinal));
        Assert.Contains(context.GetRecentLogs(), line => line.Contains("第二行", StringComparison.Ordinal));
    }

    private static TaskContext CreateContext(
        BgraFrame frame,
        OcrFrameResult ocr,
        IDecisionSnapshotSink sink,
        IFeatureMatcher? featureMatcher = null)
        => new(
            "decision-snapshot-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame, 101)),
            new RecordingInputSink(),
            taskConfig: new Dictionary<string, object>(),
            ocr: new StaticOcrProvider(ocr),
            featureMatcher: featureMatcher,
            decisionSnapshots: sink);
    private static BgraFrame CreateFrame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
            pixels[index + 1] = 255;
            pixels[index + 2] = 255;
            pixels[index + 3] = 255;
        }

        return new BgraFrame(width, height, pixels);
    }

    private sealed class RecordingDecisionSnapshotSink : IDecisionSnapshotSink
    {
        public bool IsEnabled => true;
        public List<DecisionSnapshotRequest> Requests { get; } = [];

        public ValueTask<string?> CaptureAsync(
            DecisionSnapshotRequest request,
            BgraFrame frame,
            OcrFrameResult? ocr,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult<string?>(null);
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

    private sealed class TestBgraFrameLease(BgraFrame frame, long sequence) : FrameLease(
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
        IReadOnlyDictionary<string, IReadOnlyList<FeatureBox>> boxes) : IFeatureMatcher
    {
        public IReadOnlyList<FeatureBox> Find(
            BgraFrame frame,
            string featureName,
            FeatureSearchRegion? region = null,
            double? threshold = null)
            => boxes.TryGetValue(featureName, out var matches)
                ? matches
                : [];
    }

    private sealed class RecordingInputSink : IInputSink
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

        public ValueTask KeyAsync(
            InputKey key,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask TextAsync(
            string text,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}