using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Automation.Core.Dispatching;

public enum FramePublishKind
{
    Published,
    NoConsumer,
    QueueOverflow,
    Rejected
}

public enum FrameDispatchErrorKind
{
    NoConsumer,
    QueueOverflow,
    ConsumerRejected,
    ConsumerTimeout,
    ConsumerFailed,
    DeliveryRejected
}

public sealed record FrameDispatchError(
    string SessionId,
    long FrameSequence,
    string ConsumerId,
    FrameDispatchErrorKind Kind,
    string Message,
    Exception? Exception = null);

public readonly record struct FramePublishResult(
    FramePublishKind Kind,
    string Reason)
{
    public bool Published => Kind == FramePublishKind.Published;

    public static FramePublishResult Dispatch(string reason)
        => new(FramePublishKind.Published, reason);

    public static FramePublishResult NoConsumer(string reason)
        => new(FramePublishKind.NoConsumer, reason);

    public static FramePublishResult QueueOverflow(string reason)
        => new(FramePublishKind.QueueOverflow, reason);

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

public sealed record FrameConsumer<TFrame>(
    string ConsumerId,
    Func<FrameDelivery<TFrame>, CancellationToken, ValueTask> HandlerAsync);

/// <summary>
/// Non-blocking, per-consumer frame delivery bus.
/// Every published frame is queued for the selected consumer. The bus does not
/// deduplicate OCR text and does not lock business actions. Queue overflow,
/// missing consumers, consumer rejection, timeout and failure are reported as
/// structured errors while capture/OCR can continue.
/// </summary>
public sealed class FrameDispatchBus<TFrame> : IAsyncDisposable
{
    private readonly object _workersGate = new();
    private readonly IReadOnlyDictionary<string, FrameConsumer<TFrame>> _consumers;
    private readonly Dictionary<string, ConsumerWorker> _workers;
    private readonly ConcurrentBag<Task> _workerTasks = [];
    private readonly ConcurrentBag<ConsumerWorker> _allWorkers = [];
    private readonly CancellationTokenSource _lifetime;
    private readonly int _capacity;
    private readonly TimeSpan _consumerTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly Action<FrameDispatchError>? _errorSink;
    private readonly object _errorGate = new();
    private FrameDispatchError? _lastError;
    private int _disposed;

    public FrameDispatchBus(
        IEnumerable<FrameConsumer<TFrame>> consumers,
        int capacity = 8,
        TimeSpan? consumerTimeout = null,
        TimeProvider? timeProvider = null,
        Action<FrameDispatchError>? errorSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _capacity = capacity;
        _consumerTimeout = consumerTimeout ?? TimeSpan.FromSeconds(30);
        if (_consumerTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(consumerTimeout));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _errorSink = errorSink;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var consumersById = new Dictionary<string, FrameConsumer<TFrame>>(StringComparer.Ordinal);
        foreach (var consumer in consumers)
        {
            ArgumentNullException.ThrowIfNull(consumer);
            ArgumentException.ThrowIfNullOrWhiteSpace(consumer.ConsumerId);
            ArgumentNullException.ThrowIfNull(consumer.HandlerAsync);
            if (!consumersById.TryAdd(consumer.ConsumerId, consumer))
            {
                throw new ArgumentException(
                    $"Duplicate frame consumer id '{consumer.ConsumerId}'.",
                    nameof(consumers));
            }
        }

        if (consumersById.Count == 0)
        {
            throw new ArgumentException("At least one frame consumer is required.", nameof(consumers));
        }

        _consumers = consumersById;
        _workers = [];
        foreach (var consumer in _consumers.Values)
        {
            _workers.Add(consumer.ConsumerId, CreateAndStartWorker(consumer));
        }
    }

    public int Capacity { get; }

    public IReadOnlyCollection<string> ConsumerIds => _consumers.Keys.ToArray();

    public bool IsCompleted
    {
        get
        {
            lock (_workersGate)
            {
                return _workers.Count == 0
                       || _workers.Values.All(worker => worker.ExecutionTask.IsCompleted);
            }
        }
    }

    public bool IsFaulted
    {
        get
        {
            lock (_workersGate)
            {
                return _workers.Values.Any(worker =>
                    worker.IsFaulted || worker.ExecutionTask.IsFaulted);
            }
        }
    }

    public FrameDispatchError? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// Select and enqueue a frame without waiting for the consumer. Every call
    /// is delivered; identical OCR text is not suppressed by the bus.
    /// </summary>
    public FramePublishResult TryPublish(FrameDelivery<TFrame> delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (Volatile.Read(ref _disposed) != 0)
        {
            var error = CreateError(
                delivery,
                FrameDispatchErrorKind.DeliveryRejected,
                "帧总线已经释放，拒绝新的投递。");
            ReportError(error);
            return FramePublishResult.Reject(error.Message);
        }

        if (!_consumers.TryGetValue(delivery.ConsumerId, out var consumer))
        {
            var error = CreateError(
                delivery,
                FrameDispatchErrorKind.NoConsumer,
                "当前帧没有匹配到消费者。");
            ReportError(error);
            return FramePublishResult.NoConsumer(error.Message);
        }

        ConsumerWorker worker;
        lock (_workersGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                var error = CreateError(
                    delivery,
                    FrameDispatchErrorKind.DeliveryRejected,
                    "帧总线已经释放，拒绝新的投递。");
                ReportError(error);
                return FramePublishResult.Reject(error.Message);
            }

            worker = _workers[delivery.ConsumerId];
            if (worker.IsFaulted || worker.ExecutionTask.IsCompleted)
            {
                worker = CreateAndStartWorker(consumer);
                _workers[delivery.ConsumerId] = worker;
            }
        }

        if (!worker.Channel.Writer.TryWrite(delivery))
        {
            var error = CreateError(
                delivery,
                FrameDispatchErrorKind.QueueOverflow,
                $"消费者 {delivery.ConsumerId} 的投递队列已满。");
            ReportError(error);
            return FramePublishResult.QueueOverflow(error.Message);
        }

        return FramePublishResult.Dispatch($"已投递给消费者 {delivery.ConsumerId}。");
    }

    public void ReportConsumerRejected(FrameDelivery<TFrame> delivery, string reason)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ReportError(CreateError(
            delivery,
            FrameDispatchErrorKind.ConsumerRejected,
            reason));
    }

    public void ReportConsumerFailed(
        FrameDelivery<TFrame> delivery,
        Exception exception,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(exception);
        ReportError(CreateError(
            delivery,
            FrameDispatchErrorKind.ConsumerFailed,
            message ?? $"Consumer {delivery.ConsumerId} failed: {exception.Message}",
            exception));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ConsumerWorker[] currentWorkers;
        lock (_workersGate)
        {
            currentWorkers = _workers.Values.ToArray();
        }

        foreach (var worker in currentWorkers)
        {
            worker.Channel.Writer.TryComplete();
        }

        try
        {
            await Task.WhenAll(_workerTasks.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _lifetime.Cancel();
            try
            {
                await Task.WhenAll(_workerTasks.ToArray()).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var worker in _allWorkers)
            {
                DrainPendingDeliveries(worker);
                worker.Dispose();
            }

            _lifetime.Dispose();
        }
    }

    private async Task RunConsumerAsync(ConsumerWorker worker)
    {
        try
        {
            await foreach (var delivery in worker.Channel.Reader
                               .ReadAllAsync(worker.Lifetime.Token)
                               .ConfigureAwait(false))
            {
                if (worker.Lifetime.IsCancellationRequested)
                {
                    ReleaseDelivery(delivery);
                    worker.Lifetime.Token.ThrowIfCancellationRequested();
                }

                Task handlerTask;
                try
                {
                    handlerTask = worker.HandlerAsync(delivery, worker.Lifetime.Token).AsTask();
                }
                catch (OperationCanceledException) when (worker.Lifetime.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportError(CreateError(
                        delivery,
                        FrameDispatchErrorKind.ConsumerFailed,
                        $"Consumer {delivery.ConsumerId} failed: {exception.Message}",
                        exception));
                    continue;
                }

                var timeoutTask = Task.Delay(
                    _consumerTimeout,
                    _timeProvider,
                    worker.Lifetime.Token);
                var completed = await Task
                    .WhenAny(handlerTask, timeoutTask)
                    .ConfigureAwait(false);
                if (ReferenceEquals(completed, timeoutTask))
                {
                    worker.Lifetime.Token.ThrowIfCancellationRequested();
                    worker.MarkFaulted();
                    worker.Cancel();
                    ReportError(CreateError(
                        delivery,
                        FrameDispatchErrorKind.ConsumerTimeout,
                        $"消费者 {delivery.ConsumerId} 超时未返回，当前 worker 已停止，将在下一帧自动重建。",
                        new TimeoutException($"Consumer {delivery.ConsumerId} timed out.")));
                    ObserveHandlerCompletion(handlerTask);
                    DrainPendingDeliveries(worker);
                    return;
                }

                try
                {
                    await handlerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (worker.Lifetime.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportError(CreateError(
                        delivery,
                        FrameDispatchErrorKind.ConsumerFailed,
                        $"消费者 {delivery.ConsumerId} 执行失败：{exception.Message}",
                        exception));
                }

                // Cancellation must stop new business actions immediately. Any
                // remaining queued deliveries are released during disposal.
                worker.Lifetime.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (worker.Lifetime.IsCancellationRequested)
        {
        }
    }

    private static void ObserveHandlerCompletion(Task handlerTask)
    {
        _ = handlerTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void DrainPendingDeliveries(ConsumerWorker worker)
    {
        worker.Channel.Writer.TryComplete();
        while (worker.Channel.Reader.TryRead(out var delivery))
        {
            ReleaseDelivery(delivery);
        }
    }

    private static void ReleaseDelivery(FrameDelivery<TFrame> delivery)
    {
        if (delivery.Payload is not IDisposable disposable)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Cleanup failure must not stop worker replacement or bus disposal.
        }
    }

    private ConsumerWorker CreateAndStartWorker(FrameConsumer<TFrame> consumer)
    {
        var worker = new ConsumerWorker(
            consumer.ConsumerId,
            consumer.HandlerAsync,
            Channel.CreateBounded<FrameDelivery<TFrame>>(
                new BoundedChannelOptions(_capacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.Wait
                }),
            _lifetime.Token);
        worker.ExecutionTask = Task.Run(() => RunConsumerAsync(worker));
        _workerTasks.Add(worker.ExecutionTask);
        _allWorkers.Add(worker);
        return worker;
    }

    private void ReportError(FrameDispatchError error)
    {
        Volatile.Write(ref _lastError, error);
        _errorSink?.Invoke(error);
    }

    private static FrameDispatchError CreateError(
        FrameDelivery<TFrame> delivery,
        FrameDispatchErrorKind kind,
        string message,
        Exception? exception = null)
        => new(
            delivery.SessionId,
            delivery.FrameSequence,
            delivery.ConsumerId,
            kind,
            message,
            exception);

    private sealed class ConsumerWorker : IDisposable
    {
        private int _faulted;
        private int _disposed;

        public ConsumerWorker(
            string consumerId,
            Func<FrameDelivery<TFrame>, CancellationToken, ValueTask> handlerAsync,
            Channel<FrameDelivery<TFrame>> channel,
            CancellationToken busLifetime)
        {
            ConsumerId = consumerId;
            HandlerAsync = handlerAsync;
            Channel = channel;
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(busLifetime);
        }

        public string ConsumerId { get; }
        public Func<FrameDelivery<TFrame>, CancellationToken, ValueTask> HandlerAsync { get; }
        public Channel<FrameDelivery<TFrame>> Channel { get; }
        public CancellationTokenSource Lifetime { get; }
        public Task ExecutionTask { get; set; } = Task.CompletedTask;
        public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

        public void MarkFaulted() => Interlocked.Exchange(ref _faulted, 1);

        public void Cancel()
        {
            if (!Lifetime.IsCancellationRequested)
            {
                Lifetime.Cancel();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Lifetime.Dispose();
            }
        }
    }
}