using Automation.Core.Configuration;

namespace Automation.Core.Runtime;

public sealed record TaskPlanIssue(
    string Code,
    string Message,
    string? ModuleId = null);

public sealed record ResolvedTaskPlan(
    string? PrimaryModuleId,
    IReadOnlyList<ITaskModule> Modules,
    IReadOnlyList<TaskModuleDescriptor> Descriptors);

public sealed record TaskPlanResolution(
    bool IsValid,
    ResolvedTaskPlan? Plan,
    IReadOnlyList<TaskPlanIssue> Issues);

public sealed class TaskPlanResolver
{
    private readonly ITaskModuleCatalog _catalog;

    public TaskPlanResolver(ITaskModuleCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public TaskPlanResolution Resolve(SessionTaskPlan taskPlan, UiProfile uiProfile)
    {
        ArgumentNullException.ThrowIfNull(taskPlan);

        var descriptors = _catalog.GetDescriptors()
            .ToDictionary(descriptor => descriptor.ModuleId, StringComparer.Ordinal);
        var plannedDescriptors = new List<TaskModuleDescriptor>();
        var issues = new List<TaskPlanIssue>();

        if (!string.IsNullOrWhiteSpace(taskPlan.PrimaryModuleId))
        {
            AddModule(
                taskPlan.PrimaryModuleId,
                TaskModuleKind.PrimaryMode,
                required: true);
        }

        foreach (var moduleId in taskPlan.AuxiliaryModuleIds)
        {
            AddModule(moduleId, TaskModuleKind.Auxiliary, required: true);
        }

        if (taskPlan.IncludeDeveloperDiagnostic)
        {
            AddModule("Diagnostic", TaskModuleKind.DeveloperDiagnostic, required: false);
        }

        if (issues.Count > 0)
        {
            return new TaskPlanResolution(false, null, issues);
        }

        var modules = plannedDescriptors
            .Select(descriptor => _catalog.Create(descriptor.ModuleId))
            .ToArray();

        return new TaskPlanResolution(
            true,
            new ResolvedTaskPlan(taskPlan.PrimaryModuleId, modules, plannedDescriptors),
            issues);

        void AddModule(string moduleId, TaskModuleKind expectedKind, bool required)
        {
            if (!descriptors.TryGetValue(moduleId, out var descriptor))
            {
                if (required)
                {
                    issues.Add(new TaskPlanIssue(
                        "task-plan.module.missing",
                        $"任务模块不存在：{moduleId}",
                        moduleId));
                }

                return;
            }

            if (descriptor.Kind != expectedKind)
            {
                issues.Add(new TaskPlanIssue(
                    "task-plan.module.kind",
                    $"{descriptor.DisplayName} 不能作为 {expectedKind} 使用。",
                    moduleId));
                return;
            }

            if (descriptor.Availability != TaskModuleAvailability.Available)
            {
                issues.Add(new TaskPlanIssue(
                    "task-plan.module.unavailable",
                    $"{descriptor.DisplayName} 尚未实现。",
                    moduleId));
                return;
            }

            if (!descriptor.Supports(uiProfile))
            {
                issues.Add(new TaskPlanIssue(
                    "task-plan.module.unsupported",
                    $"{descriptor.DisplayName} 不支持当前 UI 配置 {uiProfile}。",
                    moduleId));
                return;
            }

            if (plannedDescriptors.Any(item => item.ModuleId == moduleId))
            {
                issues.Add(new TaskPlanIssue(
                    "task-plan.module.duplicate",
                    $"任务模块重复：{moduleId}",
                    moduleId));
                return;
            }

            plannedDescriptors.Add(descriptor);
        }
    }
}