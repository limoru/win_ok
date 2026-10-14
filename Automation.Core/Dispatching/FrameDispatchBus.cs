using System.Threading.Channels;

namespace Automation.Core.Dispatching;

public enum FramePublishKind
{
    Published,
    SuppressedDuplicate,
    Rejected
}

public readonly record struct FramePublishResult(
    FramePublishKind Kind,
    string Reason)
{
    public bool Published => Kind == FramePublishKind.Published;

    public static FramePublishResult Dispatch(string reason)
        => new(FramePublishKind.Published, reason);

    public static FramePublishResult Suppress(string reason)
        => new(FramePublishKind.SuppressedDuplicate, reason);

    public static FramePublishResult Reject(string reason)
        => new(FramePublishKind.Rejected, reason);
}

/// <summary>
/// A frame observation selected for one consumer.
/// The bus owns delivery; the consumer owns the business decision and action.
/// </summary>
public sealed record FrameDelivery<TFrame>(
    string SessionId,
    long FrameSequence,
    string ConsumerId,
    string TextSignature,
    TFrame Payload);

/// <summary>
/// Single-consumer, non-blocking frame delivery bus.
/// Capturing/OCR can keep publishing while a consumer is running. Identical
/// OCR text for the same consumer is suppressed until the retry timeout.
/// </summary>
public sealed class FrameDispatchBus<TFrame> : IAsyncDisposable
{
    private readonly Channel<FrameDelivery<TFrame>> _channel;
    private readonly Func<FrameDelivery<TFrame>, CancellationToken, ValueTask> _consumer;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _consumerTask;
    private readonly TimeSpan _duplicateRetention;
    private readonly TimeProvider _timeProvider;
    private readonly object _selectionGate = new();
    private Exception? _lastError;
    private string? _lastConsumerId;
    private string? _lastTextSignature;
    private DateTimeOffset _lastPublishedAt;
    private bool _disposed;

    public FrameDispatchBus(
        Func<FrameDelivery<TFrame>, CancellationToken, ValueTask> consumer,
        int capacity = 8,
        TimeSpan? duplicateRetention = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _consumer = consumer;
        Capacity = capacity;
        _duplicateRetention = duplicateRetention ?? TimeSpan.FromSeconds(5);
        if (_duplicateRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duplicateRetention));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _channel = Channel.CreateBounded<FrameDelivery<TFrame>>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        _consumerTask = Task.Run(RunConsumerAsync);
    }

    public int Capacity { get; }

    public bool IsCompleted => _consumerTask.IsCompleted;

    public Exception? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// Select and enqueue a frame without waiting for the consumer. The newest
    /// frame wins when the bounded queue is full.
    /// </summary>
    public FramePublishResult TryPublish(FrameDelivery<TFrame> delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_selectionGate)
        {
            var now = _timeProvider.GetUtcNow();
            if (string.Equals(_lastConsumerId, delivery.ConsumerId, StringComparison.Ordinal)
                && string.Equals(_lastTextSignature, delivery.TextSignature, StringComparison.Ordinal)
                && now - _lastPublishedAt < _duplicateRetention)
            {
                return FramePublishResult.Suppress(
                    $"消费者 {delivery.ConsumerId} 已收到相同 OCR 文本，等待超时后重投。");
            }

            _lastConsumerId = delivery.ConsumerId;
            _lastTextSignature = delivery.TextSignature;
            _lastPublishedAt = now;
            if (!_channel.Writer.TryWrite(delivery))
            {
                return FramePublishResult.Reject("帧总线已停止或拒绝新帧。");
            }

            return FramePublishResult.Dispatch(
                $"已投递给消费者 {delivery.ConsumerId}。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        try
        {
            await _consumerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _lifetime.Cancel();
            await _consumerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }

        _lifetime.Dispose();
    }

    private async Task RunConsumerAsync()
    {
        try
        {
            await foreach (var delivery in _channel.Reader
                               .ReadAllAsync(_lifetime.Token)
                               .ConfigureAwait(false))
            {
                try
                {
                    await _consumer(delivery, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref _lastError, exception);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }
}
