using Automation.Core.Configuration;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Kes.Tasks;
using Kes.Tasks.Configuration;
using Xunit;

namespace Automation.Core.Tests;

public sealed class ChaosSpecialPageMigrationTests
{
    [Theory]
    [InlineData("故事", 0.887, 0.160)]
    [InlineData("营救", 0.101, 0.046)]
    [InlineData("方舟城市", 0.124, 0.049)]
    public async Task NonBattlePagesRequestSessionStop(
        string pageName,
        double x,
        double y)
    {
        var input = new RecordingInputSink();
        var stopRequests = new RecordingStopRequestSink();
        var context = CreateContext(
            CreateFrame(),
            CreateOcr(LineAt(pageName, x, y)),
            input,
            stopRequestSink: stopRequests);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.Empty(input.Clicks);
        var request = Assert.Single(stopRequests.Requests);
        Assert.Contains(pageName, request.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonBattleDetectionDoesNotStopOrdinaryPage()
    {
        var input = new RecordingInputSink();
        var stopRequests = new RecordingStopRequestSink();
        var context = CreateContext(
            CreateFrame(),
            CreateOcr(LineAt("普通页面", 0.887, 0.160)),
            input,
            stopRequestSink: stopRequests);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(stopRequests.Requests);
        Assert.Empty(input.Clicks);
    }

    [Fact]
    public async Task BattleAutoCheckClicksStartAttackFeature()
    {
        var input = new RecordingInputSink();
        var matcher = new StaticFeatureMatcher(
            new Dictionary<string, IReadOnlyList<FeatureBox>>
            {
                ["start_attack_event"] = [new FeatureBox("start_attack_event", 700, 720, 80, 30, 0.98)]
            });
        var context = CreateContext(
            CreateFrame(),
            CreateOcr(LineAt("3/10", 0.512, 0.969)),
            input,
            matcher);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.462, 0.463);
        Assert.InRange(click.Y, 0.816, 0.817);
        Assert.False(task.RunState.FinalBossBattle);
    }

    [Fact]
    public async Task BattleAutoCheckClicksDisabledAutoControlWhenWhitePixelPresent()
    {
        var frame = CreateFrame();
        SetPixelWhite(frame, (int)(0.877 * frame.Width), (int)(0.050 * frame.Height));
        var input = new RecordingInputSink();
        var context = CreateContext(
            frame,
            CreateOcr(LineAt("5/10", 0.512, 0.969)),
            input);
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.879, 0.881);
        Assert.InRange(click.Y, 0.055, 0.057);
    }

    [Fact]
    public async Task BattleAutoCheckMarksFinalBossBattle()
    {
        var input = new RecordingInputSink();
        var context = CreateContext(
            CreateFrame(),
            CreateOcr(LineAt("7/10", 0.512, 0.969)),
            input);
        var task = new ChaosModeTask();
        task.RunState.ReachFinalBoss = true;

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        Assert.True(task.RunState.FinalBossBattle);
        Assert.Empty(input.Clicks);
    }

    [Fact]
    public async Task DiscoverySelectClicksFirstOptionMeetingThreshold()
    {
        var input = new RecordingInputSink();
        var context = CreateContext(
            CreateFrame(),
            CreateOcr(
                LineAt("获得法典", 0.500, 0.100),
                LineAt("存储数据保存上限100", 0.1865, 0.579),
                LineAt("存储数据保存上限130", 0.4995, 0.576),
                LineAt("存储数据保存上限160", 0.811, 0.576)),
            input,
            config: new Dictionary<string, object>
            {
                ["存储数据保存上限大于多少"] = 120
            });
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.499, 0.500);
        Assert.InRange(click.Y, 0.575, 0.577);
    }

    [Fact]
    public async Task DiscoverySelectCancelsWhenNoOptionMeetsThreshold()
    {
        var input = new RecordingInputSink();
        var context = CreateContext(
            CreateFrame(),
            CreateOcr(
                LineAt("获得法典", 0.500, 0.100),
                LineAt("存储数据保存上限100", 0.1865, 0.579),
                LineAt("存储数据保存上限120", 0.4995, 0.576),
                LineAt("存储数据保存上限140", 0.811, 0.576)),
            input,
            config: new Dictionary<string, object>
            {
                ["存储数据保存上限大于多少"] = 150
            });
        var task = new ChaosModeTask();

        var handled = await task.RunOnceAsync(context, CancellationToken.None);

        Assert.True(handled);
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.960, 0.962);
        Assert.InRange(click.Y, 0.049, 0.051);
    }

    private static TaskContext CreateContext(
        BgraFrame frame,
        OcrFrameResult ocr,
        IInputSink input,
        IFeatureMatcher? featureMatcher = null,
        IReadOnlyDictionary<string, object>? config = null,
        ITaskStopRequestSink? stopRequestSink = null)
        => new(
            "special-page-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            taskConfig: config,
            ocr: new StaticOcrProvider(ocr),
            taskConfigSchema: ChaosModeConfigSchema.Instance,
            featureMatcher: featureMatcher ?? new StaticFeatureMatcher(
                new Dictionary<string, IReadOnlyList<FeatureBox>>()),
            stopRequestSink: stopRequestSink);

    private static OcrFrameResult CreateOcr(params OcrTextLine[] lines)
        => new(string.Join(" ", lines.Select(line => line.Text)), 0.99, lines);

    private static OcrTextLine LineAt(
        string text,
        double relativeX,
        double relativeY,
        int width = 160,
        int height = 30,
        int frameWidth = 1600,
        int frameHeight = 900)
        => new(
            text,
            0.99,
            new OcrTextRegion(
                (int)Math.Round((relativeX * frameWidth) - (width / 2d)),
                (int)Math.Round((relativeY * frameHeight) - (height / 2d)),
                width,
                height));

    private static BgraFrame CreateFrame(int width = 1600, int height = 900)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index + 3] = 255;
        }

        return new BgraFrame(width, height, pixels);
    }

    private static void SetPixelWhite(BgraFrame frame, int x, int y)
    {
        var offset = (y * frame.Stride) + (x * 4);
        frame.Pixels[offset] = 255;
        frame.Pixels[offset + 1] = 255;
        frame.Pixels[offset + 2] = 255;
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

    private sealed class TestBgraFrameLease(BgraFrame frame) : FrameLease(
        1,
        DateTimeOffset.Now,
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
        public List<RelativePoint> Clicks { get; } = [];

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

        public ValueTask KeyAsync(
            InputKey key,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask TextAsync(
            string text,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class RecordingStopRequestSink : ITaskStopRequestSink
    {
        public List<(string SessionId, string Reason)> Requests { get; } = [];

        public void RequestStop(string sessionId, string reason)
            => Requests.Add((sessionId, reason));
    }
}