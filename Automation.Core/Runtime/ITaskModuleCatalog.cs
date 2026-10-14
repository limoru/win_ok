namespace Automation.Core.Runtime;

public interface ITaskModuleCatalog
{
    IReadOnlyList<TaskModuleDescriptor> GetDescriptors();

    ITaskModule Create(string moduleId);
}