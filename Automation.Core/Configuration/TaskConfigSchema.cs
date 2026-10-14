namespace Automation.Core.Configuration;

public sealed record TaskConfigSchema(
    string ModuleId,
    string DisplayName,
    string Description,
    IReadOnlyList<TaskConfigGroupDescriptor> Groups)
{
    public IEnumerable<TaskConfigOptionDescriptor> Options
        => Groups.SelectMany(group => group.Options);
}