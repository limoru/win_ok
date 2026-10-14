using Automation.Core.Configuration;

namespace Automation.Core.Runtime;

public sealed record TaskModuleDescriptor(
    string ModuleId,
    string DisplayName,
    string Description,
    TaskModuleKind Kind,
    TaskModuleAvailability Availability,
    IReadOnlyList<UiProfile> SupportedProfiles,
    int Order,
    string ConfigViewId,
    string? MutualExclusionGroup)
{
    public bool Supports(UiProfile uiProfile) => SupportedProfiles.Contains(uiProfile);
}