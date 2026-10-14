namespace Automation.Core.Runtime;

public sealed record SessionTaskPlan(
    string? PrimaryModuleId,
    IReadOnlyList<string> AuxiliaryModuleIds)
{
    public bool IncludeDeveloperDiagnostic { get; init; }

    public static SessionTaskPlan DiagnosticOnly { get; } = new(null, [])
    {
        IncludeDeveloperDiagnostic = true
    };
}