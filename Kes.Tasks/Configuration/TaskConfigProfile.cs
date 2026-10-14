using Automation.Core.Vision;

namespace Kes.Tasks.Configuration;

public sealed class TaskConfigProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ModuleId { get; init; } = string.Empty;
    public Dictionary<string, object> Values { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<RuntimeFeatureTemplate>> RuntimeTemplates { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}