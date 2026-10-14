namespace Automation.Core.Configuration;

public sealed record TaskConfigGroupDescriptor(
    string DisplayName,
    IReadOnlyList<TaskConfigOptionDescriptor> Options,
    bool IsCollapsible = true,
    bool IsInitiallyExpanded = true);