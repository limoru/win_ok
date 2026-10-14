namespace Automation.Core.Targeting;

public sealed record TargetCandidate(
    TargetBinding Binding,
    string DisplayName,
    string ProcessPath,
    string WindowClass,
    string WindowTitle,
    int ClientWidth,
    int ClientHeight,
    bool IsAvailable)
{
    public string ProcessName => string.IsNullOrWhiteSpace(ProcessPath)
        ? "unknown"
        : Path.GetFileNameWithoutExtension(ProcessPath);

    public string Summary => $"{ProcessName}  |  {ClientWidth}x{ClientHeight}  |  {WindowClass}";
}