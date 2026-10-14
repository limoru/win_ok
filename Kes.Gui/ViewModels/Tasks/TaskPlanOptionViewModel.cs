using Automation.Core.Runtime;

namespace Kes.Gui.ViewModels.Tasks;

public sealed class TaskPlanOptionViewModel
{
    public TaskPlanOptionViewModel(
        string displayName,
        string description,
        SessionTaskPlan plan,
        TaskModuleDescriptor? descriptor = null)
    {
        DisplayName = displayName;
        Description = description;
        Plan = plan;
        Descriptor = descriptor;
    }

    public string DisplayName { get; }
    public string Description { get; }
    public SessionTaskPlan Plan { get; }
    public TaskModuleDescriptor? Descriptor { get; }
    public string AvailabilityLabel => Descriptor?.Availability switch
    {
        TaskModuleAvailability.Available => "可运行",
        TaskModuleAvailability.Planned => "待实现",
        _ => "仅连接"
    };

    public override string ToString() => DisplayName;
}