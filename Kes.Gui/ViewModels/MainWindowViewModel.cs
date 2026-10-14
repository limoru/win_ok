using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Automation.Core.Configuration;
using Automation.Core.DeveloperTools;
using Automation.Core.Frames;
using Automation.Core.Runtime;
using Automation.Core.Sessions;
using Automation.Core.Targeting;
using Automation.Core.Vision;
using Automation.Targets.Windows;
using Kes.Gui.DeveloperTools;
using Kes.Gui.ViewModels.Tasks;
using Kes.Tasks;
using Kes.Tasks.Configuration;

namespace Kes.Gui.ViewModels;

public enum TargetProfile
{
    All,
    Pc,
    Emulator
}

public enum CaptureBackendPreference
{
    Auto,
    WindowsGraphicsCapture,
    Compatibility
}

public enum InputBackendPreference
{
    Auto,
    PostMessage,
    ForegroundSendInput
}

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(2);
    private static readonly string RuntimeLogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private static readonly string RuntimeLogPath = Path.Combine(RuntimeLogDirectory, "runtime.log");
    private static readonly object RuntimeLogGate = new();
    private static readonly System.Text.UTF8Encoding RuntimeLogEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly WindowsTargetProvider _windowsProvider = new();
    private readonly RelayCommand _addSelectedTargetCommand;
    private readonly AsyncRelayCommand _removeSelectedSessionCommand;
    private readonly SemaphoreSlim _targetRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _monitorCancellation = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, bool> _targetSelectionStates = new(StringComparer.Ordinal);
    private int _nextSessionSequence;
    private TargetCandidateViewModel? _selectedTarget;
    private SessionViewModel? _selectedSession;
    private TargetProfile _selectedTargetProfile = TargetProfile.All;
    private CaptureBackendPreference _captureBackendPreference = CaptureBackendPreference.Auto;
    private InputBackendPreference _inputBackendPreference = InputBackendPreference.Auto;
    private Task? _monitorTask;
    private string? _lastDiscoverySignature;
    private string _statusText = "准备就绪";
    private string _targetSearchText = string.Empty;
    private string _monitorStatusText = "等待首次检查";
    private string _lastTargetCheckText = "尚未检查";
    private string _developerPreviewStatusText = "等待截图";
    private string _developerFrameInfoText = "未获取帧";
    private string _developerPointerTargetText = "--";
    private string _developerPointerRelativeText = "--";
    private string _developerPointerSurfaceText = "--";
    private string _developerTemplatePath = string.Empty;
    private string _developerTemplateThresholdText = "0.80";
    private string _developerMatchResultText = "尚未执行模板匹配";
    private string _decisionSnapshotStatusText = "自动捕获未启用";
    private ImageSource? _developerPreviewImage;
    private BgraFrame? _developerFrameData;
    private int _developerFrameWidth;
    private int _developerFrameHeight;
    private long _developerFrameSequence;
    private DateTimeOffset _developerFrameCapturedAt = DateTimeOffset.Now;
    private int _selectedPageIndex;
    private bool _isBusy;
    private bool _initialized;
    private bool _disposed;

    public MainWindowViewModel()
    {
        _addSelectedTargetCommand = new RelayCommand(
            AddSelectedTarget,
            () => SelectedTargetCount > 0);

        _removeSelectedSessionCommand = new AsyncRelayCommand(
            RemoveSelectedSessionAsync,
            parameter => parameter is SessionViewModel || SelectedSession is not null);

        NavigateCommand = new RelayCommand(Navigate);
        SelectTargetProfileCommand = new RelayCommand(SelectTargetProfile);
        RefreshTargetsCommand = new AsyncRelayCommand(
            () => RefreshTargetsAsync(false),
            () => !IsBusy);
        AddSelectedTargetCommand = _addSelectedTargetCommand;
        RemoveSelectedSessionCommand = _removeSelectedSessionCommand;
        StartAllCommand = new AsyncRelayCommand(StartAllSessionsAsync, () => Sessions.Count > 0);
        PauseAllCommand = new RelayCommand(PauseAllSessions, () => Sessions.Count > 0);
        StartSessionCommand = new AsyncRelayCommand(
            parameter => RunSessionCommandAsync(parameter, session => session.StartAsync(), "已启动"),
            CanRunSessionCommand);
        PauseSessionCommand = new AsyncRelayCommand(
            parameter => RunSessionCommandAsync(
                parameter,
                session =>
                {
                    session.Pause();
                    return Task.CompletedTask;
                },
                "已暂停"),
            CanRunSessionCommand);
        StopSessionCommand = new AsyncRelayCommand(
            parameter => RunSessionCommandAsync(parameter, session => session.StopAsync(), "已停止"),
            CanRunSessionCommand);
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        OpenDiagnosticsFolderCommand = new RelayCommand(OpenDiagnosticsFolder);
        ClearLogsCommand = new RelayCommand(Logs.Clear);
        CaptureDeveloperFrameCommand = new AsyncRelayCommand(CaptureDeveloperFrameAsync);
        SaveDeveloperFrameCommand = new RelayCommand(SaveDeveloperFrame);
        BrowseDeveloperTemplateCommand = new RelayCommand(BrowseDeveloperTemplate);
        RunDeveloperTemplateMatchCommand = new RelayCommand(RunDeveloperTemplateMatch);
        OpenDecisionSnapshotFolderCommand = new RelayCommand(OpenDecisionSnapshotFolder);
        DecisionSnapshots = new DecisionSnapshotService(GetDecisionSnapshotDirectory(), _dispatcher);
        DecisionSnapshots.SnapshotSaved += OnDecisionSnapshotSaved;
        ConfigProfileStore.Load();
        try
        {
            if (ConfigProfileStore.Profiles.Count == 0)
            {
                ConfigProfileStore.Save("ChaosMode", ChaosModeSettings.DefaultProfileName, ChaosModeSettings.Snapshot);
            }
            else
            {
                var legacyDefault = ConfigProfileStore.Profiles.FirstOrDefault(profile => profile.Name == "默认配置");
                if (legacyDefault is not null)
                {
                    ConfigProfileStore.Save(
                        "ChaosMode",
                        ChaosModeSettings.DefaultProfileName,
                        legacyDefault.Values,
                        legacyDefault.Id);
                }
            }
        }
        catch
        {
            // Keep the in-memory default when the profile file cannot be written.
        }

        TaskCatalogItems = TaskModuleCatalog.Instance.GetDescriptors()
            .Where(descriptor => descriptor.Kind != TaskModuleKind.DeveloperDiagnostic)
            .OrderBy(descriptor => descriptor.Order)
            .Select(descriptor => new TaskModuleItemViewModel(descriptor))
            .ToArray();

        AppendLog("system", "控制台已启动，正在监视 PC 版与模拟器目标。");
    }

    public ObservableCollection<TargetCandidateViewModel> DiscoveredTargets { get; } = [];
    public ObservableCollection<TargetCandidateViewModel> FilteredTargets { get; } = [];
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public ObservableCollection<ActivityLogEntry> Logs { get; } = [];
    public IReadOnlyList<TaskModuleItemViewModel> TaskCatalogItems { get; }
    public ChaosModeSettingsViewModel ChaosModeSettings { get; } = new();
    public TaskConfigProfileStore ConfigProfileStore { get; } = new(TaskConfigProfileStore.GetDefaultFilePath());

    public RelayCommand NavigateCommand { get; }
    public RelayCommand SelectTargetProfileCommand { get; }
    public AsyncRelayCommand RefreshTargetsCommand { get; }
    public RelayCommand AddSelectedTargetCommand { get; }
    public AsyncRelayCommand RemoveSelectedSessionCommand { get; }
    public AsyncRelayCommand StartAllCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public AsyncRelayCommand StartSessionCommand { get; }
    public AsyncRelayCommand PauseSessionCommand { get; }
    public AsyncRelayCommand StopSessionCommand { get; }
    public RelayCommand ExportDiagnosticsCommand { get; }
    public RelayCommand OpenDiagnosticsFolderCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public AsyncRelayCommand CaptureDeveloperFrameCommand { get; }
    public RelayCommand SaveDeveloperFrameCommand { get; }
    public RelayCommand BrowseDeveloperTemplateCommand { get; }
    public RelayCommand RunDeveloperTemplateMatchCommand { get; }
    public RelayCommand OpenDecisionSnapshotFolderCommand { get; }
    public DecisionSnapshotService DecisionSnapshots { get; }

    public TargetCandidateViewModel? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                _addSelectedTargetCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(DeveloperPreviewTargetText));
                OnPropertyChanged(nameof(DeveloperPreviewSizeText));
            }
        }
    }

    public SessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                _removeSelectedSessionCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(HasSelectedSession));
            }
        }
    }

    public bool HasSelectedSession => SelectedSession is not null;
    public bool HasSessions => Sessions.Count > 0;
    public bool HasNoSessions => !HasSessions;
    public bool HasNoFilteredTargets => FilteredTargets.Count == 0;
    public int SelectedTargetCount => FilteredTargets.Count(target => target.IsSelected);

    public TargetProfile SelectedTargetProfile
    {
        get => _selectedTargetProfile;
        set
        {
            if (SetProperty(ref _selectedTargetProfile, value))
            {
                OnPropertyChanged(nameof(IsAllTargetProfile));
                OnPropertyChanged(nameof(IsPcTargetProfile));
                OnPropertyChanged(nameof(IsEmulatorTargetProfile));
                OnPropertyChanged(nameof(SelectedTargetProfileLabel));
                ApplyTargetFilter();
            }
        }
    }

    public bool IsAllTargetProfile => SelectedTargetProfile == TargetProfile.All;
    public bool IsPcTargetProfile => SelectedTargetProfile == TargetProfile.Pc;
    public bool IsEmulatorTargetProfile => SelectedTargetProfile == TargetProfile.Emulator;
    public string SelectedTargetProfileLabel => SelectedTargetProfile switch
    {
        TargetProfile.All => "全部",
        TargetProfile.Emulator => "模拟器",
        _ => "Windows PC"
    };

    public CaptureBackendPreference CaptureBackendPreference
    {
        get => _captureBackendPreference;
        set
        {
            if (!SetProperty(ref _captureBackendPreference, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsCapturePreferenceAuto));
            OnPropertyChanged(nameof(IsCapturePreferenceWindowsGraphicsCapture));
            OnPropertyChanged(nameof(IsCapturePreferenceCompatibility));
        }
    }

    public bool IsCapturePreferenceAuto
    {
        get => CaptureBackendPreference == CaptureBackendPreference.Auto;
        set
        {
            if (value)
            {
                CaptureBackendPreference = CaptureBackendPreference.Auto;
            }
        }
    }

    public bool IsCapturePreferenceWindowsGraphicsCapture
    {
        get => CaptureBackendPreference == CaptureBackendPreference.WindowsGraphicsCapture;
        set
        {
            if (value)
            {
                CaptureBackendPreference = CaptureBackendPreference.WindowsGraphicsCapture;
            }
        }
    }

    public bool IsCapturePreferenceCompatibility
    {
        get => CaptureBackendPreference == CaptureBackendPreference.Compatibility;
        set
        {
            if (value)
            {
                CaptureBackendPreference = CaptureBackendPreference.Compatibility;
            }
        }
    }

    public InputBackendPreference InputBackendPreference
    {
        get => _inputBackendPreference;
        set
        {
            if (!SetProperty(ref _inputBackendPreference, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsInputPreferenceAuto));
            OnPropertyChanged(nameof(IsInputPreferencePostMessage));
            OnPropertyChanged(nameof(IsInputPreferenceForegroundSendInput));
        }
    }

    public bool IsInputPreferenceAuto
    {
        get => InputBackendPreference == InputBackendPreference.Auto;
        set
        {
            if (value)
            {
                InputBackendPreference = InputBackendPreference.Auto;
            }
        }
    }

    public bool IsInputPreferencePostMessage
    {
        get => InputBackendPreference == InputBackendPreference.PostMessage;
        set
        {
            if (value)
            {
                InputBackendPreference = InputBackendPreference.PostMessage;
            }
        }
    }

    public bool IsInputPreferenceForegroundSendInput
    {
        get => InputBackendPreference == InputBackendPreference.ForegroundSendInput;
        set
        {
            if (value)
            {
                InputBackendPreference = InputBackendPreference.ForegroundSendInput;
            }
        }
    }

    public string TargetSearchText
    {
        get => _targetSearchText;
        set
        {
            if (SetProperty(ref _targetSearchText, value))
            {
                ApplyTargetFilter();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string MonitorStatusText
    {
        get => _monitorStatusText;
        private set => SetProperty(ref _monitorStatusText, value);
    }

    public string LastTargetCheckText
    {
        get => _lastTargetCheckText;
        private set => SetProperty(ref _lastTargetCheckText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshTargetsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set
        {
            if (SetProperty(ref _selectedPageIndex, value))
            {
                OnPropertyChanged(nameof(IsCapturePage));
                OnPropertyChanged(nameof(IsTasksPage));
                OnPropertyChanged(nameof(IsLogsPage));
                OnPropertyChanged(nameof(IsDeveloperPage));
                OnPropertyChanged(nameof(IsSettingsPage));
                OnPropertyChanged(nameof(IsAboutPage));
            }
        }
    }

    public bool IsCapturePage => SelectedPageIndex == 0;
    public bool IsTasksPage => SelectedPageIndex == 1;
    public bool IsLogsPage => SelectedPageIndex == 2;
    public bool IsDeveloperPage => SelectedPageIndex == 3;
    public bool IsSettingsPage => SelectedPageIndex == 4;
    public bool IsAboutPage => SelectedPageIndex == 5;

    public string DeveloperPreviewTargetText => SelectedTarget?.Summary ?? "未选择目标";
    public string DeveloperPreviewSizeText => _developerFrameWidth > 0 && _developerFrameHeight > 0
        ? $"{_developerFrameWidth} x {_developerFrameHeight}"
        : SelectedTarget?.SizeText ?? "--";

    public ImageSource? DeveloperPreviewImage
    {
        get => _developerPreviewImage;
        private set
        {
            if (SetProperty(ref _developerPreviewImage, value))
            {
                OnPropertyChanged(nameof(HasDeveloperPreviewImage));
                OnPropertyChanged(nameof(HasNoDeveloperPreviewImage));
            }
        }
    }

    public bool HasDeveloperPreviewImage => DeveloperPreviewImage is not null;
    public bool HasNoDeveloperPreviewImage => !HasDeveloperPreviewImage;
    public BgraFrame? DeveloperFrameData => _developerFrameData;

    public string DeveloperPreviewStatusText
    {
        get => _developerPreviewStatusText;
        private set => SetProperty(ref _developerPreviewStatusText, value);
    }

    public string DeveloperFrameInfoText
    {
        get => _developerFrameInfoText;
        private set => SetProperty(ref _developerFrameInfoText, value);
    }

    public string DeveloperPointerTargetText
    {
        get => _developerPointerTargetText;
        private set => SetProperty(ref _developerPointerTargetText, value);
    }

    public string DeveloperPointerRelativeText
    {
        get => _developerPointerRelativeText;
        private set => SetProperty(ref _developerPointerRelativeText, value);
    }

    public string DeveloperPointerSurfaceText
    {
        get => _developerPointerSurfaceText;
        private set => SetProperty(ref _developerPointerSurfaceText, value);
    }

    public string DeveloperTemplatePath
    {
        get => _developerTemplatePath;
        private set => SetProperty(ref _developerTemplatePath, value);
    }

    public string DeveloperTemplateThresholdText
    {
        get => _developerTemplateThresholdText;
        set => SetProperty(ref _developerTemplateThresholdText, value);
    }

    public string DeveloperMatchResultText
    {
        get => _developerMatchResultText;
        private set => SetProperty(ref _developerMatchResultText, value);
    }

    public bool IsDecisionSnapshotCaptureEnabled
    {
        get => DecisionSnapshots.IsEnabled;
        set
        {
            if (DecisionSnapshots.IsEnabled == value)
            {
                return;
            }

            DecisionSnapshots.IsEnabled = value;
            OnPropertyChanged();
            DecisionSnapshotStatusText = value
                ? "自动捕获已启用，任务到关键判断点时保存一次样本。"
                : "自动捕获已暂停，仍可在 1:1 预览中手动生成样本。";
        }
    }

    public string DecisionSnapshotOutputDirectory => DecisionSnapshots.OutputDirectory;

    public string DecisionSnapshotStatusText
    {
        get => _decisionSnapshotStatusText;
        private set => SetProperty(ref _decisionSnapshotStatusText, value);
    }

    public int TargetCount => DiscoveredTargets.Count;
    public int FilteredTargetCount => FilteredTargets.Count;
    public int PcTargetCount => DiscoveredTargets.Count(target => !target.IsEmulator);
    public int EmulatorTargetCount => DiscoveredTargets.Count(target => target.IsEmulator);
    public int SessionCount => Sessions.Count;
    public int RunningSessionCount => Sessions.Count(session => session.State == SessionState.Running);

    public string PcTargetStatusText => PcTargetCount == 0
        ? "未检测到"
        : $"检测到 {PcTargetCount} 个";

    public string EmulatorTargetStatusText => EmulatorTargetCount == 0
        ? "未检测到"
        : $"检测到 {EmulatorTargetCount} 个";

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await RefreshTargetsAsync(false);
        _monitorTask = MonitorTargetsAsync(_monitorCancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DecisionSnapshots.SnapshotSaved -= OnDecisionSnapshotSaved;
        foreach (var session in Sessions)
        {
            session.ActivityReported -= OnSessionActivityReported;
            session.LogReported -= OnSessionLogReported;
            await session.DisposeAsync();
        }

        _monitorCancellation.Cancel();

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _monitorCancellation.Dispose();
        _targetRefreshGate.Dispose();
    }

    private async Task MonitorTargetsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MonitorInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshTargetsAsync(true, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Navigate(object? parameter)
    {
        if (parameter is int index)
        {
            SelectedPageIndex = Math.Clamp(index, 0, 5);
            return;
        }

        if (parameter is string text && int.TryParse(text, out index))
        {
            SelectedPageIndex = Math.Clamp(index, 0, 5);
        }
    }

    private void SelectTargetProfile(object? parameter)
    {
        var profile = parameter?.ToString() switch
        {
            "All" => TargetProfile.All,
            "Emulator" => TargetProfile.Emulator,
            "PC" => TargetProfile.Pc,
            _ => SelectedTargetProfile
        };

        if (profile == SelectedTargetProfile)
        {
            return;
        }

        SelectedTargetProfile = profile;
        SelectedTarget = null;
        ApplyTargetFilter();
        var profileLabel = profile switch
        {
            TargetProfile.All => "全部",
            TargetProfile.Emulator => "模拟器",
            _ => "PC 版"
        };
        AppendLog("monitor", $"切换到{profileLabel}目标。");
    }

    private async Task RefreshTargetsAsync(
        bool background,
        CancellationToken cancellationToken = default)
    {
        await _targetRefreshGate.WaitAsync(cancellationToken);

        try
        {
            if (!background)
            {
                IsBusy = true;
                StatusText = "正在检查 PC 版与模拟器目标...";
            }

            var candidates = await _windowsProvider.DiscoverAsync(cancellationToken);
            var previouslySelected = SelectedTarget?.Binding.ProviderInstanceId;

            DiscoveredTargets.Clear();
            foreach (var candidate in candidates)
            {
                var target = new TargetCandidateViewModel(
                    candidate,
                    !_targetSelectionStates.TryGetValue(candidate.Binding.ProviderInstanceId, out var isSelected) || isSelected);
                target.PropertyChanged += OnTargetCandidatePropertyChanged;
                DiscoveredTargets.Add(target);
            }

            ApplyTargetFilter(previouslySelected);
            AddDiscoveredTargets();
            NotifyTargetCollectionChanged();
            UpdateMonitorSnapshot(candidates, background);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MonitorStatusText = "监视异常";
            LastTargetCheckText = $"检查失败 · {DateTime.Now:HH:mm:ss}";

            if (!background)
            {
                StatusText = "目标检查失败";
                AppendLog("error", exception.Message);
            }
        }
        finally
        {
            if (!background)
            {
                IsBusy = false;
            }

            _targetRefreshGate.Release();
        }
    }

    private void UpdateMonitorSnapshot(
        IReadOnlyList<TargetCandidate> candidates,
        bool background)
    {
        var now = DateTimeOffset.Now;
        LastTargetCheckText = $"上次检查 {now:HH:mm:ss}";
        MonitorStatusText = $"监视中 · 每 {MonitorInterval.TotalSeconds:0} 秒";

        var signature = string.Join(
            "|",
            candidates
                .OrderBy(candidate => candidate.Binding.ProviderInstanceId, StringComparer.Ordinal)
                .Select(candidate => candidate.Binding.ProviderInstanceId));

        if (_lastDiscoverySignature != signature)
        {
            _lastDiscoverySignature = signature;
            AppendLog(
                "monitor",
                $"目标状态更新：PC 版 {PcTargetCount} 个，模拟器 {EmulatorTargetCount} 个。");
        }

        if (!background)
        {
            StatusText = $"PC 版 {PcTargetCount} 个 · 模拟器 {EmulatorTargetCount} 个";
        }
    }

    private void ApplyTargetFilter(string? preferredSelectionId = null)
    {
        var selectionId = preferredSelectionId
            ?? SelectedTarget?.Binding.ProviderInstanceId;
        var query = TargetSearchText.Trim();

        FilteredTargets.Clear();
        foreach (var target in DiscoveredTargets)
        {
            var matchesProfile = SelectedTargetProfile switch
            {
                TargetProfile.All => true,
                TargetProfile.Emulator => target.IsEmulator,
                _ => !target.IsEmulator
            };

            if (!matchesProfile)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(query)
                || target.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || target.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || target.ProcessPath.Contains(query, StringComparison.OrdinalIgnoreCase)
                || target.WindowClass.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredTargets.Add(target);
            }
        }

        SelectedTarget = selectionId is null
            ? FilteredTargets.FirstOrDefault()
            : FilteredTargets.FirstOrDefault(
                item => item.Binding.ProviderInstanceId == selectionId)
              ?? FilteredTargets.FirstOrDefault();

        OnPropertyChanged(nameof(FilteredTargetCount));
        OnPropertyChanged(nameof(SelectedTargetCount));
        OnPropertyChanged(nameof(HasNoFilteredTargets));
        _addSelectedTargetCommand.RaiseCanExecuteChanged();
    }

    private ITargetProvider CreateSessionProvider()
    {
        var options = new WindowsAutomationOptions
        {
            CaptureBackend = CaptureBackendPreference switch
            {
                CaptureBackendPreference.WindowsGraphicsCapture => WindowsCaptureBackend.WindowsGraphicsCapture,
                CaptureBackendPreference.Compatibility => WindowsCaptureBackend.Compatibility,
                _ => WindowsCaptureBackend.Auto
            },
            InputBackend = InputBackendPreference switch
            {
                InputBackendPreference.PostMessage => WindowsInputBackend.PostMessage,
                InputBackendPreference.ForegroundSendInput => WindowsInputBackend.ForegroundSendInput,
                _ => WindowsInputBackend.Auto
            }
        };

        return new WindowsTargetProvider(options);
    }
    private void AddDiscoveredTargets()
    {
        var added = 0;
        foreach (var target in DiscoveredTargets)
        {
            if (Sessions.Any(item => item.Binding.ProviderInstanceId == target.Binding.ProviderInstanceId))
            {
                continue;
            }

            if (!TryAddSession(target, out var session))
            {
                continue;
            }

            added++;
            SelectedSession ??= session;
        }

        if (added > 0)
        {
            AppendLog("session", $"自动加载实例：新增 {added} 个。");
            NotifySessionCollectionChanged();
        }
    }

    private void AddSelectedTarget()
    {
        var candidates = FilteredTargets
            .Where(target => target.IsSelected)
            .ToArray();
        if (candidates.Length == 0)
        {
            StatusText = "没有选中的目标";
            return;
        }

        var added = 0;
        var skipped = 0;
        foreach (var candidate in candidates)
        {
            if (!TryAddSession(candidate, out var session))
            {
                skipped++;
                continue;
            }

            added++;
            SelectedSession = session;
        }

        NotifySessionCollectionChanged();
        StatusText = added == 0
            ? "选中的目标已经全部添加"
            : skipped == 0
                ? $"已添加 {added} 个会话"
                : $"已添加 {added} 个会话，跳过 {skipped} 个已存在目标";
        AppendLog("session", $"批量添加目标：成功 {added} 个，跳过 {skipped} 个。");
    }

    private bool TryAddSession(TargetCandidateViewModel candidate, out SessionViewModel session)
    {
        session = null!;
        if (Sessions.Any(
                item => item.Binding.ProviderInstanceId == candidate.Binding.ProviderInstanceId))
        {
            AppendLog("session", $"目标已存在：{candidate.DisplayName}");
            return false;
        }

        var accountLabel = $"账号 {Sessions.Count + 1}";
        var sessionId = $"{(candidate.IsEmulator ? "MUMU" : "WIN")}-{++_nextSessionSequence:00}";
        session = new SessionViewModel(
            sessionId,
            candidate.Binding,
            candidate.IsEmulator ? UiProfile.AndroidMobile : UiProfile.WindowsPc,
            accountLabel,
            CreateSessionProvider,
            ConfigProfileStore,
            decisionSnapshots: DecisionSnapshots);

        session.ActivityReported += OnSessionActivityReported;
        session.LogReported += OnSessionLogReported;
        session.UpdateMessage($"已绑定：{candidate.DisplayName}");
        Sessions.Add(session);
        AppendLog("session", $"{sessionId} <- {candidate.DisplayName}");
        return true;
    }

    private async Task RemoveSelectedSessionAsync(object? parameter)
    {
        var session = parameter as SessionViewModel ?? SelectedSession;
        if (session is null)
        {
            return;
        }

        session.ActivityReported -= OnSessionActivityReported;
        session.LogReported -= OnSessionLogReported;
        await session.DisposeAsync();
        Sessions.Remove(session);
        SelectedSession = Sessions.FirstOrDefault();
        NotifySessionCollectionChanged();
        AppendLog("session", $"已移除 {session.SessionId}");
    }

    private void OnDecisionSnapshotSaved(
        object? sender,
        DecisionSnapshotSavedEventArgs eventArgs)
    {
        DecisionSnapshotStatusText = $"样本已保存：{eventArgs.Request.PageLabel}";
        AppendLog(
            "developer",
            $"判断样本已保存：{eventArgs.Request.PageLabel}（{eventArgs.Request.ReasonId}）→ {eventArgs.Directory}");
    }

    private void OnSessionLogReported(object? sender, SessionLogChangedEventArgs eventArgs)
    {
        if (sender is not SessionViewModel session)
        {
            return;
        }

        AppendLog(
            eventArgs.Source,
            $"{session.SessionId} · {eventArgs.Message}",
            eventArgs.Tag);
    }
    private void OnSessionActivityReported(object? sender, SessionActivityChangedEventArgs eventArgs)
    {
        if (sender is not SessionViewModel session)
        {
            return;
        }

        var activity = eventArgs.Activity;
        var progress = activity.HasProgress ? $" · {activity.ProgressText}" : string.Empty;
        var tag = activity.HasProgress ? "progress" : "activity";
        AppendLog(
            "activity",
            $"{session.SessionId} · {activity.TaskLabel} · {activity.StepLabel}{progress} · {activity.Detail}",
            tag);

        if (ReferenceEquals(SelectedSession, session))
        {
            StatusText = $"{activity.TaskLabel} · {activity.StepLabel}";
        }
    }

    private async Task StartAllSessionsAsync()
    {
        foreach (var session in Sessions)
        {
            await session.StartAsync();
        }

        RefreshSessionCounters();
        StatusText = $"已处理 {Sessions.Count} 个会话";
        AppendLog("session", "全部会话已完成启动检查。");
    }

    private void PauseAllSessions()
    {
        foreach (var session in Sessions)
        {
            session.Pause();
        }

        RefreshSessionCounters();
        StatusText = "全部会话已暂停";
        AppendLog("session", "全部会话已暂停。");
    }

    private async Task RunSessionCommandAsync(
        object? parameter,
        Func<SessionViewModel, Task> action,
        string actionLabel)
    {
        var session = parameter as SessionViewModel ?? SelectedSession;
        if (session is null)
        {
            return;
        }

        SelectedSession = session;
        await action(session);
        RefreshSessionCounters();
        StatusText = $"{session.SessionId} {actionLabel}";
        AppendLog("session", $"{session.SessionId} {actionLabel}。");
    }

    private bool CanRunSessionCommand(object? parameter)
        => parameter is SessionViewModel || SelectedSession is not null;

    public void UpdateDeveloperPointer(double surfaceX, double surfaceY, double surfaceWidth, double surfaceHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            ClearDeveloperPointer();
            return;
        }

        var imageWidth = _developerFrameWidth > 0
            ? _developerFrameWidth
            : SelectedTarget?.Candidate.ClientWidth ?? (int)surfaceWidth;
        var imageHeight = _developerFrameHeight > 0
            ? _developerFrameHeight
            : SelectedTarget?.Candidate.ClientHeight ?? (int)surfaceHeight;
        var scale = Math.Min(surfaceWidth / imageWidth, surfaceHeight / imageHeight);
        var displayedWidth = imageWidth * scale;
        var displayedHeight = imageHeight * scale;
        var offsetX = (surfaceWidth - displayedWidth) / 2;
        var offsetY = (surfaceHeight - displayedHeight) / 2;

        DeveloperPointerSurfaceText = FormattableString.Invariant($"{surfaceX:0}, {surfaceY:0}");
        if (surfaceX < offsetX
            || surfaceY < offsetY
            || surfaceX > offsetX + displayedWidth
            || surfaceY > offsetY + displayedHeight)
        {
            DeveloperPointerTargetText = "--";
            DeveloperPointerRelativeText = "--";
            return;
        }

        var relativeX = Math.Clamp((surfaceX - offsetX) / displayedWidth, 0, 1);
        var relativeY = Math.Clamp((surfaceY - offsetY) / displayedHeight, 0, 1);
        DeveloperPointerRelativeText = FormattableString.Invariant($"{relativeX:0.0000}, {relativeY:0.0000}");

        var targetX = (int)Math.Round(relativeX * imageWidth);
        var targetY = (int)Math.Round(relativeY * imageHeight);
        DeveloperPointerTargetText = FormattableString.Invariant($"{targetX}, {targetY}");
    }

    public void ClearDeveloperPointer()
    {
        DeveloperPointerTargetText = "--";
        DeveloperPointerRelativeText = "--";
        DeveloperPointerSurfaceText = "--";
    }

    public async ValueTask<string?> CaptureDeveloperDecisionSnapshotAsync(
        string reasonId,
        OcrFrameResult? ocr,
        CancellationToken cancellationToken = default)
    {
        var frame = _developerFrameData;
        if (frame is null)
        {
            DecisionSnapshotStatusText = "没有可用于标注的截图。";
            return null;
        }

        var definition = DecisionSnapshotCatalog.Resolve(reasonId);
        var sessionId = SelectedSession?.SessionId
                        ?? SelectedTarget?.Binding.ProviderInstanceId
                        ?? "developer";
        var request = definition.CreateRequest(
            sessionId,
            _developerFrameSequence,
            _developerFrameCapturedAt);

        try
        {
            var directory = await DecisionSnapshots.CaptureAsync(
                    request,
                    frame,
                    ocr,
                    force: true,
                    cancellationToken)
                .ConfigureAwait(true);
            if (directory is null)
            {
                DecisionSnapshotStatusText = "样本未保存。";
                return null;
            }

            DecisionSnapshotStatusText = $"样本已保存：{Path.GetFileName(directory)}";
            return directory;
        }
        catch (Exception exception)
        {
            DecisionSnapshotStatusText = $"样本保存失败：{exception.Message}";
            AppendLog("developer", $"样本保存失败：{exception.Message}");
            return null;
        }
    }

    private async Task CaptureDeveloperFrameAsync()
    {
        if (SelectedTarget is null)
        {
            DeveloperPreviewStatusText = "未选择目标";
            DeveloperFrameInfoText = "请先在捕获页选择窗口";
            return;
        }

        if (!_windowsProvider.TryResolveWindowHandle(SelectedTarget.Binding, out var windowHandle))
        {
            DeveloperPreviewStatusText = "未找到目标窗口";
            DeveloperFrameInfoText = SelectedTarget.ProcessName;
            AppendLog("developer", $"截图失败：未找到窗口 {SelectedTarget.DisplayName}");
            return;
        }

        DeveloperPreviewStatusText = "正在截图...";
        try
        {
            var frame = await Task.Run(() => WindowsWindowCapture.CaptureClient(windowHandle));
            var image = BitmapSource.Create(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                frame.BgraPixels,
                frame.Width * 4);
            image.Freeze();

            _developerFrameWidth = frame.Width;
            _developerFrameHeight = frame.Height;
            _developerFrameSequence = 0;
            _developerFrameCapturedAt = DateTimeOffset.Now;
            _developerFrameData = new BgraFrame(frame.Width, frame.Height, frame.BgraPixels);
            DeveloperPreviewImage = image;
            DeveloperPreviewStatusText = "截图已更新";
            DeveloperFrameInfoText = $"{frame.Width} x {frame.Height} · {frame.Method} · {DateTime.Now:HH:mm:ss}";
            OnPropertyChanged(nameof(DeveloperPreviewSizeText));
            OnPropertyChanged(nameof(DeveloperFrameData));
            ClearDeveloperPointer();
            AppendLog("developer", $"截图完成：{SelectedTarget.DisplayName} · {frame.Width}x{frame.Height} · {frame.Method}");
        }
        catch (Exception exception)
        {
            DeveloperPreviewStatusText = "截图失败";
            DeveloperFrameInfoText = exception.Message;
            AppendLog("error", $"截图失败：{exception.Message}");
        }
    }

    private void SaveDeveloperFrame()
    {
        if (DeveloperPreviewImage is not BitmapSource image)
        {
            DeveloperPreviewStatusText = "没有可保存的帧";
            AppendLog("developer", "保存截图失败：当前尚未获取到帧。");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
        DeveloperPreviewStatusText = "截图已保存";
        AppendLog("developer", $"截图已保存：{dialog.FileName}");
    }

    private void BrowseDeveloperTemplate()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择模板图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            DeveloperTemplatePath = dialog.FileName;
            DeveloperMatchResultText = "已选择模板，等待执行匹配";
        }
    }

    private void RunDeveloperTemplateMatch()
    {
        if (string.IsNullOrWhiteSpace(DeveloperTemplatePath))
        {
            DeveloperMatchResultText = "请先选择模板图片";
            return;
        }

        DeveloperMatchResultText = "OpenCV 模板匹配后端尚未接入";
        AppendLog("developer", $"已请求匹配模板：{Path.GetFileName(DeveloperTemplatePath)}");
    }

    private void ExportDiagnostics()
    {
        try
        {
            var path = CreateDiagnosticsFile();
            StatusText = $"诊断已导出：{Path.GetFileName(path)}";
            AppendLog("diagnostics", path);
        }
        catch (Exception exception)
        {
            StatusText = "诊断导出失败";
            AppendLog("error", exception.Message);
        }
    }

    private void OpenDiagnosticsFolder()
    {
        try
        {
            var directory = GetDiagnosticsDirectory();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusText = "无法打开诊断目录";
            AppendLog("error", exception.Message);
        }
    }

    private void OpenDecisionSnapshotFolder()
    {
        try
        {
            Directory.CreateDirectory(DecisionSnapshots.OutputDirectory);
            Process.Start(new ProcessStartInfo(DecisionSnapshots.OutputDirectory)
            {
                UseShellExecute = true
            });
            DecisionSnapshotStatusText = "已打开判断样本目录。";
        }
        catch (Exception exception)
        {
            DecisionSnapshotStatusText = $"无法打开样本目录：{exception.Message}";
            AppendLog("developer", $"无法打开样本目录：{exception.Message}");
        }
    }

    private string CreateDiagnosticsFile()
    {
        var directory = GetDiagnosticsDirectory();
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var payload = new
        {
            exportedAt = DateTimeOffset.Now,
            monitor = new
            {
                PcTargetCount,
                EmulatorTargetCount,
                MonitorStatusText,
                LastTargetCheckText
            },
            targets = DiscoveredTargets.Select(item => new
            {
                item.ProfileLabel,
                item.DisplayName,
                item.ProcessName,
                item.ProcessPath,
                item.WindowClass,
                item.WindowTitle,
                item.SizeText,
                item.StateLabel,
                binding = item.Binding
            }),
            sessions = Sessions.Select(session => new
            {
                session.SessionId,
                session.TargetTypeLabel,
                session.UiProfileLabel,
                session.AccountLabel,
                session.StateLabel,
                session.CurrentModule,
                session.LastMessage,
                session.FrameSequence,
                session.OcrMilliseconds,
                session.InputFailureCount,
                activity = new
                {
                    session.Activity.TaskId,
                    session.Activity.TaskLabel,
                    session.Activity.StepId,
                    session.Activity.StepLabel,
                    session.Activity.Detail,
                    session.Activity.Current,
                    session.Activity.Total,
                    session.Activity.UpdatedAt
                },
                binding = session.Binding
            })
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        return path;
    }

    private static string GetDiagnosticsDirectory()
        => Path.Combine(AppContext.BaseDirectory, "diagnostics");

    private static string GetDecisionSnapshotDirectory()
        => Path.Combine(AppContext.BaseDirectory, "developer-annotations");

    private void OnTargetCandidatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not TargetCandidateViewModel target
            || e.PropertyName != nameof(TargetCandidateViewModel.IsSelected))
        {
            return;
        }

        _targetSelectionStates[target.Binding.ProviderInstanceId] = target.IsSelected;
        OnPropertyChanged(nameof(SelectedTargetCount));
        _addSelectedTargetCommand.RaiseCanExecuteChanged();
    }

    private void NotifyTargetCollectionChanged()
    {
        OnPropertyChanged(nameof(TargetCount));
        OnPropertyChanged(nameof(FilteredTargetCount));
        OnPropertyChanged(nameof(SelectedTargetCount));
        _addSelectedTargetCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(PcTargetCount));
        OnPropertyChanged(nameof(EmulatorTargetCount));
        OnPropertyChanged(nameof(PcTargetStatusText));
        OnPropertyChanged(nameof(EmulatorTargetStatusText));
        OnPropertyChanged(nameof(HasNoFilteredTargets));
    }

    private void NotifySessionCollectionChanged()
    {
        OnPropertyChanged(nameof(SessionCount));
        OnPropertyChanged(nameof(RunningSessionCount));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasNoSessions));
        StartAllCommand.RaiseCanExecuteChanged();
        PauseAllCommand.RaiseCanExecuteChanged();
    }

    private void RefreshSessionCounters()
    {
        OnPropertyChanged(nameof(RunningSessionCount));
    }

    private void AppendLog(string source, string message, string? tag = null)
    {
        WriteRuntimeLog(message);
        var entry = new ActivityLogEntry(DateTimeOffset.Now, source, message, tag);

        if (_dispatcher.CheckAccess())
        {
            InsertLogEntry(entry);
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => InsertLogEntry(entry)));
    }

    private void InsertLogEntry(ActivityLogEntry entry)
    {
        Logs.Insert(0, entry);
        while (Logs.Count > 500)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    private static void WriteRuntimeLog(string message)
    {
        try
        {
            Directory.CreateDirectory(RuntimeLogDirectory);
            var normalizedMessage = message.Replace('\r', ' ').Replace('\n', ' ');
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}\t{normalizedMessage}{Environment.NewLine}";
            lock (RuntimeLogGate)
            {
                File.AppendAllText(RuntimeLogPath, line, RuntimeLogEncoding);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Debug.WriteLine($"Failed to write runtime log: {exception.Message}");
        }
    }
}
