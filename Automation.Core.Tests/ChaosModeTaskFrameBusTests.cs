using Automation.Core.Configuration;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Kes.Tasks;
using Xunit;

namespace Automation.Core.Tests;

public sealed class ChaosModeTaskFrameBusTests
{
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
