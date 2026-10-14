namespace Automation.Targets.Windows;

public enum WindowsCaptureBackend
{
    Auto,
    WindowsGraphicsCapture,
    Compatibility
}

public enum WindowsInputBackend
{
    Auto,
    PostMessage,
    ForegroundSendInput
}

public sealed record WindowsAutomationOptions
{
    public WindowsCaptureBackend CaptureBackend { get; init; } = WindowsCaptureBackend.Auto;
    public WindowsInputBackend InputBackend { get; init; } = WindowsInputBackend.Auto;
    public TimeSpan CaptureInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(3);

    internal void Validate()
    {
        if (CaptureInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CaptureInterval),
                CaptureInterval,
                "The capture interval must be greater than zero.");
        }

        if (FirstFrameTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FirstFrameTimeout),
                FirstFrameTimeout,
                "The first-frame timeout must be greater than zero.");
        }
    }
}