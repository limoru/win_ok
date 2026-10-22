using System.Text.Json;
using Automation.Core.DeveloperTools;
using Automation.Core.Configuration;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Kes.Tasks;
using Kes.Tasks.Configuration;
using OpenCvSharp;
using Xunit;

namespace Automation.Core.Tests;

public sealed class FrameSampleArchiveTests
{
    [Fact]
    public async Task SavesCleanPngAndOcrJsonWithCoordinates()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new FrameSampleArchiveService(directory) { IsEnabled = true };
            var frame = CreateFrame(60, 20, 40);
            var request = CreateRequest(
                "flash-effect",
                frame,
                new OcrFrameResult(
                    "选择闪光",
                    0.97,
                    [new OcrTextLine("选择闪光", 0.91, new OcrTextRegion(12, 34, 56, 18))]));

            var result = await service.CaptureAsync(request);

            Assert.NotNull(result);
            Assert.Equal(FrameSampleArchiveOutcome.Saved, result.Outcome);
            Assert.True(File.Exists(result.ImagePath));
            Assert.True(File.Exists(result.JsonPath));
            Assert.Equal(1, result.SavedCount);
            Assert.Equal(20, result.Limit);

            using var image = Cv2.ImRead(result.ImagePath!, ImreadModes.Color);
            Assert.False(image.Empty());
            Assert.Equal(frame.Width, image.Cols);
            Assert.Equal(frame.Height, image.Rows);
            var pixel = image.At<Vec3b>(0, 0);
            Assert.Equal(20, pixel.Item0);
            Assert.Equal(40, pixel.Item1);
            Assert.Equal(60, pixel.Item2);

            using var json = JsonDocument.Parse(File.ReadAllText(result.JsonPath!));
            var root = json.RootElement;
            Assert.Equal("WIN-01", root.GetProperty("sessionId").GetString());
            Assert.Equal("run-1", root.GetProperty("runId").GetString());
            Assert.Equal("flash-effect", root.GetProperty("consumerId").GetString());
            Assert.Equal(77, root.GetProperty("frameSequence").GetInt64());
            Assert.Equal(frame.Width, root.GetProperty("frameWidth").GetInt32());
            Assert.Equal(frame.Height, root.GetProperty("frameHeight").GetInt32());
            var ocrLine = root.GetProperty("ocr").GetProperty("lines")[0];
            Assert.Equal("选择闪光", ocrLine.GetProperty("text").GetString());
            Assert.Equal(0.91, ocrLine.GetProperty("score").GetDouble(), 3);
            Assert.Equal(12, ocrLine.GetProperty("x").GetInt32());
            Assert.Equal(34, ocrLine.GetProperty("y").GetInt32());
            Assert.Equal(56, ocrLine.GetProperty("width").GetInt32());
            Assert.Equal(18, ocrLine.GetProperty("height").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SameRunAndConsumerCapturesEachUniqueImage()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new FrameSampleArchiveService(directory) { IsEnabled = true };

            var first = await service.CaptureAsync(
                CreateRequest("equipment", CreateFrame(60, 20, 40), EmptyOcr(), "run-1"));
            var second = await service.CaptureAsync(
                CreateRequest("equipment", CreateFrame(61, 21, 41), EmptyOcr(), "run-1"));

            Assert.Equal(FrameSampleArchiveOutcome.Saved, first!.Outcome);
            Assert.Equal(FrameSampleArchiveOutcome.Saved, second!.Outcome);
            Assert.Equal(1, first.SavedCount);
            Assert.Equal(2, second.SavedCount);
            var consumerDirectory = Path.Combine(directory, "equipment");
            Assert.Equal(2, Directory.EnumerateFiles(consumerDirectory, "*.png").Count());
            Assert.Equal(2, Directory.EnumerateFiles(consumerDirectory, "*.json").Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateImageAcrossRunsIsNotSavedAgain()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new FrameSampleArchiveService(directory) { IsEnabled = true };
            var frame = CreateFrame(60, 20, 40);

            var first = await service.CaptureAsync(
                CreateRequest("flash", frame, EmptyOcr(), "run-1"));
            var second = await service.CaptureAsync(
                CreateRequest("flash", frame, EmptyOcr(), "run-2"));

            Assert.Equal(FrameSampleArchiveOutcome.Saved, first!.Outcome);
            Assert.Equal(FrameSampleArchiveOutcome.Duplicate, second!.Outcome);
            Assert.Equal(1, second.SavedCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task IncompletePngJsonPairDoesNotConsumeTheLimit()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new FrameSampleArchiveService(directory, limitPerConsumer: 1)
            {
                IsEnabled = true
            };
            var consumerDirectory = Path.Combine(directory, "event-task");
            Directory.CreateDirectory(consumerDirectory);
            await File.WriteAllBytesAsync(Path.Combine(consumerDirectory, "orphan.png"), [1, 2, 3]);

            var result = await service.CaptureAsync(
                CreateRequest("event-task", CreateFrame(80), EmptyOcr(), "run-1"));

            Assert.Equal(FrameSampleArchiveOutcome.Saved, result!.Outcome);
            Assert.Equal(1, result.SavedCount);
            Assert.Equal(2, Directory.EnumerateFiles(consumerDirectory, "*.png").Count());
            Assert.Single(Directory.EnumerateFiles(consumerDirectory, "*.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StopsAfterTwentyUniqueImagesPerConsumer()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new FrameSampleArchiveService(directory) { IsEnabled = true };
            for (var index = 0; index < 20; index++)
            {
                var result = await service.CaptureAsync(
                    CreateRequest("battle-auto-check", CreateFrame((byte)index, 2, 3), EmptyOcr(), "run-1"));
                Assert.Equal(FrameSampleArchiveOutcome.Saved, result!.Outcome);
            }

            var rejected = await service.CaptureAsync(
                CreateRequest("battle-auto-check", CreateFrame(200, 20, 30), EmptyOcr(), "run-21"));

            Assert.Equal(FrameSampleArchiveOutcome.LimitReached, rejected!.Outcome);
            Assert.Equal(20, rejected.SavedCount);
            var consumerDirectory = Path.Combine(directory, "battle-auto-check");
            Assert.Equal(20, Directory.EnumerateFiles(consumerDirectory, "*.png").Count());
            Assert.Equal(20, Directory.EnumerateFiles(consumerDirectory, "*.json").Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FrameBusArchivesOnlyPublishedFrames()
    {
        var sink = new RecordingFrameSampleSink();
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var recognized = new OcrFrameResult(
            "点击屏幕",
            0.99,
            [new OcrTextLine("点击屏幕", 0.99, new OcrTextRegion(700, 430, 200, 40))]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "frame-sample-bus-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            new NoOpInputSink(),
            ocr: new StaticOcrProvider(recognized),
            frameSamples: sink);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));

        Assert.Single(sink.Requests);
        Assert.Equal("close-page", sink.Requests[0].ConsumerId);
        Assert.Equal(context.RunId, sink.Requests[0].RunId);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task RejectedBackgroundEnqueueIsRetriedOnTheNextFramePoll()
    {
        var sink = new RejectThenAcceptBackgroundSink();
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var recognized = new OcrFrameResult(
            "关闭",
            0.99,
            [new OcrTextLine("关闭", 0.99, new OcrTextRegion(790, 820, 80, 30))]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "frame-sample-retry-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new AdvancingFrameSource(frame),
            new NoOpInputSink(),
            taskConfig: new Dictionary<string, object>
            {
                [ChaosModeConfigSchema.RecognitionFallbackWaitRoundsKey] = 1
            },
            ocr: new StaticOcrProvider(recognized),
            frameSamples: sink);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));
        Assert.True(await task.RunOnceAsync(context, cancellation.Token));

        Assert.Equal(2, sink.AttemptCount);
        var request = Assert.Single(sink.Requests);
        Assert.Equal(2, request.FrameSequence);
        Assert.False(string.IsNullOrWhiteSpace(request.ConsumerId));
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task NoConsumerFramesAreArchivedUnderDedicatedSampleCategory()
    {
        var sink = new RecordingFrameSampleSink();
        var frame = new BgraFrame(1600, 900, new byte[1600 * 900 * 4]);
        var recognized = new OcrFrameResult(
            "未知页面",
            0.99,
            [new OcrTextLine("未知页面", 0.99, new OcrTextRegion(700, 400, 200, 40))]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new TaskContext(
            "no-consumer-sample-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            new StaticFrameSource(new TestBgraFrameLease(frame)),
            new NoOpInputSink(),
            ocr: new StaticOcrProvider(recognized),
            frameSamples: sink);
        var task = new ChaosModeTask(useFrameDispatchBus: true);

        Assert.True(await task.RunOnceAsync(context, cancellation.Token));

        var request = Assert.Single(sink.Requests);
        Assert.Equal("no-consumer", request.ConsumerId);
        Assert.Equal(context.RunId, request.RunId);
        await cancellation.CancelAsync();
    }

    private static FrameSampleRequest CreateRequest(
        string consumerId,
        BgraFrame frame,
        OcrFrameResult ocr,
        string runId = "run-1")
        => new(
            "WIN-01",
            runId,
            consumerId,
            77,
            DateTimeOffset.Parse("2026-10-14T12:34:56.789+08:00"),
            frame,
            ocr,
            "ocr-signature");

    private static OcrFrameResult EmptyOcr() => new(string.Empty, 1, []);

    private static BgraFrame CreateFrame(byte seed, byte blue = 20, byte green = 40)
    {
        var pixels = new byte[4 * 4 * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = seed;
            pixels[offset + 3] = 255;
        }

        return new BgraFrame(4, 4, pixels);
    }

    private static string CreateTempDirectory()
        => Path.Combine(
            Path.GetTempPath(),
            "KesFrameSampleTests",
            Guid.NewGuid().ToString("N"));

    private sealed class RejectThenAcceptBackgroundSink : IBackgroundFrameSampleArchiveSink
    {
        private int _attemptCount;

        public bool IsEnabled => true;
        public int AttemptCount => Volatile.Read(ref _attemptCount);
        public List<FrameSampleRequest> Requests { get; } = [];

        public bool TryEnqueue(FrameSampleRequest request)
        {
            if (Interlocked.Increment(ref _attemptCount) == 1)
            {
                return false;
            }

            Requests.Add(request);
            return true;
        }

        public ValueTask<FrameSampleArchiveResult?> CaptureAsync(
            FrameSampleRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<FrameSampleArchiveResult?>(null);
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

    private sealed class AdvancingFrameSource(BgraFrame frame) : IFrameSource
    {
        private long _sequence;

        public ValueTask<FrameLease> GetLatestAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<FrameLease>(NextFrame());

        public ValueTask<FrameLease> WaitNextAsync(
            long afterSequence,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<FrameLease>(NextFrame());

        private TestBgraFrameLease NextFrame()
            => new(frame, Interlocked.Increment(ref _sequence));
    }

    private sealed class StaticOcrProvider(OcrFrameResult result) : IOcrProvider
    {
        public OcrFrameResult Recognize(BgraFrame frame) => result;
    }

    private sealed class NoOpInputSink : IInputSink
    {
        public ValueTask ClickAsync(RelativePoint point, CancellationToken cancellationToken = default)
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

    private sealed class TestBgraFrameLease(BgraFrame frame, long sequence = 1) : FrameLease(
        sequence,
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