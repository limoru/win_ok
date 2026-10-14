namespace Automation.Core.Targeting;

[Flags]
public enum TargetCapabilities
{
    None = 0,
    BackgroundCapture = 1 << 0,
    BackgroundInput = 1 << 1,
    ForegroundInput = 1 << 2,
    Pointer = 1 << 3,
    Touch = 1 << 4,
    Keyboard = 1 << 5,
    TextInput = 1 << 6,
    Scroll = 1 << 7
}