using Automation.Core.Configuration;
using Automation.Core.Runtime;

namespace Kes.Tasks;

public sealed class TaskModuleCatalog : ITaskModuleCatalog
{
    public static TaskModuleCatalog Instance { get; } = new();

    private static readonly IReadOnlyList<TaskModuleDescriptor> Descriptors =
    [
        new TaskModuleDescriptor(
            "ChaosMode",
            "自动卡厄思模式",
            "卡厄思梦境正常流程可运行；刻印、面具和赛季专属逻辑暂缓。",
            TaskModuleKind.PrimaryMode,
            TaskModuleAvailability.Available,
            [UiProfile.WindowsPc],
            10,
            "ChaosModeConfig",
            "gameMode"),
        new TaskModuleDescriptor(
            "Diagnostic",
            "运行诊断",
            "检查抓帧序号、尺寸和像素格式，不操作游戏。",
            TaskModuleKind.DeveloperDiagnostic,
            TaskModuleAvailability.Available,
            [UiProfile.WindowsPc, UiProfile.AndroidMobile],
            1000,
            "",
            null)
    ];

    public IReadOnlyList<TaskModuleDescriptor> GetDescriptors() => Descriptors;

    public ITaskModule Create(string moduleId)
        => moduleId switch
        {
            "Diagnostic" => new DiagnosticTask(),
            "ChaosMode" => new ChaosModeTask(useFrameDispatchBus: true),
            _ => throw new ArgumentOutOfRangeException(
                nameof(moduleId),
                moduleId,
                "Unknown task module.")
        };
}