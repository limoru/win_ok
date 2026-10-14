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

        await using var bus = new FrameDispatchBus<int>(
            async (delivery, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                processed.Enqueue(delivery.FrameSequence);
            },
            capacity: 2,
            duplicateRetention: TimeSpan.FromMinutes(1));

        Assert.True(bus.TryPublish(Delivery(1, "flash", "same-text")).Published);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(bus.TryPublish(Delivery(2, "flash", "changed-text")).Published);
        Assert.False(bus.IsCompleted);

        release.TrySetResult(true);
        await WaitUntilAsync(() => processed.Count == 2);

        Assert.Equal([1L, 2L], processed.ToArray());
    }

    [Fact]
    public async Task SameConsumerAndTextIsSuppressedUntilTimeout()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        await using var bus = new FrameDispatchBus<int>(
            (_, _) => ValueTask.CompletedTask,
            capacity: 4,
            duplicateRetention: TimeSpan.FromSeconds(5),
            timeProvider: clock);

        Assert.Equal(FramePublishKind.Published, bus.TryPublish(Delivery(1, "flash", "same-text")).Kind);
        Assert.Equal(
            FramePublishKind.SuppressedDuplicate,
            bus.TryPublish(Delivery(2, "flash", "same-text")).Kind);

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(FramePublishKind.Published, bus.TryPublish(Delivery(3, "flash", "same-text")).Kind);
    }

    [Fact]
    public async Task ChangedTextDispatchesImmediately()
    {
        await using var bus = new FrameDispatchBus<int>(
            (_, _) => ValueTask.CompletedTask,
            capacity: 4,
            duplicateRetention: TimeSpan.FromSeconds(5));

        Assert.Equal(FramePublishKind.Published, Pick(bus.TryPublish(Delivery(1, "flash", "old-text"))));
        Assert.Equal(FramePublishKind.Published, Pick(bus.TryPublish(Delivery(2, "flash", "new-text"))));
    }

    [Fact]
    public async Task FullQueueDropsOldestFrameAndKeepsNewest()
    {
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new ConcurrentQueue<long>();

        await using var bus = new FrameDispatchBus<int>(
            async (delivery, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                processed.Enqueue(delivery.FrameSequence);
            },
            capacity: 1,
            duplicateRetention: TimeSpan.FromMinutes(1));

        Assert.True(bus.TryPublish(Delivery(1, "flash", "one")).Published);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(bus.TryPublish(Delivery(2, "flash", "two")).Published);
        Assert.True(bus.TryPublish(Delivery(3, "flash", "three")).Published);

        release.TrySetResult(true);
        await WaitUntilAsync(() => processed.Count == 2);

        Assert.Equal([1L, 3L], processed.ToArray());
    }

    private static FrameDelivery<int> Delivery(long sequence, string consumerId, string signature)
        => new("session", sequence, consumerId, signature, checked((int)sequence));

    private static FramePublishKind Pick(FramePublishResult result) => result.Kind;

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

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
