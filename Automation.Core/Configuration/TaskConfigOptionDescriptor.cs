namespace Automation.Core.Configuration;

public sealed record TaskConfigOptionDescriptor(
    string Key,
    string DisplayName,
    TaskConfigValueKind Kind,
    object DefaultValue,
    string Description = "",
    IReadOnlyList<string>? Choices = null,
    int? Minimum = null,
    int? Maximum = null);