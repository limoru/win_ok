using Automation.Core.Runtime;

namespace Kes.Gui.ViewModels.Tasks;

public sealed class TaskModuleItemViewModel
{
    public TaskModuleItemViewModel(TaskModuleDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public TaskModuleDescriptor Descriptor { get; }
    public string ModuleId => Descriptor.ModuleId;
    public string DisplayName => Descriptor.DisplayName;
    public string Description => Descriptor.Description;
    public bool IsAvailable => Descriptor.Availability == TaskModuleAvailability.Available;

    public string KindLabel => Descriptor.Kind switch
    {
        TaskModuleKind.PrimaryMode => "主任务",
        TaskModuleKind.Auxiliary => "辅助任务",
        TaskModuleKind.DeveloperDiagnostic => "开发诊断",
        _ => Descriptor.Kind.ToString()
    };

    public string AvailabilityLabel => IsAvailable ? "可运行" : "待实现";
}