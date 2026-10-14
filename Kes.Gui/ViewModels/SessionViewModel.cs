using System.Collections.ObjectModel;
using System.IO;
using Automation.Core.Configuration;
using Automation.Core.DeveloperTools;
using Automation.Core.Runtime;
using Automation.Core.Sessions;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Automation.Targets.Windows;
using Automation.Vision.Onnx;
using Kes.Gui.ViewModels.Tasks;
using Kes.Tasks;
using Kes.Tasks.Configuration;

namespace Kes.Gui.ViewModels;

public sealed class SessionActivityChangedEventArgs(SessionActivity activity) : EventArgs
{
    public SessionActivity Activity { get; } = activity;
}

public sealed class SessionLogChangedEventArgs(
    string sessionId,
    string source,
    string message,
    string? tag) : EventArgs
{
    public string SessionId { get; } = sessionId;
    public string Source { get; } = source;
    public string Message { get; } = message;
    public string? Tag { get; } = tag;
}

public sealed class SessionViewModel : ObservableObject, IAsyncDisposable, ITaskLogSink
{
    public event EventHandler<SessionActivityChangedEventArgs>? ActivityReported;
    public event EventHandler<SessionLogChangedEventArgs>? LogReported;

    private static readonly Lazy<IOcrProvider?> SharedOcrProvider =
        new(CreateSharedOcrProvider);

    private readonly Lazy<IFeatureMatcher?> _featureMatcher =
        new(CreateFeatureMatcher);

    private SessionState _state = SessionState.Ready;
    private SessionActivity _activity = SessionActivity.Idle("等待绑定目标");
    private string _currentModule = "-";
    private string _lastMessage = "已创建会话";
    private DateTimeOffset _lastActivity = DateTimeOffset.Now;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Func<ITargetProvider> _targetProviderFactory;
    private readonly IDecisionSnapshotSink? _decisionSnapshots;
    private readonly TaskConfigProfileStore _configProfileStore;
    private readonly ITaskModuleCatalog _taskModuleCatalog;
    private readonly TaskPlanResolver _taskPlanResolver;
    private readonly TimeSpan _taskPollInterval;
    private SessionTaskPlan _taskPlan = new(null, []);
    private TaskPlanOptionViewModel _selectedTaskPlanOption = null!;
    private TaskConfigProfile? _selectedChaosModeProfile;
    private readonly SynchronizationContext? _synchronizationContext;
    private ITargetSession? _targetSession;
    private SessionTaskCoordinator? _taskCoordinator;
    private CancellationTokenSource? _taskRunCancellation;
    private Task? _taskRun;
    private int _taskRunGeneration;
    private long _frameSequence;
    private int _ocrMilliseconds;
    private int _inputFailureCount;
    private bool _disposed;

    public SessionViewModel(
        string sessionId,
        TargetBinding binding,
        UiProfile uiProfile,
        string accountLabel,
        Func<ITargetProvider> targetProviderFactory,
        TaskConfigProfileStore configProfileStore,
        ITaskModuleCatalog? taskModuleCatalog = null,
        TimeSpan? taskPollInterval = null,
        IDecisionSnapshotSink? decisionSnapshots = null)
    {
        SessionId = sessionId;
        Binding = binding;
        UiProfile = uiProfile;
        AccountLabel = accountLabel;
        _targetProviderFactory = targetProviderFactory
            ?? throw new ArgumentNullException(nameof(targetProviderFactory));
        _configProfileStore = configProfileStore
            ?? throw new ArgumentNullException(nameof(configProfileStore));
        _decisionSnapshots = decisionSnapshots;
        _taskModuleCatalog = taskModuleCatalog ?? TaskModuleCatalog.Instance;
        _taskPlanResolver = new TaskPlanResolver(_taskModuleCatalog);
        TaskModules = _taskModuleCatalog.GetDescriptors()
            .Where(descriptor => descriptor.Kind != TaskModuleKind.DeveloperDiagnostic)
            .OrderBy(descriptor => descriptor.Order)
            .Select(descriptor => new TaskModuleItemViewModel(descriptor))
            .ToArray();
        TaskPlanOptions = BuildTaskPlanOptions(_taskModuleCatalog);
        _selectedTaskPlanOption = TaskPlanOptions[0];
        _selectedChaosModeProfile = _configProfileStore.Profiles.FirstOrDefault();
        if (_selectedChaosModeProfile is not null)
        {
            ChaosModeSettings.LoadSnapshot(_selectedChaosModeProfile.Values);
        }
        _taskPollInterval = taskPollInterval ?? TimeSpan.FromMilliseconds(200);
        if (_taskPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(taskPollInterval));
        }

        _synchronizationContext = SynchronizationContext.Current;
        CreatedAt = DateTimeOffset.Now;
    }

    public string SessionId { get; }
    public TargetBinding Binding { get; }
    public IReadOnlyList<TaskModuleItemViewModel> TaskModules { get; }
    public UiProfile UiProfile { get; }
    public string AccountLabel { get; }
    public DateTimeOffset CreatedAt { get; }
    public string TargetTypeLabel => Binding.Kind == TargetKind.WindowsWindow ? "Windows" : "MuMu";
    public string UiProfileLabel => UiProfile == UiProfile.WindowsPc ? "PC" : "Android";
    public string CreatedAtText => CreatedAt.ToString("HH:mm:ss");
    public IReadOnlyList<TaskPlanOptionViewModel> TaskPlanOptions { get; }
    public ChaosModeSettingsViewModel ChaosModeSettings { get; } = new();
    public ObservableCollection<TaskConfigProfile> ChaosModeProfiles => _configProfileStore.Profiles;

    public TaskPlanOptionViewModel SelectedTaskPlanOption
    {
        get => _selectedTaskPlanOption;
        set
        {
            if (value is null || !CanChangeTaskPlan || ReferenceEquals(_selectedTaskPlanOption, value))
            {
                return;
            }

            if (SetProperty(ref _selectedTaskPlanOption, value))
            {
                _taskPlan = value.Plan;
                OnPropertyChanged(nameof(HasSelectedTask));
                OnPropertyChanged(nameof(HasSelectedChaosModeTask));
                if (_taskPlan.PrimaryModuleId == "ChaosMode" && SelectedChaosModeProfile is null)
                {
                    SelectedChaosModeProfile = ChaosModeProfiles.FirstOrDefault();
                }

                UpdateMessage(value.Plan.PrimaryModuleId is null
                    ? "未选择主任务方案。"
                    : $"已选择任务方案：{value.DisplayName}");
            }
        }
    }

    public TaskConfigProfile? SelectedChaosModeProfile
    {
        get => _selectedChaosModeProfile;
        set
        {
            if (ReferenceEquals(_selectedChaosModeProfile, value))
            {
                return;
            }

            if (value is not null && !CanChangeTaskPlan)
            {
                return;
            }

            if (SetProperty(ref _selectedChaosModeProfile, value) && value is not null)
            {
                ChaosModeSettings.LoadSnapshot(value.Values);
                UpdateMessage($"已加载配置方案：{value.Name}");
            }
        }
    }

    public bool HasSelectedChaosModeTask => _taskPlan.PrimaryModuleId == "ChaosMode";

    public bool HasSelectedTask => _taskPlan.PrimaryModuleId is not null;
    public bool CanChangeTaskPlan => State is SessionState.Ready
        or SessionState.Stopped
        or SessionState.Faulted
        or SessionState.NeedsAttention;

    public SessionState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(CanChangeTaskPlan));
            }
        }
    }

    public string StateLabel => State switch
    {
        SessionState.Ready => "未启动",
        SessionState.Stopped => "已停止",
        SessionState.Starting => "启动中",
        SessionState.Running => "运行中",
        SessionState.Paused => "已暂停",
        SessionState.Faulted => "故障",
        SessionState.NeedsAttention => "需处理",
        _ => State.ToString()
    };

    public SessionActivity Activity
    {
        get => _activity;
        private set
        {
            if (!SetProperty(ref _activity, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CurrentActivityTitle));
            OnPropertyChanged(nameof(CurrentActivityStep));
            OnPropertyChanged(nameof(CurrentActivityDetail));
            OnPropertyChanged(nameof(ActivityProgressText));
            OnPropertyChanged(nameof(ActivityProgressValue));
            OnPropertyChanged(nameof(HasActivityProgress));
            OnPropertyChanged(nameof(ActivityStatusLabel));
            OnPropertyChanged(nameof(ActivityUpdatedText));
        }
    }

    public string CurrentActivityTitle => Activity.TaskLabel;
    public string CurrentActivityStep => Activity.StepLabel;
    public string CurrentActivityDetail => Activity.Detail;
    public string ActivityProgressText => Activity.ProgressText;
    public double ActivityProgressValue => Activity.ProgressValue;
    public bool HasActivityProgress => Activity.HasProgress;
    public string ActivityUpdatedText => Activity.UpdatedAt.ToString("HH:mm:ss");

    public string ActivityStatusLabel => Activity.Kind switch
    {
        SessionActivityKind.Idle => "空闲",
        SessionActivityKind.Running => "运行中",
        SessionActivityKind.Waiting => "等待",
        SessionActivityKind.Progress => "进行中",
        SessionActivityKind.Paused => "已暂停",
        SessionActivityKind.Completed => "已完成",
        SessionActivityKind.Faulted => "故障",
        _ => Activity.Kind.ToString()
    };

    public string CurrentModule
    {
        get => _currentModule;
        private set => SetProperty(ref _currentModule, value);
    }

    public string LastMessage
    {
        get => _lastMessage;
        private set => SetProperty(ref _lastMessage, value);
    }

    public DateTimeOffset LastActivity
    {
        get => _lastActivity;
        private set
        {
            if (SetProperty(ref _lastActivity, value))
            {
                OnPropertyChanged(nameof(LastActivityText));
            }
        }
    }

    public string LastActivityText => LastActivity.ToString("HH:mm:ss");
    public long FrameSequence => _frameSequence;
    public int OcrMilliseconds => _ocrMilliseconds;
    public int InputFailureCount => _inputFailureCount;
    public bool HasTargetSession => _targetSession is not null;
    public ITargetSession? TargetSession => _targetSession;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_targetSession is not null)
            {
                _taskCoordinator?.Resume();
                State = SessionState.Running;
                CurrentModule = _taskCoordinator?.CurrentModuleId ?? "Task";
                ReportActivity(new SessionActivity(
                    SessionActivityKind.Running,
                    "runtime",
                    "任务运行时",
                    "resumed",
                    "会话已恢复",
                    "任务循环继续执行。",
                    null,
                    null,
                    DateTimeOffset.Now));
                UpdateMessage("会话已恢复，任务循环继续执行。");
                return;
            }

            State = SessionState.Starting;
            CurrentModule = "Target";
            ReportActivity(new SessionActivity(
                SessionActivityKind.Waiting,
                "target",
                "连接目标",
                "opening-target",
                "建立 Windows 通道",
                "正在绑定窗口、启动共享抓帧并等待首帧。",
                null,
                null,
                DateTimeOffset.Now));
            UpdateMessage("正在建立 Windows 抓帧与输入通道。");

            ITargetSession? target = null;
            try
            {
                target = await _targetProviderFactory().OpenAsync(Binding, cancellationToken);
                using var firstFrame = await target.Frames.GetLatestAsync(cancellationToken);
                _targetSession = target;
                target = null;
                StartTaskLoop(_targetSession);

                State = SessionState.Running;
                CurrentModule = "Target";
                UpdateDiagnostics(firstFrame.Sequence, _ocrMilliseconds, _inputFailureCount);
                var captureMethod = firstFrame is BgraFrameLease windowsLease
                    ? $" · {windowsLease.CaptureMethod}"
                    : string.Empty;
                var detail = $"已连接 {firstFrame.Width}x{firstFrame.Height}，首帧 #{firstFrame.Sequence}{captureMethod}。";
                ReportActivity(new SessionActivity(
                    SessionActivityKind.Running,
                    "target",
                    "连接目标",
                    "target-ready",
                    "Windows 通道已就绪",
                    detail,
                    null,
                    null,
                    DateTimeOffset.Now));
                UpdateMessage(detail);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                State = SessionState.Stopped;
                CurrentModule = "-";
                ReportActivity(SessionActivity.Idle("连接已取消"));
                UpdateMessage("连接目标已取消。");
            }
            catch (Exception exception)
            {
                State = SessionState.Faulted;
                CurrentModule = "Target";
                ReportActivity(new SessionActivity(
                    SessionActivityKind.Faulted,
                    "target",
                    "连接目标",
                    "target-failed",
                    "Windows 通道启动失败",
                    exception.Message,
                    null,
                    null,
                    DateTimeOffset.Now));
                UpdateMessage($"目标启动失败：{exception.Message}");
            }
            finally
            {
                if (target is not null)
                {
                    await target.DisposeAsync();
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Pause()
    {
        if (State == SessionState.Running)
        {
            _taskCoordinator?.Pause();
            State = SessionState.Paused;
            ReportActivity(Activity with
            {
                Kind = SessionActivityKind.Paused,
                StepLabel = "已暂停",
                Detail = "等待恢复运行",
                UpdatedAt = DateTimeOffset.Now
            });
            UpdateMessage("会话已暂停");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var target = _targetSession;
            var runCancellation = _taskRunCancellation;
            var runTask = _taskRun;
            var coordinator = _taskCoordinator;

            _targetSession = null;
            _taskRunCancellation = null;
            _taskRun = null;
            _taskCoordinator = null;
            Interlocked.Increment(ref _taskRunGeneration);

            runCancellation?.Cancel();
            if (runTask is not null)
            {
                try
                {
                    await runTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            runCancellation?.Dispose();
            coordinator?.Dispose();

            if (target is not null)
            {
                await target.DisposeAsync().ConfigureAwait(true);
            }

            State = SessionState.Stopped;
            CurrentModule = "-";
            ReportActivity(SessionActivity.Idle("会话已停止"));
            UpdateMessage("会话已停止，Windows 通道已释放。");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }


    private void StartTaskLoop(ITargetSession target)
    {
        var generation = Interlocked.Increment(ref _taskRunGeneration);
        var resolution = _taskPlanResolver.Resolve(_taskPlan, UiProfile);
        if (!resolution.IsValid || resolution.Plan is null)
        {
            var message = string.Join(
                "；",
                resolution.Issues.Select(issue => issue.Message));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? "The task plan could not be resolved."
                    : message);
        }

        var runtimeFeatureTemplates = CreateRuntimeTemplateStore();
        var context = new TaskContext(
            SessionId,
            UiProfile,
            target.Capabilities,
            target.Frames,
            target.Input,
            new TaskActivityReporter(this, generation),
            new TaskLogSink(this, generation),
            HasSelectedChaosModeTask ? ChaosModeSettings.Snapshot : null,
            HasSelectedChaosModeTask ? SharedOcrProvider.Value : null,
            HasSelectedChaosModeTask ? ChaosModeConfigSchema.Instance : null,
            HasSelectedChaosModeTask ? _featureMatcher.Value : null,
            runtimeFeatureTemplates,
            _decisionSnapshots,
            new TaskStopRequestSink(this, generation));
        var coordinator = new SessionTaskCoordinator(
            resolution.Plan.Modules,
            context,
            _taskPollInterval);
        var cancellation = new CancellationTokenSource();

        _taskCoordinator = coordinator;
        _taskRunCancellation = cancellation;
        _taskRun = Task.Run(() => RunTaskLoopAsync(coordinator, generation, cancellation.Token));
    }

    private async Task RunTaskLoopAsync(
        SessionTaskCoordinator coordinator,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await coordinator.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref _taskRunGeneration))
            {
                return;
            }

            PostToUi(() =>
            {
                if (generation != Volatile.Read(ref _taskRunGeneration))
                {
                    return;
                }

                var moduleId = coordinator.CurrentModuleId ?? "runtime";
                State = SessionState.Faulted;
                CurrentModule = moduleId;
                ReportActivity(new SessionActivity(
                    SessionActivityKind.Faulted,
                    moduleId,
                    "任务运行时",
                    "task-failed",
                    "任务执行失败",
                    exception.Message,
                    null,
                    null,
                    DateTimeOffset.Now));
                UpdateMessage($"任务执行失败：{exception.Message}");
            });
        }
    }

    private void ReportTaskActivity(int generation, SessionActivity activity)
    {
        if (generation != Volatile.Read(ref _taskRunGeneration))
        {
            return;
        }

        PostToUi(() =>
        {
            if (generation == Volatile.Read(ref _taskRunGeneration))
            {
                CurrentModule = activity.TaskId;
                ReportActivity(activity);
            }
        });
    }

    private void WriteTaskLog(string sessionId, string message, string? tag)
    {
        PostToUi(() => LogReported?.Invoke(
            this,
            new SessionLogChangedEventArgs(sessionId, "task", message, tag)));
    }

    private void PostToUi(Action action)
    {
        var synchronizationContext = _synchronizationContext;
        if (synchronizationContext is null
            || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            action();
            return;
        }

        synchronizationContext.Post(_ => action(), null);
    }

    private async Task StopFromTaskRequestAsync(int generation, string reason)
    {
        if (generation != Volatile.Read(ref _taskRunGeneration))
        {
            return;
        }

        UpdateMessage(reason);
        try
        {
            await StopAsync();
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _taskRunGeneration))
            {
                WriteTaskLog(SessionId, $"停止会话失败：{exception.Message}", "warning");
            }
        }
    }

    public void Log(string sessionId, string message, string? tag = null)
        => WriteTaskLog(sessionId, message, tag);
    public void MarkNeedsAttention(string message)
    {
        State = SessionState.NeedsAttention;
        ReportActivity(Activity with
        {
            Kind = SessionActivityKind.Faulted,
            StepLabel = "需要人工处理",
            Detail = message,
            UpdatedAt = DateTimeOffset.Now
        });
        UpdateMessage(message);
    }

    public void ReportActivity(SessionActivity activity)
    {
        Activity = activity;
        ActivityReported?.Invoke(this, new SessionActivityChangedEventArgs(activity));
    }

    public void UpdateMessage(string message)
    {
        LastMessage = message;
        LastActivity = DateTimeOffset.Now;
    }

    public void UpdateDiagnostics(long frameSequence, int ocrMilliseconds, int inputFailureCount)
    {
        _frameSequence = frameSequence;
        _ocrMilliseconds = ocrMilliseconds;
        _inputFailureCount = inputFailureCount;
        OnPropertyChanged(nameof(FrameSequence));
        OnPropertyChanged(nameof(OcrMilliseconds));
        OnPropertyChanged(nameof(InputFailureCount));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        _lifecycleGate.Dispose();
    }

    private IRuntimeFeatureTemplateStore? CreateRuntimeTemplateStore()
    {
        if (!HasSelectedChaosModeTask || SelectedChaosModeProfile is null)
        {
            return null;
        }

        var targetName = ChaosModeSettings.GetValue("刷存档主战员") as string;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return null;
        }

        return new TaskConfigProfileRuntimeTemplateStore(
            _configProfileStore,
            SelectedChaosModeProfile,
            $"{SelectedChaosModeProfile.Id}|{targetName.Trim()}");
    }

    private static IOcrProvider? CreateSharedOcrProvider()
    {
        return PpOcrEngine.TryCreateDefault(out var engine, out _)
            ? engine
            : null;
    }

    private static IFeatureMatcher? CreateFeatureMatcher()
    {
        var assetPath = ChaosFeatureAssets.GetDefaultPath();
        if (!Directory.Exists(assetPath))
        {
            return null;
        }

        var catalog = FeatureTemplateCatalog.Load(assetPath);
        return new TemplateFeatureMatcher(catalog);
    }

    private static IReadOnlyList<TaskPlanOptionViewModel> BuildTaskPlanOptions(ITaskModuleCatalog catalog)
    {
        var options = new List<TaskPlanOptionViewModel>
        {
            new(
                "暂不选择",
                "只连接目标，不运行主任务模式。",
                new SessionTaskPlan(null, []))
        };

        options.AddRange(catalog.GetDescriptors()
            .Where(descriptor => descriptor.Kind == TaskModuleKind.PrimaryMode)
            .OrderBy(descriptor => descriptor.Order)
            .Select(descriptor => new TaskPlanOptionViewModel(
                descriptor.Availability == TaskModuleAvailability.Planned
                    ? $"{descriptor.DisplayName}（待实现）"
                    : descriptor.DisplayName,
                descriptor.Description,
                new SessionTaskPlan(descriptor.ModuleId, []),
                descriptor)));

        return options;
    }

    private sealed class TaskActivityReporter(
        SessionViewModel owner,
        int generation) : IActivityReporter
    {
        public void Report(SessionActivity activity)
            => owner.ReportTaskActivity(generation, activity);
    }

    private sealed class TaskStopRequestSink(
        SessionViewModel owner,
        int generation) : ITaskStopRequestSink
    {
        public void RequestStop(string sessionId, string reason)
        {
            if (generation != Volatile.Read(ref owner._taskRunGeneration))
            {
                return;
            }

            if (owner._synchronizationContext is null)
            {
                _ = Task.Run(() => owner.StopFromTaskRequestAsync(generation, reason));
                return;
            }

            owner.PostToUi(() => _ = owner.StopFromTaskRequestAsync(generation, reason));
        }
    }

    private sealed class TaskLogSink(
        SessionViewModel owner,
        int generation) : ITaskLogSink
    {
        public void Log(string sessionId, string message, string? tag = null)
        {
            if (generation == Volatile.Read(ref owner._taskRunGeneration))
            {
                owner.WriteTaskLog(sessionId, message, tag);
            }
        }
    }
}
