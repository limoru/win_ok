using System.Runtime.InteropServices;
using Automation.Core.Configuration;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Automation.Vision.Onnx;
using Kes.Tasks;
using OpenCvSharp;
using Xunit;

namespace Automation.Core.Tests;

public sealed class ChaosModeTaskScreenshotFrameBusTests
{
    [Fact]
    public async Task FlashEffectSelectionScreenshotChoosesRecognizedPriorityCard()
    {
        var screenshotPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "FrameBus",
            "flash-effect-selection.png");
        Assert.True(File.Exists(screenshotPath), screenshotPath);

        var frame = LoadBgraFrame(screenshotPath);
        Assert.True(
            PpOcrEngine.TryCreateDefault(out var ocrEngine, out var ocrStatus),
            ocrStatus);
        using var ocr = ocrEngine!;
        var recognized = ocr.Recognize(frame);
        var catalog = FeatureTemplateCatalog.Load(ChaosFeatureAssets.GetDefaultPath());
        var matcher = new TemplateFeatureMatcher(catalog);
        var cards = ChaosCardRecognition.Recognize(frame, recognized, matcher);
        Assert.NotEmpty(cards);

        var expectedCard = cards[0];
        var input = new RecordingInputSink();
        var logs = new RecordingLogSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "screenshot-frame-bus-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            logSink: logs,
            taskConfig: new Dictionary<string, object>
            {
                ["闪光优先级"] = new[] { expectedCard.Name }
            },
            ocr: new StaticOcrProvider(recognized),
            featureMatcher: matcher);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        var handled = await task
            .RunOnceAsync(context, cancellation.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(handled);

        await input.Clicked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, expectedCard.X - 0.002, expectedCard.X + 0.002);
        Assert.InRange(click.Y, expectedCard.Y - 0.002, expectedCard.Y + 0.002);
        Assert.Contains(logs.Messages, message =>
            message.Contains($"闪光优先选择「{expectedCard.Name}」", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message =>
            message.Contains("随机选择", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task RemoveCardSelectionScreenshotSelectsCardBeforeRemoveButton()
    {
        var screenshotPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "FrameBus",
            "remove-card-selection.png");
        Assert.True(File.Exists(screenshotPath), screenshotPath);

        var frame = LoadBgraFrame(screenshotPath);
        Assert.True(
            PpOcrEngine.TryCreateDefault(out var ocrEngine, out var ocrStatus),
            ocrStatus);
        using var ocr = ocrEngine!;
        var recognized = ocr.Recognize(frame);
        var catalog = FeatureTemplateCatalog.Load(ChaosFeatureAssets.GetDefaultPath());
        var matcher = new TemplateFeatureMatcher(catalog);
        var cards = ChaosCardRecognition.RecognizeDeck(frame, recognized, matcher);
        Assert.NotEmpty(cards);

        var input = new RecordingInputSink();
        var logs = new RecordingLogSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "remove-card-selection-frame-bus-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            logSink: logs,
            taskConfig: new Dictionary<string, object>
            {
                ["移除卡牌列表"] = Array.Empty<string>(),
                ["优先移除基础牌"] = true
            },
            ocr: new StaticOcrProvider(recognized),
            featureMatcher: matcher);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        var handled = await task
            .RunOnceAsync(context, cancellation.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(handled);

        await input.Clicked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var click = Assert.Single(input.Clicks);
        Assert.Contains(cards, card =>
            Math.Abs(click.X - card.X) <= 0.003
            && Math.Abs(click.Y - card.Y) <= 0.003);
        Assert.True(click.Y < 0.82, $"移除选牌页不应先点击底部移除按钮，实际点击 ({click.X}, {click.Y})。");
        Assert.Equal(1, task.RunState.PendingRemovedCardCount);
        Assert.Equal(0, task.RunState.RemovedCardCount);
        Assert.Contains(logs.Messages, message =>
            message.Contains("检测到卡牌移除选择", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message =>
            message.Contains("检测到移除操作", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task RemoveCardSelectionScreenshotClicksRemoveAfterSelectionPending()
    {
        var screenshotPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "FrameBus",
            "remove-card-selection-after-card-selected.png");
        Assert.True(File.Exists(screenshotPath), screenshotPath);

        var frame = LoadBgraFrame(screenshotPath);
        Assert.True(
            PpOcrEngine.TryCreateDefault(out var ocrEngine, out var ocrStatus),
            ocrStatus);
        using var ocr = ocrEngine!;
        var recognized = ocr.Recognize(frame);
        var catalog = FeatureTemplateCatalog.Load(ChaosFeatureAssets.GetDefaultPath());
        var matcher = new TemplateFeatureMatcher(catalog);
        var input = new RecordingInputSink();
        var logs = new RecordingLogSink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "remove-card-selection-confirm-frame-bus-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            input,
            logSink: logs,
            taskConfig: new Dictionary<string, object>
            {
                ["移除卡牌列表"] = Array.Empty<string>(),
                ["优先移除基础牌"] = true
            },
            ocr: new StaticOcrProvider(recognized),
            featureMatcher: matcher);
        var task = new ChaosModeTask(useFrameDispatchBus: true);
        task.RunState.PendingRemovedCardCount = 1;

        var handled = await task
            .RunOnceAsync(context, cancellation.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(handled);

        await input.Clicked.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var click = Assert.Single(input.Clicks);
        Assert.InRange(click.X, 0.50, 0.99);
        Assert.InRange(click.Y, 0.82, 0.99);
        Assert.Equal(0, task.RunState.PendingRemovedCardCount);
        Assert.Equal(1, task.RunState.RemovedCardCount);
        Assert.Contains(logs.Messages, message =>
            message.Contains("检测到移除操作", StringComparison.Ordinal)
            || message.Contains("检测到三选一卡牌移除页面", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message =>
            message.Contains("检测到卡牌移除选择", StringComparison.Ordinal));
        await cancellation.CancelAsync();
    }

    private static BgraFrame LoadBgraFrame(string path)
    {
        using var color = Cv2.ImRead(path, ImreadModes.Color);
        Assert.False(color.Empty());
        using var bgra = new Mat();
        Cv2.CvtColor(color, bgra, ColorConversionCodes.BGR2BGRA);
        Assert.True(bgra.IsContinuous());

        var pixels = new byte[checked(bgra.Rows * bgra.Cols * 4)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new BgraFrame(bgra.Cols, bgra.Rows, pixels);
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

    private sealed class StaticOcrProvider(OcrFrameResult result) : IOcrProvider
    {
        public OcrFrameResult Recognize(BgraFrame frame) => result;
    }

    private sealed class RecordingLogSink : ITaskLogSink
    {
        public List<string> Messages { get; } = [];

        public void Log(string sessionId, string message, string? tag = null)
            => Messages.Add(message);
    }

    private sealed class RecordingInputSink : IInputSink
    {
        public TaskCompletionSource<bool> Clicked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<RelativePoint> Clicks { get; } = [];

        public ValueTask ClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
        {
            Clicks.Add(point);
            Clicked.TrySetResult(true);
            return ValueTask.CompletedTask;
        }

        public ValueTask HoverClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
            => ClickAsync(point, cancellationToken);

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
}
