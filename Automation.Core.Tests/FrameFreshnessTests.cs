using Automation.Core.Configuration;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Xunit;

namespace Automation.Core.Tests;

public sealed class FrameFreshnessTests
{
    [Fact]
    public async Task NextFrameAfterInputDiscardsFramesCapturedBeforeClickCompletion()
    {
        var frames = new QueuedFrameSource();
        var input = new RecordingInputSink();
        var context = new TaskContext(
            "freshness-test",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture | TargetCapabilities.BackgroundInput,
            frames,
            input);

        await context.ClickRelativeAsync(new RelativePoint(0.5, 0.5));

        frames.Enqueue(new TestFrameLease(
            1,
            input.ClickCompletedAt.AddMilliseconds(-100)));
        frames.Enqueue(new TestFrameLease(
            2,
            input.ClickCompletedAt.AddMilliseconds(100)));

        using var frame = await context.GetNextFrameAsync(
            0,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(2, frame.Sequence);
        Assert.True(frames.Frames[0].IsDisposed);
        Assert.False(frames.Frames[1].IsDisposed);
    }

    private sealed class QueuedFrameSource : IFrameSource
    {
        private readonly Queue<FrameLease> _pending = new();

        public List<TestFrameLease> Frames { get; } = [];

        public void Enqueue(TestFrameLease frame)
        {
            _pending.Enqueue(frame);
            Frames.Add(frame);
        }

        public ValueTask<FrameLease> GetLatestAsync(CancellationToken cancellationToken = default)
            => _pending.Count > 0
                ? ValueTask.FromResult(_pending.Peek())
                : throw new InvalidOperationException("No frame is available.");

        public ValueTask<FrameLease> WaitNextAsync(
            long afterSequence,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            while (_pending.Count > 0)
            {
                var frame = _pending.Dequeue();
                if (frame.Sequence > afterSequence)
                {
                    return ValueTask.FromResult<FrameLease>(frame);
                }
            }

            throw new TimeoutException("No queued frame is available.");
        }
    }

    private sealed class RecordingInputSink : IInputSink
    {
        public DateTimeOffset ClickCompletedAt { get; private set; }

        public ValueTask ClickAsync(
            RelativePoint point,
            CancellationToken cancellationToken = default)
        {
            ClickCompletedAt = DateTimeOffset.Now;
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

    private sealed class TestFrameLease(
        long sequence,
        DateTimeOffset capturedAt) : FrameLease(
        sequence,
        capturedAt,
        1,
        1,
        "BGRA32")
    {
        public bool IsDisposed { get; private set; }

        public override void Dispose()
            => IsDisposed = true;
    }
}