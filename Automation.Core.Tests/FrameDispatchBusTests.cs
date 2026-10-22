using System.Collections.Concurrent;
using Automation.Core.Dispatching;
using Xunit;

namespace Automation.Core.Tests;

public sealed class FrameDispatchBusTests
{
    [Fact]
    public async Task PublishingDoesNotWaitForConsumer()
    {
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new ConcurrentQueue<long>();

        await using var bus = CreateBus(
            new FrameConsumer<int>("flash", async (delivery, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                processed.Enqueue(delivery.FrameSequence);
            }),
            capacity: 2);

        Assert.True(bus.TryPublish(Delivery(1, "flash", "same-text")).Published);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(bus.TryPublish(Delivery(2, "flash", "changed-text")).Published);
        Assert.False(bus.IsCompleted);

        release.TrySetResult(true);
        await WaitUntilAsync(() => processed.Count == 2);

        Assert.Equal([1L, 2L], processed.ToArray());
    }

    [Fact]
    public async Task IdenticalOcrTextIsDeliveredEveryTime()
    {
        var processed = new ConcurrentQueue<long>();
        await using var bus = CreateBus(
            new FrameConsumer<int>("flash", (delivery, _) =>
            {
                processed.Enqueue(delivery.FrameSequence);
                return ValueTask.CompletedTask;
            }),
            capacity: 4);

        Assert.Equal(
            FramePublishKind.Published,
            bus.TryPublish(Delivery(1, "flash", "same-text")).Kind);
        Assert.Equal(
            FramePublishKind.Published,
            bus.TryPublish(Delivery(2, "flash", "same-text")).Kind);

        await WaitUntilAsync(() => processed.Count == 2);
        Assert.Equal([1L, 2L], processed.ToArray());
    }

    [Fact]
    public async Task ChangedTextDispatchesImmediately()
    {
        await using var bus = CreateBus(
            new FrameConsumer<int>("flash", (_, _) => ValueTask.CompletedTask),
            capacity: 4);

        Assert.Equal(
            FramePublishKind.Published,
            bus.TryPublish(Delivery(1, "flash", "old-text")).Kind);
        Assert.Equal(
            FramePublishKind.Published,
            bus.TryPublish(Delivery(2, "flash", "new-text")).Kind);
    }

    [Fact]
    public async Task FullQueueReportsOverflowWithoutSilentlyDroppingExistingFrames()
    {
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new ConcurrentQueue<long>();
        var errors = new ConcurrentQueue<FrameDispatchError>();

        await using var bus = CreateBus(
            new FrameConsumer<int>("flash", async (delivery, cancellationToken) =>
            {
                if (delivery.FrameSequence == 1)
                {
                    entered.TrySetResult(true);
                    await release.Task.WaitAsync(cancellationToken);
                }

                processed.Enqueue(delivery.FrameSequence);
            }),
            capacity: 1,
            errorSink: errors.Enqueue);

        Assert.True(bus.TryPublish(Delivery(1, "flash", "one")).Published);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(bus.TryPublish(Delivery(2, "flash", "two")).Published);
        Assert.Equal(
            FramePublishKind.QueueOverflow,
            bus.TryPublish(Delivery(3, "flash", "three")).Kind);

        release.TrySetResult(true);
        await WaitUntilAsync(() => processed.Count == 2);

        Assert.Equal([1L, 2L], processed.ToArray());
        Assert.Contains(errors, error => error.Kind == FrameDispatchErrorKind.QueueOverflow);
    }

    [Fact]
    public async Task OneHungConsumerDoesNotBlockAnotherConsumer()
    {
        var hungEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHung = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var restProcessed = new ConcurrentQueue<long>();

        await using var bus = CreateBus(
            new FrameConsumer<int>("flash", async (_, cancellationToken) =>
            {
                hungEntered.TrySetResult(true);
                await releaseHung.Task.WaitAsync(cancellationToken);
            }),
            new FrameConsumer<int>("rest", (delivery, _) =>
            {
                restProcessed.Enqueue(delivery.FrameSequence);
                return ValueTask.CompletedTask;
            }),
            capacity: 2);

        Assert.True(bus.TryPublish(Delivery(1, "flash", "flash-page")).Published);
        await hungEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(bus.TryPublish(Delivery(2, "rest", "rest-page")).Published);

        await WaitUntilAsync(() => restProcessed.Count == 1);
        Assert.Equal(2L, Assert.Single(restProcessed));

        releaseHung.TrySetResult(true);
    }

    [Fact]
    public async Task TimedOutConsumerIsReplacedForLaterFrames()
    {
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new ConcurrentQueue<long>();
        var errors = new ConcurrentQueue<FrameDispatchError>();
        var callCount = 0;

        await using var bus = CreateBus(
            new FrameConsumer<int>("flash", async (delivery, cancellationToken) =>
            {
                Interlocked.Increment(ref callCount);
                if (delivery.FrameSequence == 1)
                {
                    entered.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }

                    return;
                }

                processed.Enqueue(delivery.FrameSequence);
            }),
            capacity: 2,
            consumerTimeout: TimeSpan.FromMilliseconds(50),
            errorSink: errors.Enqueue);

        Assert.True(bus.TryPublish(Delivery(1, "flash", "stuck")).Published);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => errors.Any(error =>
            error.Kind == FrameDispatchErrorKind.ConsumerTimeout));

        var replacement = bus.TryPublish(Delivery(2, "flash", "after-timeout"));
        Assert.Equal(FramePublishKind.Published, replacement.Kind);
        await WaitUntilAsync(() => processed.Count == 1);

        Assert.Equal(2, Volatile.Read(ref callCount));
        Assert.Equal(2L, Assert.Single(processed));
    }

    [Fact]
    public async Task TimedOutConsumerDisposesQueuedPayloads()
    {
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPayload = new TrackedPayload(1);
        var queuedPayload = new TrackedPayload(2);

        await using var bus = new FrameDispatchBus<TrackedPayload>(
            [
                new FrameConsumer<TrackedPayload>("flash", async (delivery, cancellationToken) =>
                {
                    if (delivery.FrameSequence == 1)
                    {
                        entered.TrySetResult(true);
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                        }
                    }
                })
            ],
            capacity: 2,
            consumerTimeout: TimeSpan.FromMilliseconds(50));

        Assert.True(bus.TryPublish(new FrameDelivery<TrackedPayload>(
            "session", 1, "flash", "one", firstPayload)).Published);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(bus.TryPublish(new FrameDelivery<TrackedPayload>(
            "session", 2, "flash", "two", queuedPayload)).Published);

        await WaitUntilAsync(() => queuedPayload.IsDisposed);
        Assert.False(firstPayload.IsDisposed);
    }

    [Fact]
    public async Task NoConsumerIsReportedAsDispatchError()
    {
        var errors = new ConcurrentQueue<FrameDispatchError>();
        await using var bus = CreateBus(
            new FrameConsumer<int>("flash", (_, _) => ValueTask.CompletedTask),
            errorSink: errors.Enqueue);

        var result = bus.TryPublish(Delivery(1, "rest", "rest-page"));

        Assert.Equal(FramePublishKind.NoConsumer, result.Kind);
        Assert.Contains(errors, error => error.Kind == FrameDispatchErrorKind.NoConsumer);
    }

    [Fact]
    public async Task ConsumerRejectionIsReportedAsDispatchError()
    {
        var errors = new ConcurrentQueue<FrameDispatchError>();
        await using var bus = CreateBus(
            new FrameConsumer<int>("flash", (_, _) => ValueTask.CompletedTask),
            errorSink: errors.Enqueue);
        var delivery = Delivery(1, "flash", "wrong-page");

        bus.ReportConsumerRejected(delivery, "The frame does not belong to this consumer.");

        Assert.Contains(errors, error => error.Kind == FrameDispatchErrorKind.ConsumerRejected);
    }

    [Fact]
    public async Task ConsumerExceptionIsReportedAndLaterFramesContinue()
    {
        var errors = new ConcurrentQueue<FrameDispatchError>();
        var processed = new ConcurrentQueue<long>();

        await using var bus = CreateBus(
            new FrameConsumer<int>("flash", (delivery, _) =>
            {
                if (delivery.FrameSequence == 1)
                {
                    throw new InvalidOperationException("boom");
                }

                processed.Enqueue(delivery.FrameSequence);
                return ValueTask.CompletedTask;
            }),
            capacity: 2,
            errorSink: errors.Enqueue);

        Assert.True(bus.TryPublish(Delivery(1, "flash", "bad")).Published);
        Assert.True(bus.TryPublish(Delivery(2, "flash", "next")).Published);

        await WaitUntilAsync(() => processed.Count == 1);
        await WaitUntilAsync(() => errors.Any(error =>
            error.Kind == FrameDispatchErrorKind.ConsumerFailed));

        Assert.Equal(2L, Assert.Single(processed));
        Assert.Contains(errors, error =>
            error.Kind == FrameDispatchErrorKind.ConsumerFailed
            && error.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task CancellationAndDisposalReleaseQueuedPayloads()
    {
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPayload = new TrackedPayload(1);
        var queuedPayload = new TrackedPayload(2);
        var callCount = 0;
        using var runCancellation = new CancellationTokenSource();
        var bus = new FrameDispatchBus<TrackedPayload>(
            [
                new FrameConsumer<TrackedPayload>("flash", async (_, cancellationToken) =>
                {
                    Interlocked.Increment(ref callCount);
                    entered.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                })
            ],
            capacity: 2,
            cancellationToken: runCancellation.Token);

        Assert.True(bus.TryPublish(new FrameDelivery<TrackedPayload>(
            "session", 1, "flash", "one", firstPayload)).Published);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(bus.TryPublish(new FrameDelivery<TrackedPayload>(
            "session", 2, "flash", "two", queuedPayload)).Published);

        await runCancellation.CancelAsync();
        await bus.DisposeAsync();

        Assert.True(queuedPayload.IsDisposed);
        Assert.False(firstPayload.IsDisposed);
        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    private static FrameDispatchBus<int> CreateBus(
        FrameConsumer<int> consumer,
        int capacity = 8,
        TimeSpan? consumerTimeout = null,
        Action<FrameDispatchError>? errorSink = null)
        => CreateBus([consumer], capacity, consumerTimeout, errorSink);

    private static FrameDispatchBus<int> CreateBus(
        FrameConsumer<int> first,
        FrameConsumer<int> second,
        int capacity = 8,
        TimeSpan? consumerTimeout = null,
        Action<FrameDispatchError>? errorSink = null)
        => CreateBus([first, second], capacity, consumerTimeout, errorSink);

    private static FrameDispatchBus<int> CreateBus(
        IEnumerable<FrameConsumer<int>> consumers,
        int capacity = 8,
        TimeSpan? consumerTimeout = null,
        Action<FrameDispatchError>? errorSink = null)
        => new(
            consumers,
            capacity,
            consumerTimeout,
            errorSink: errorSink);

    private static FrameDelivery<int> Delivery(long sequence, string consumerId, string signature)
        => new("session", sequence, consumerId, signature, checked((int)sequence));

    private sealed class TrackedPayload(int sequence) : IDisposable
    {
        public int Sequence { get; } = sequence;
        private int _disposed;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

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
}