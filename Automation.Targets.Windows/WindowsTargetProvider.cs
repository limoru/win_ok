using Automation.Core.Targeting;

namespace Automation.Targets.Windows;

public sealed class WindowsTargetProvider : ITargetProvider
{
    private readonly WindowsAutomationOptions _options;

    public WindowsTargetProvider()
        : this(new WindowsAutomationOptions())
    {
    }

    public WindowsTargetProvider(WindowsAutomationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public string ProviderId => "windows";
    public TargetKind TargetKind => TargetKind.WindowsWindow;

    public ValueTask<IReadOnlyList<TargetCandidate>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return new ValueTask<IReadOnlyList<TargetCandidate>>(
            Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return WindowsTargetDiscovery.Discover();
                },
                cancellationToken));
    }

    public bool TryResolveWindowHandle(TargetBinding binding, out nint windowHandle)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return WindowsTargetDiscovery.TryResolveWindowHandle(binding, out windowHandle);
    }

    public ValueTask<ITargetSession> OpenAsync(
        TargetBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();

        if (binding.Kind is not (TargetKind.WindowsWindow or TargetKind.MuMuEmulator))
        {
            throw new ArgumentException("The binding is not a supported Windows-hosted target.", nameof(binding));
        }

        return ValueTask.FromResult<ITargetSession>(new WindowsTargetSession(binding, _options));
    }
}