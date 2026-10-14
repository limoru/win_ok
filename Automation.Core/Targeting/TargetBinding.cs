namespace Automation.Core.Targeting;

public sealed record TargetBinding
{
    public required TargetKind Kind { get; init; }
    public required string ProviderInstallationId { get; init; }
    public required string ProviderInstanceId { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool ManualPin { get; init; }
}