using Automation.Core.Frames;

namespace Automation.Targets.Windows;

internal sealed class WindowsFrameSource : IFrameSource, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<nint> _resolveWindowHandle;
    private readonly WindowsAutomationOptions _options;
    private readonly WindowsGraphicsCaptureSource _wgc;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _cancellationToken;
    private readonly List<Waiter> _waiters = [];
    private readonly Task _captureLoop;
    private BgraFrameLease? _latest;
    private long _sequence;
    private string? _lastError;
    private bool _disposed;
    private bool _wgcAutoUnavailable;

    public WindowsFrameSource(
        Func<nint> resolveWindowHandle,
        WindowsAutomationOptions options)
    {
        _resolveWindowHandle = resolveWindowHandle;
        _options = options;
        _wgc = new WindowsGraphicsCaptureSource(options.FirstFrameTimeout);
        _cancellationToken = _cancellation.Token;
        _captureLoop = Task.Run(CaptureLoopAsync);
    }

    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    public WindowsCaptureMethod? LastCaptureMethod
    {
        get
        {
            lock (_gate)
            {
                return _latest?.CaptureMethod;
            }
        }
    }

    public async ValueTask<FrameLease> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_latest is not null)
            {
                return CreateLease(_latest);
            }
        }

        return await WaitNextAsync(0, _options.FirstFrameTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FrameLease> WaitNextAsync(
        long afterSequence,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        TaskCompletionSource<FrameLease> completion;
        lock (_gate)
        {
            if (_latest is not null && _latest.Sequence > afterSequence)
            {
                return CreateLease(_latest);
            }

            completion = new TaskCompletionSource<FrameLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new Waiter(afterSequence, completion));
        }

        try
        {
            return await completion.Task
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(CreateTimeoutMessage(afterSequence));
        }
        finally
        {
            lock (_gate)
            {
                _waiters.RemoveAll(waiter => ReferenceEquals(waiter.Completion, completion));
            }
        }
    }

    private async Task CaptureLoopAsync()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            try
            {
                var windowHandle = _resolveWindowHandle();
                if (windowHandle == 0)
                {
                    throw new InvalidOperationException("The target window is no longer available.");
                }

                var capturedAt = DateTimeOffset.Now;
                var captured = CaptureFrame(windowHandle);
                _cancellationToken.ThrowIfCancellationRequested();
                var frame = new BgraFrame(captured.Width, captured.Height, captured.BgraPixels);
                var lease = new BgraFrameLease(
                    Interlocked.Increment(ref _sequence),
                    capturedAt,
                    frame,
                    captured.Method);

                lock (_gate)
                {
                    _latest = lease;
                    _lastError = null;
                    foreach (var waiter in _waiters)
                    {
                        if (waiter.AfterSequence < lease.Sequence)
                        {
                            waiter.Completion.TrySetResult(CreateLease(lease));
                        }
                    }

                    _waiters.RemoveAll(waiter => waiter.Completion.Task.IsCompleted);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_gate)
                {
                    _lastError = exception.Message;
                }
            }

            try
            {
                await Task.Delay(_options.CaptureInterval, _cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private CapturedWindowFrame CaptureFrame(nint windowHandle)
    {
        if (_options.CaptureBackend == WindowsCaptureBackend.Compatibility)
        {
            return WindowsWindowCapture.CaptureClient(
                windowHandle,
                WindowsCaptureBackend.Compatibility);
        }

        if (_options.CaptureBackend == WindowsCaptureBackend.WindowsGraphicsCapture)
        {
            return _wgc.Capture(windowHandle, _cancellationToken);
        }

        if (!_wgcAutoUnavailable)
        {
            try
            {
                var frame = _wgc.Capture(windowHandle, _cancellationToken);
                if (!WindowsWindowCapture.IsLikelyBlack(frame))
                {
                    return frame;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            _wgcAutoUnavailable = true;
            _wgc.Dispose();
        }

        return WindowsWindowCapture.CaptureClient(
            windowHandle,
            WindowsCaptureBackend.Auto);
    }

    private string CreateTimeoutMessage(long afterSequence)
    {
        lock (_gate)
        {
            var sequenceText = _latest is null
                ? "no frame has been captured"
                : $"latest sequence is {_latest.Sequence}, requested after {afterSequence}";
            return _lastError is null
                ? $"Timed out waiting for a Windows frame; {sequenceText}."
                : $"Timed out waiting for a Windows frame; {sequenceText}; last error: {_lastError}.";
        }
    }

    private static BgraFrameLease CreateLease(BgraFrameLease source)
        => new(source.Sequence, source.CapturedAt, source.Frame, source.CaptureMethod);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        var captureLoopStopped = false;
        try
        {
            await _captureLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            captureLoopStopped = true;
        }
        catch (OperationCanceledException)
        {
            captureLoopStopped = true;
        }
        catch (TimeoutException)
        {
        }

        lock (_gate)
        {
            foreach (var waiter in _waiters)
            {
                waiter.Completion.TrySetCanceled(_cancellationToken);
            }

            _waiters.Clear();
            _latest = null;
        }

        _wgc.Dispose();
        if (captureLoopStopped)
        {
            _cancellation.Dispose();
        }
    }

    private sealed record Waiter(
        long AfterSequence,
        TaskCompletionSource<FrameLease> Completion);
}