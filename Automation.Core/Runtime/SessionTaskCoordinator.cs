namespace Automation.Core.Runtime;

public sealed class SessionTaskCoordinator : IDisposable
{
    private const int WaitingReportThreshold = 5;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly IReadOnlyList<ITaskModule> _modules;
    private readonly TaskContext _context;
    private readonly TimeSpan _pollInterval;
    private TaskCompletionSource<bool>? _resumeCompletion;
    private bool _paused;
    private bool _disposed;
    private bool _waitingReported;
    private int _consecutiveUnhandledFrames;
    private string? _currentModuleId;

    public SessionTaskCoordinator(
        IEnumerable<ITaskModule> modules,
        TaskContext context,
        TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        _modules = modules.ToArray();
        _pollInterval = pollInterval;
    }

    public IReadOnlyList<ITaskModule> Modules => _modules;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    public string? CurrentModuleId
    {
        get
        {
            lock (_gate)
            {
                return _currentModuleId;
            }
        }
    }

    public async ValueTask<bool> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var module in _modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    _currentModuleId = module.ModuleId;
                }

                if (await module.RunOnceAsync(_context, cancellationToken).ConfigureAwait(false))
                {
                    var wasWaiting = _waitingReported;
                    _waitingReported = false;
                    _consecutiveUnhandledFrames = 0;
                    if (wasWaiting)
                    {
                        ReportRunning(module);
                    }

                    return true;
                }
            }

            _consecutiveUnhandledFrames++;
            if (_consecutiveUnhandledFrames >= WaitingReportThreshold)
            {
                ReportWaitingOnce();
            }

            return false;
        }
        finally
        {
            lock (_gate)
            {
                _currentModuleId = null;
            }

            _runGate.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_paused)
            {
                return;
            }

            _paused = true;
            _resumeCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? resumeCompletion;
        lock (_gate)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            resumeCompletion = _resumeCompletion;
            _resumeCompletion = null;
        }

        resumeCompletion?.TrySetResult(true);
    }

    private async ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        Task? resumeTask;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_paused)
            {
                return;
            }

            resumeTask = _resumeCompletion!.Task;
        }

        await resumeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ReportWaitingOnce()
    {
        if (_waitingReported)
        {
            return;
        }

        _waitingReported = true;
        _context.Activity?.Report(new SessionActivity(
            SessionActivityKind.Waiting,
            "coordinator",
            "任务调度",
            "no-handler",
            "等待游戏页面变化",
            $"连续{_consecutiveUnhandledFrames}帧没有任务模块处理，当前处于页面切换或等待状态。",
            null,
            null,
            DateTimeOffset.Now));
    }

    private void ReportRunning(ITaskModule module)
    {
        _context.Activity?.Report(new SessionActivity(
            SessionActivityKind.Running,
            module.ModuleId,
            module.DisplayName,
            "handled",
            "继续执行",
            "已识别到可处理页面，任务继续运行。",
            null,
            null,
            DateTimeOffset.Now));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        TaskCompletionSource<bool>? resumeCompletion;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _paused = false;
            resumeCompletion = _resumeCompletion;
            _resumeCompletion = null;
        }

        resumeCompletion?.TrySetResult(true);
        _runGate.Dispose();
    }
}