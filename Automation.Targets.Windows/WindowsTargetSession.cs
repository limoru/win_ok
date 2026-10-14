using Automation.Core.Frames;
using Automation.Core.Targeting;

namespace Automation.Targets.Windows;

public sealed class WindowsTargetSession : ITargetSession
{
    private readonly WindowsFrameSource _frames;
    private readonly WindowsInputDispatcher _input;
    private readonly nint _fixedWindowHandle;
    private bool _disposed;

    public WindowsTargetSession(TargetBinding binding)
        : this(binding, new WindowsAutomationOptions())
    {
    }

    public WindowsTargetSession(
        TargetBinding binding,
        WindowsAutomationOptions options)
        : this(binding, options, 0)
    {
    }

    public WindowsTargetSession(
        TargetBinding binding,
        nint windowHandle)
        : this(binding, new WindowsAutomationOptions(), windowHandle)
    {
    }

    public WindowsTargetSession(
        TargetBinding binding,
        WindowsAutomationOptions options,
        nint windowHandle)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Binding = binding;
        Options = options;
        _fixedWindowHandle = windowHandle;
        _frames = new WindowsFrameSource(ResolveWindowHandle, options);
        _input = new WindowsInputDispatcher(ResolveWindowHandle, options.InputBackend);
    }

    public TargetBinding Binding { get; }
    public WindowsAutomationOptions Options { get; }

    public TargetCapabilities Capabilities =>
        TargetCapabilities.BackgroundCapture
        | TargetCapabilities.BackgroundInput
        | TargetCapabilities.ForegroundInput
        | TargetCapabilities.Pointer
        | TargetCapabilities.Keyboard
        | TargetCapabilities.TextInput;

    public IFrameSource Frames => _frames;
    public IInputSink Input => _input;
    public string? LastError => _frames.LastError;
    public string? LastCaptureError => LastError;
    public WindowsCaptureMethod? LastCaptureMethod => _frames.LastCaptureMethod;

    private nint ResolveWindowHandle()
    {
        if (_fixedWindowHandle != 0)
        {
            return _fixedWindowHandle;
        }

        return WindowsTargetDiscovery.TryResolveWindowHandle(Binding, out var windowHandle)
            ? windowHandle
            : 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _frames.DisposeAsync().ConfigureAwait(false);
    }
}