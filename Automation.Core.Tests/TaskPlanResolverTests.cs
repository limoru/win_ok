using Automation.Core.Configuration;
using Automation.Core.Runtime;
using Xunit;

namespace Automation.Core.Tests;

public sealed class TaskPlanResolverTests
{
    [Fact]
    public void ResolveUsesPrimaryThenAuxiliaryThenDiagnostic()
    {
        var catalog = new FakeCatalog(
        [
            Descriptor("primary", TaskModuleKind.PrimaryMode),
            Descriptor("auxiliary", TaskModuleKind.Auxiliary),
            Descriptor("Diagnostic", TaskModuleKind.DeveloperDiagnostic)
        ]);
        var resolver = new TaskPlanResolver(catalog);
        var plan = new SessionTaskPlan("primary", ["auxiliary"])
        {
            IncludeDeveloperDiagnostic = true
        };

        var result = resolver.Resolve(plan, UiProfile.WindowsPc);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Plan);
        Assert.Equal(
            ["primary", "auxiliary", "Diagnostic"],
            result.Plan.Modules.Select(module => module.ModuleId));
    }

    [Fact]
    public void ResolveRejectsPlannedModule()
    {
        var catalog = new FakeCatalog(
        [
            Descriptor(
                "ChaosMode",
                TaskModuleKind.PrimaryMode,
                TaskModuleAvailability.Planned)
        ]);
        var resolver = new TaskPlanResolver(catalog);

        var result = resolver.Resolve(
            new SessionTaskPlan("ChaosMode", []),
            UiProfile.WindowsPc);

        Assert.False(result.IsValid);
        Assert.Null(result.Plan);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "task-plan.module.unavailable");
    }

    [Fact]
    public void ResolveRejectsUnsupportedUiProfile()
    {
        var catalog = new FakeCatalog(
        [
            Descriptor(
                "primary",
                TaskModuleKind.PrimaryMode,
                supportedProfiles: [UiProfile.WindowsPc])
        ]);
        var resolver = new TaskPlanResolver(catalog);

        var result = resolver.Resolve(
            new SessionTaskPlan("primary", []),
            UiProfile.AndroidMobile);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "task-plan.module.unsupported");
    }

    private static TaskModuleDescriptor Descriptor(
        string moduleId,
        TaskModuleKind kind,
        TaskModuleAvailability availability = TaskModuleAvailability.Available,
        IReadOnlyList<UiProfile>? supportedProfiles = null)
        => new(
            moduleId,
            moduleId,
            moduleId,
            kind,
            availability,
            supportedProfiles ?? [UiProfile.WindowsPc, UiProfile.AndroidMobile],
            0,
            string.Empty,
            null);

    private sealed class FakeCatalog(IReadOnlyList<TaskModuleDescriptor> descriptors)
        : ITaskModuleCatalog
    {
        public IReadOnlyList<TaskModuleDescriptor> GetDescriptors() => descriptors;

        public ITaskModule Create(string moduleId)
            => new StubModule(moduleId);
    }

    private sealed class StubModule(string moduleId) : ITaskModule
    {
        public string ModuleId { get; } = moduleId;
        public string DisplayName => ModuleId;

        public ValueTask<bool> RunOnceAsync(
            TaskContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(false);
    }
}