namespace Kes.Gui.ViewModels;

public sealed record ActivityLogEntry(
    DateTimeOffset Timestamp,
    string Source,
    string Message,
    string? Tag = null)
{
    public string TimestampText => Timestamp.ToString("HH:mm:ss");
    public bool HasTag => !string.IsNullOrWhiteSpace(Tag);
    public string TagLabel => Tag?.ToUpperInvariant() ?? string.Empty;
}