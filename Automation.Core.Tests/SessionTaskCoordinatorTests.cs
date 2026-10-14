using Automation.Core.Configuration;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Targeting;
using Xunit;

namespace Automation.Core.Tests;

public sealed class SessionTaskCoordinatorTests
{
    [Fact]
    public async Task RunOnceStopsAfterFirstHandledModule()
    {
        var first = new RecordingModule("first", true);
        var second = new RecordingModule("second", true);
        var context = CreateContext();
        using var coordinator = new SessionTaskCoordinator(
            [first, second],
            context,
            TimeSpan.FromMilliseconds(10));

        var handled = await coordinator.RunOnceAsync();

        Assert.True(handled);
        Assert.Equal(1, first.RunCount);
        Assert.Equal(0, second.RunCount);
    }

    [Fact]
    public async Task RunOnceReportsWaitingOnlyAfterConsecutiveUnhandledFrames()
    {
        var reporter = new RecordingActivityReporter();
        var context = CreateContext(reporter);
        using var coordinator = new SessionTaskCoordinator(
            [new RecordingModule("idle", false)],
            context,
            TimeSpan.FromMilliseconds(10));

        for (var index = 0; index < 4; index++)
        {
            Assert.False(await coordinator.RunOnceAsync());
            Assert.Null(reporter.LastActivity);
        }

        Assert.False(await coordinator.RunOnceAsync());
        Assert.Equal("no-handler", reporter.LastActivity?.StepId);
        Assert.Equal(SessionActivityKind.Waiting, reporter.LastActivity?.Kind);
    }

    [Fact]
    public async Task RunOnceReportsRunningAgainAfterWaitingRecovers()
    {
        var reporter = new RecordingActivityReporter();
        var context = CreateContext(reporter);
        var module = new RecordingModule("recover", false);
        using var coordinator = new SessionTaskCoordinator(
            [module],
            context,
            TimeSpan.FromMilliseconds(10));

        for (var index = 0; index < 5; index++)
        {
            await coordinator.RunOnceAsync();
        }

        module.Handled = true;
        Assert.True(await coordinator.RunOnceAsync());

        Assert.Equal("handled", reporter.LastActivity?.StepId);
        Assert.Equal(SessionActivityKind.Running, reporter.LastActivity?.Kind);
        Assert.Equal("recover", reporter.LastActivity?.TaskId);
    }

    [Fact]
    public async Task PausePreventsExecutionUntilResume()
    {
        var module = new RecordingModule("count", true);
        var context = CreateContext();
        using var coordinator = new SessionTaskCoordinator(
            [module],
            context,
            TimeSpan.FromMilliseconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        coordinator.Pause();
        var runTask = coordinator.RunAsync(cancellation.Token);
        await Task.Delay(60);
        Assert.Equal(0, module.RunCount);

        coordinator.Resume();
        await WaitUntilAsync(() => Volatile.Read(ref module.RunCount) > 0);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task TaskContextReturnsLatestFrame()
    {
        var frame = new TestFrameLease(7, 320, 200);
        var frames = new TestFrameSource(frame);
        var context = CreateContext(frames: frames);

        using var result = await context.GetLatestFrameAsync();

        Assert.Equal(7, result.Sequence);
        Assert.Equal(320, result.Width);
        Assert.Equal(200, result.Height);
    }

    private static TaskContext CreateContext(
        IActivityReporter? activity = null,
        IFrameSource? frames = null)
        => new(
            "test-session",
            UiProfile.WindowsPc,
            TargetCapabilities.BackgroundCapture,
            frames ?? new TestFrameSource(new TestFrameLease(1, 100, 100)),
            new NoOpInputSink(),
            activity);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not reached.");
    }

    private sealed class RecordingModule(string moduleId, bool handled) : ITaskModule
    {
        public string ModuleId { get; } = moduleId;
        public string DisplayName => ModuleId;
        public int RunCount;
        public bool Handled { get; set; } = handled;

        public ValueTask<bool> RunOnceAsync(
            TaskContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RunCount);
            return ValueTask.FromResult(Handled);
        }
    }

    private sealed class RecordingActivityReporter : IActivityReporter
    {
        public SessionActivity? LastActivity { get; private set; }

        public void Report(SessionActivity activity)
            => LastActivity = activity;
    }

    private sealed class TestFrameSource(FrameLease latest) : IFrameSource
    {
        public ValueTask<FrameLease> GetLatestAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(latest);

        public ValueTask<FrameLease> WaitNextAsync(
            long afterSequence,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(latest);
    }

    private sealed class TestFrameLease(long sequence, int width, int height) : FrameLease(
        sequence,
        DateTimeOffset.Now,
        width,
        height,
        "BGRA32")
    {
        public override void Dispose()
        {
        }
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
}